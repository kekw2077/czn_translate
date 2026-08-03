#!/usr/bin/env python3
"""Batch translation through Ollama (TZ §8).

    python translate.py --db ../czn.db --limit 2000

Order of operations per string: translation memory first, then the model. Gacha text repeats
20–40%, so the memory lookup is not an optimization detail — it is most of the work.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

import yaml

from czn.db import STATUS_MT, STATUS_NEW, STATUS_REVIEWED, STATUS_STALE, Database
from czn.ollama import BatchTranslationError, OllamaClient, TranslationItem, chunk
from czn.validate import is_translatable, validate

DEFAULT_GLOSSARY = Path(__file__).resolve().parent / "glossary.yaml"


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
    parser.add_argument("--endpoint", default="http://127.0.0.1:11434")
    parser.add_argument("--model", default="qwen3-loc")
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
    client = OllamaClient(args.endpoint, args.model)

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

        remaining: list[TranslationItem] = []
        originals: dict[int, str] = {}

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

            remaining.append(TranslationItem(row.id, row.en))
            originals[row.id] = row.en

        connection.commit()
        print(f"Memory covered {memory_hits}, {skipped} untranslatable, {len(remaining)} left for the model.")

        if args.dry_run:
            print("Dry run, the model was not called.")
            return 0

        for index, batch in enumerate(chunk(remaining, args.batch_size), start=1):
            try:
                results = client.translate_batch(batch, glossary)
            except BatchTranslationError as error:
                # The whole batch is retried inside the client; if it still fails the strings are
                # left as-is so the next run picks them up rather than writing partial output.
                failed_batches += 1
                print(f"  batch {index} failed: {error}", file=sys.stderr)
                continue

            for string_id, russian in results.items():
                findings = validate(originals[string_id], russian)
                if findings:
                    flagged += 1

                # Everything the model produced lands as 'mt', which is the review queue.
                # A failed check does not block the write — a flawed translation with a note
                # beside it is more useful to a reviewer than a hole.
                database.set_translation(connection, string_id, russian, STATUS_MT)

            connection.commit()
            translated += len(results)
            print(f"  batch {index}: {len(results)} translated")

    print(
        f"Done. memory {memory_hits}, model {translated}, untranslatable {skipped}, "
        f"flagged for review {flagged}, failed batches {failed_batches}."
    )
    return 1 if failed_batches else 0


if __name__ == "__main__":
    raise SystemExit(main())
