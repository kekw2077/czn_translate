#!/usr/bin/env python3
"""Takes the translated text back, restores the markup and rebuilds the strings.

    python apply_text.py --source all_en.json --out-dir translate_out --output all_ru.json

Reads every ``part_XXX.ru.txt`` sitting next to its ``part_XXX.txt``, checks that the line count
still matches, folds the results into the segment memory, then reassembles the full strings with
their markup put back.

Nothing is taken on trust. A segment whose ``[0]`` markers did not all survive translation falls
back to its English and is listed in the report — losing a ``</>`` would leave a colour span open
and repaint everything after it, which is far worse than one line staying in English.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from czn.segment import mask_string, rebuild

from extract_text import load_sources


def read_lines(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8-sig")
    lines = text.split("\n")

    # A trailing newline is normal; anything else blank means the translator ate a line and the
    # alignment is already wrong, so it is left in place for the count check to catch.
    if lines and lines[-1] == "":
        lines.pop()

    return [line.rstrip("\r") for line in lines]


def load_glossary(out_dir: Path) -> dict[str, str]:
    source = out_dir / "glossary_terms.txt"
    target = out_dir / "glossary_terms.ru.txt"

    if not target.exists():
        return {}

    terms = read_lines(source)
    translated = read_lines(target)

    if len(terms) != len(translated):
        raise ValueError(
            f"glossary_terms.ru.txt has {len(translated)} lines but glossary_terms.txt has "
            f"{len(terms)} — the two must line up one to one"
        )

    return {
        term: value.strip()
        for term, value in zip(terms, translated)
        if value.strip() and value.strip() != term
    }


def collect_translations(out_dir: Path) -> tuple[dict[str, str], list[str]]:
    manifest_path = out_dir / "manifest.json"
    if not manifest_path.exists():
        raise FileNotFoundError(f"{manifest_path} not found — run extract_text.py first")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    translations: dict[str, str] = {}
    notes: list[str] = []
    pending: list[str] = []

    for part in manifest["parts"]:
        source_name = part["file"]
        target_path = out_dir / source_name.replace(".txt", ".ru.txt")

        if not target_path.exists():
            pending.append(target_path.name)
            continue

        translated = read_lines(target_path)
        segments = part["segments"]

        if len(translated) != len(segments):
            # Matching by position is the only thing that can work with a plain text file, so a
            # count mismatch has to stop this part rather than silently shift every line after
            # the point where the translator merged or dropped one.
            notes.append(
                f"{target_path.name}: {len(translated)} lines against {len(segments)} expected — "
                "skipped, retranslate this part keeping one line per line"
            )
            continue

        for segment, value in zip(segments, translated):
            if value.strip():
                translations[segment] = value

    if pending:
        # One line, not one per part: with 365 parts the useful warnings would scroll away.
        notes.append(f"{len(pending)} part(s) not translated yet, e.g. {', '.join(pending[:3])}")

    return translations, notes


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--out-dir", type=Path, default=Path("translate_out"))
    parser.add_argument("--memory", type=Path, default=Path("segments_ru.json"))
    parser.add_argument("--output", type=Path, default=Path("all_ru.json"))
    parser.add_argument("--report", type=Path, default=Path("apply_report.json"))
    args = parser.parse_args(argv)

    if not args.source.exists():
        print(f"{args.source} does not exist", file=sys.stderr)
        return 1

    memory: dict[str, str] = {}
    if args.memory.exists():
        memory.update(json.loads(args.memory.read_text(encoding="utf-8")))

    try:
        glossary = load_glossary(args.out_dir)
        fresh, notes = collect_translations(args.out_dir)
    except (ValueError, FileNotFoundError) as error:
        print(f"❌ {error}", file=sys.stderr)
        return 1

    for note in notes:
        print(f"  ⚠ {note}")

    memory.update(fresh)
    args.memory.write_text(json.dumps(memory, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\nMemory: +{len(fresh):,} new, {len(memory):,} total -> {args.memory}")
    print(f"Glossary: {len(glossary):,} translated term(s)")

    sources = load_sources(args.source)
    result: dict[str, str] = {}
    problems: list[dict] = []
    fully = partly = untouched = 0

    for source in sources:
        masked = mask_string(source)
        text, issues = rebuild(masked, memory, glossary)
        result[source] = text

        translatable = masked.translatable_segments
        covered = sum(1 for s in translatable if s.masked in memory)

        if issues:
            problems.append({"en": source, "ru": text, "issues": issues})

        if not translatable or covered == len(translatable):
            fully += 1
        elif covered:
            partly += 1
        else:
            untouched += 1

    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    args.report.write_text(json.dumps(problems, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"\n📦 {args.output} — {len(result):,} strings")
    print(f"   fully translated {fully:,} · partly {partly:,} · still English {untouched:,}")

    if problems:
        print(f"\n⚠ {len(problems):,} string(s) lost a marker and kept their English — see {args.report}")
        for entry in problems[:5]:
            print(f"   {entry['en'][:64]!r}")
            print(f"     {entry['issues'][0]}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
