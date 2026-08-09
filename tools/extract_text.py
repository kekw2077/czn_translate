#!/usr/bin/env python3
"""Pulls translatable text out of the game strings, with all markup masked away.

    python extract_text.py --source all_en.json --seed all_ru_clean.json --out-dir translate_out

Produces, in --out-dir:

    glossary_terms.txt   520 game terms, one per line — translate these first
    part_001.txt ...     the text itself, one segment per line, chunked for a web translator
    manifest.json        which line of which part is which segment (needed by apply_text.py)

Everything a translator sees is plain prose with ``[0]`` ``[1]`` where markup used to be. Send a
part off, save the result next to it as ``part_001.ru.txt`` with the same number of lines, and
run apply_text.py.

Re-running is incremental: anything already in the memory file is skipped, so after a game patch
this emits only what is genuinely new.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from czn.segment import collect_keywords, mask_string

DEFAULT_CHUNK_CHARS = 4500


def load_sources(path: Path) -> list[str]:
    payload = json.loads(path.read_text(encoding="utf-8"))

    if isinstance(payload, list):
        strings = [s for s in payload if isinstance(s, str)]
    elif isinstance(payload, dict):
        strings = [k for k in payload if isinstance(k, str)]
    else:
        raise ValueError(f"{path} must hold a JSON array of strings or an object keyed by them")

    return list(dict.fromkeys(s for s in strings if s.strip()))


def seed_memory(seed_path: Path, memory: dict[str, str]) -> tuple[int, int]:
    """Folds an existing en->ru mapping into the segment memory.

    Only entries that mask to a single marker-free segment are trusted. An entry that turns out
    to contain markup was translated by something that could not see the markup — exactly the
    case that produces "$Discard Pile$" rendered as "доллары США" — so it is dropped and the
    string is queued for a proper pass instead.
    """
    payload = json.loads(seed_path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"{seed_path} must be an object mapping English to Russian")

    accepted = rejected = 0
    for source, translation in payload.items():
        if not isinstance(source, str) or not isinstance(translation, str):
            continue

        masked = mask_string(source)
        if len(masked.segments) == 1 and not masked.segments[0].markers:
            memory[masked.segments[0].masked] = translation
            accepted += 1
        else:
            rejected += 1

    return accepted, rejected


def chunk(segments: list[str], limit: int) -> list[list[str]]:
    """Groups segments into parts that fit a web translator's input box."""
    parts: list[list[str]] = []
    current: list[str] = []
    size = 0

    for segment in segments:
        length = len(segment) + 1
        if current and size + length > limit:
            parts.append(current)
            current, size = [], 0
        current.append(segment)
        size += length

    if current:
        parts.append(current)

    return parts


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, type=Path, help="all_en.json or similar")
    parser.add_argument("--seed", type=Path, help="existing en->ru json to fold in as memory")
    parser.add_argument("--memory", type=Path, default=Path("segments_ru.json"))
    parser.add_argument("--out-dir", type=Path, default=Path("translate_out"))
    parser.add_argument("--chunk-chars", type=int, default=DEFAULT_CHUNK_CHARS)
    args = parser.parse_args(argv)

    if not args.source.exists():
        print(f"{args.source} does not exist", file=sys.stderr)
        return 1

    sources = load_sources(args.source)
    print(f"Source strings: {len(sources):,}")

    memory: dict[str, str] = {}
    if args.memory.exists():
        memory.update(json.loads(args.memory.read_text(encoding="utf-8")))
        print(f"Memory carries {len(memory):,} segment(s) already")

    if args.seed:
        if not args.seed.exists():
            print(f"{args.seed} does not exist", file=sys.stderr)
            return 1
        accepted, rejected = seed_memory(args.seed, memory)
        print(f"Seeded {accepted:,} segment(s) from {args.seed.name}")
        if rejected:
            print(f"  rejected {rejected:,} whose source turned out to contain markup — requeued")

    # Ordered by first appearance so the parts stay in a sensible reading order.
    needed: dict[str, int] = {}
    for source in sources:
        for segment in mask_string(source).segments:
            if segment.translatable and segment.masked not in memory:
                needed[segment.masked] = needed.get(segment.masked, 0) + 1

    # The seeded memory has to be persisted here. Leaving it in a local dict meant everything
    # folded in from --seed was recomputed as "needs translating" on the very next run.
    args.memory.parent.mkdir(parents=True, exist_ok=True)
    args.memory.write_text(json.dumps(memory, ensure_ascii=False, indent=2), encoding="utf-8")

    args.out_dir.mkdir(parents=True, exist_ok=True)

    glossary = collect_keywords(sources)
    glossary_path = args.out_dir / "glossary_terms.txt"
    glossary_path.write_text("\n".join(glossary) + "\n", encoding="utf-8")

    if not needed:
        print("\nNothing new to translate.")
        (args.out_dir / "manifest.json").write_text(
            json.dumps({"chunk_chars": args.chunk_chars, "parts": []}, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return 0

    parts = chunk(list(needed), args.chunk_chars)
    manifest = {"chunk_chars": args.chunk_chars, "parts": []}

    for index, part in enumerate(parts, start=1):
        name = f"part_{index:03d}.txt"
        (args.out_dir / name).write_text("\n".join(part) + "\n", encoding="utf-8")
        manifest["parts"].append({"file": name, "lines": len(part), "segments": part})

    (args.out_dir / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    total_chars = sum(len(s) for s in needed)
    print(f"\nSegments needing translation: {len(needed):,}  ({total_chars:,} characters)")
    print(f"Glossary terms:               {len(glossary):,}  -> {glossary_path}")
    print(f"Parts written:                {len(parts)}  -> {args.out_dir}/part_XXX.txt")
    print(f"Memory saved:                 {len(memory):,} segment(s) -> {args.memory}")
    print(
        "\nTranslate glossary_terms.txt first and save it as glossary_terms.ru.txt (same line\n"
        "count). Then each part_XXX.txt -> part_XXX.ru.txt, keeping one line per line.\n"
        "The [0] [1] markers must survive; apply_text.py reports any that do not."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
