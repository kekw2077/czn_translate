#!/usr/bin/env python3
"""Batch translation through Ollama (TZ §8).

    python translate.py --db ../czn.db --limit 2000                       # local Ollama (default)
    python translate.py --db ../czn.db --provider anthropic --limit 30    # hosted API, small taste
    python translate.py --db ../czn.db --provider anthropic               # hosted API, full run

Order of operations per string: translation memory first, then the model. Gacha text repeats
20–40%, so the memory lookup is not an optimization detail — it is most of the work. On top of
that, identical English within one run is collapsed to a single model call and fanned back out,
which roughly halves a cold base (the memory only reuses human-reviewed rows, not this run's own
machine output).

For a hosted provider the API key is read from an environment variable (``ANTHROPIC_API_KEY`` or
``OPENAI_API_KEY`` by default), or from ``tools/.env`` which is gitignored — the key is never a
command-line argument.
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

import yaml

from czn.apiclient import ApiClient
from czn.db import STATUS_MT, STATUS_NEW, STATUS_REVIEWED, STATUS_STALE, Database
from czn.ollama import BatchTranslationError, OllamaClient, TranslationItem, chunk
from czn.segment import display_text, mask_string, rebuild
from czn.station import keeps_sentinels
from czn.validate import is_translatable, validate

DEFAULT_GLOSSARY = Path(__file__).resolve().parent / "glossary.yaml"
DEFAULT_ENV = Path(__file__).resolve().parent / ".env"

# Which env var holds the key for each hosted provider, unless --api-key-env overrides it.
KEY_ENV = {"anthropic": "ANTHROPIC_API_KEY", "openai": "OPENAI_API_KEY"}


def load_env_file(path: Path) -> None:
    """Populate os.environ from a gitignored KEY=VALUE file, without clobbering a real env var."""
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        name, value = line.split("=", 1)
        os.environ.setdefault(name.strip(), value.strip().strip('"').strip("'"))


def load_glossary(path: Path) -> dict[str, dict]:
    if not path.exists():
        return {}

    document = yaml.safe_load(path.read_text(encoding="utf-8")) or {}
    terms = document.get("terms", {})

    normalized: dict[str, dict] = {}
    for en, value in terms.items():
        normalized[en] = value if isinstance(value, dict) else {"ru": value}
    return normalized


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--glossary", type=Path, default=DEFAULT_GLOSSARY)
    parser.add_argument(
        "--provider",
        choices=("ollama", "anthropic", "openai"),
        default="ollama",
        help="local Ollama (default) or a hosted API",
    )
    parser.add_argument("--endpoint", default="http://127.0.0.1:11434", help="Ollama endpoint")
    parser.add_argument("--base-url", help="override the API base URL (openai-compatible hosts, self-hosted)")
    parser.add_argument("--api-key-env", help="env var holding the API key (default per provider)")
    parser.add_argument("--model", help="model name (defaults per provider)")
    parser.add_argument("--batch-size", type=int, default=40)
    parser.add_argument("--limit", type=int, help="stop after this many strings")
    parser.add_argument("--dry-run", action="store_true", help="use the memory only, never call the model")
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"{args.db} does not exist — run import_dump.py first.", file=sys.stderr)
        return 1

    glossary_entries = load_glossary(args.glossary)
    glossary = {en: value["ru"] for en, value in glossary_entries.items()}

    database = Database(args.db)

    if args.provider == "ollama":
        client = OllamaClient(args.endpoint, args.model or "qwen3:4b")
    else:
        load_env_file(DEFAULT_ENV)
        key_env = args.api_key_env or KEY_ENV[args.provider]
        api_key = os.environ.get(key_env, "")
        if not api_key:
            print(
                f"No API key: set {key_env} in the environment or in {DEFAULT_ENV}.",
                file=sys.stderr,
            )
            return 1
        client = ApiClient(args.provider, api_key, model=args.model, base_url=args.base_url)
        print(f"Provider {args.provider}, model {client.model}.")

    memory_hits = 0
    skipped = 0
    translated = 0
    flagged = 0
    failed_batches = 0

    with database.connect() as connection:
        if glossary_entries:
            database.replace_glossary(connection, glossary_entries)

        pending = list(database.iter_by_status(connection, (STATUS_NEW, STATUS_STALE), args.limit))
        print(f"{len(pending)} string(s) to translate.")

        remaining: dict[int, str] = {}  # row id -> English, for the rows the model must see

        for row in pending:
            if not is_translatable(row.en):
                # Codes, numbers and empty strings come back unchanged per §8; marking them
                # reviewed keeps them out of every later queue.
                database.set_translation(connection, row.id, row.en, STATUS_REVIEWED)
                skipped += 1
                continue

            reused = database.find_translation_memory(connection, row.norm)
            if reused is not None:
                # Identical normalized text already blessed by a human — same status, not 'mt'.
                database.set_translation(connection, row.id, reused, STATUS_REVIEWED)
                memory_hits += 1
                continue

            remaining[row.id] = row.en

        connection.commit()
        print(f"Memory covered {memory_hits}, {skipped} untranslatable, {len(remaining)} left for the model.")

        if args.dry_run:
            print("Dry run, the model was not called.")
            return 0

        # Mask the markup out of every string first, so the model only ever sees plain text with
        # [0] [1] where a colour tag or placeholder was — it cannot damage markup even in principle.
        # Identical masked segments across rows collapse to a single model call, the same saving the
        # old whole-string dedup gave, and are fanned back out at rebuild time.
        masked = {row_id: mask_string(en) for row_id, en in remaining.items()}

        segment_items: list[TranslationItem] = []
        seen_segments: dict[str, int] = {}
        for m in masked.values():
            for seg in m.segments:
                if seg.translatable and seg.masked not in seen_segments:
                    seen_segments[seg.masked] = len(segment_items) + 1
                    segment_items.append(TranslationItem(seen_segments[seg.masked], seg.masked))
        id_to_masked = {item.id: item.en for item in segment_items}
        if segment_items:
            print(f"{len(remaining)} row(s) -> {len(segment_items)} unique segment(s) for the model.")

        segments: dict[str, str] = {}
        for index, batch in enumerate(chunk(segment_items, args.batch_size), start=1):
            try:
                results = client.translate_batch(batch, glossary)
            except BatchTranslationError as error:
                # The whole batch is retried inside the client; if it still fails the segments are
                # left out, so their rows stay pending for the next run rather than half-written.
                failed_batches += 1
                print(f"  batch {index} failed: {error}", file=sys.stderr)
                continue

            for seg_id, russian in results.items():
                source = id_to_masked[seg_id]
                # A translation that lost a marker is dropped, not written — the same guarantee the
                # station makes. The segment stays unknown and its row falls back to English.
                if keeps_sentinels(source, russian):
                    segments[source] = russian
            print(f"  batch {index}: {len(results)} segment(s)")

        # Rebuild each row from the translated segments, put the markup back, then strip it to the
        # display text the overlay draws. Only rows with at least one translated segment are written;
        # keywords ($Shield$) stay as their English term (see station_fill for why).
        for row_id, m in masked.items():
            if not any(seg.masked in segments for seg in m.translatable_segments):
                continue
            # The curated glossary resolves keywords ($Shield$ -> $Щит$), and display_text strips
            # the delimiters either way; a term not in it falls back to English.
            text, _ = rebuild(m, segments, glossary)
            ru = display_text(text)
            if not ru:
                continue
            if validate(remaining[row_id], text):
                flagged += 1
            database.set_translation(connection, row_id, ru, STATUS_MT)
            translated += 1

        connection.commit()

    print(
        f"Done. memory {memory_hits}, model {translated}, untranslatable {skipped}, "
        f"flagged for review {flagged}, failed batches {failed_batches}."
    )
    if isinstance(client, ApiClient):
        usage = client.usage
        print(
            f"API usage: {usage.calls} call(s), "
            f"{usage.input_tokens:,} input + {usage.output_tokens:,} output tokens."
        )
    return 1 if failed_batches else 0


if __name__ == "__main__":
    raise SystemExit(main())
