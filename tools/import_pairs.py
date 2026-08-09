#!/usr/bin/env python3
"""Imports a finished en->ru dictionary (all_ru.json) into the translation base.

    python import_pairs.py --pairs all_ru.json --db ../czn.db

This is the shortest route to a working overlay: the pairs already cover the game's text, so no
AssetRipper export and no database carving is needed for the strings themselves.

**Markup is stripped by default, and that is deliberate.** all_ru.json keeps the tags because
they matter for replacing strings inside the game. The overlay is a different consumer: it draws
the Russian with its own font, so a stored ``<#FFFBC9>`` would appear on screen as those literal
characters. OCR never sees the markup either — it reads the string the game already rendered. So
what belongs in the base is display text. Pass --keep-markup for the raw form.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from czn.db import SRC_MANUAL, SRC_OCR, SRC_PACK, STATUS_MT, STATUS_REVIEWED, Database
from czn.normalize import has_latin_letters, norm_hash, normalize
from czn.segment import display_text
from czn.validate import validate


def load_pairs(path: Path) -> dict[str, str]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"{path} must be an object mapping English to Russian")
    return {k: v for k, v in payload.items() if isinstance(k, str) and isinstance(v, str)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pairs", required=True, type=Path, help="all_ru.json")
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--status", default=STATUS_MT, choices=[STATUS_MT, STATUS_REVIEWED])
    parser.add_argument("--src", default=SRC_MANUAL, choices=[SRC_MANUAL, SRC_PACK, SRC_OCR])
    parser.add_argument("--keep-markup", action="store_true", help="store the raw strings, tags and all")
    parser.add_argument("--keep-identity", action="store_true",
                        help="also import rows whose Russian is byte-identical to the English")
    parser.add_argument("--report", type=Path, default=Path("import_pairs_report.json"))
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)

    if not args.pairs.exists():
        print(f"{args.pairs} does not exist", file=sys.stderr)
        return 1

    pairs = load_pairs(args.pairs)
    print(f"Pairs in {args.pairs.name}: {len(pairs):,}")

    prepare = (lambda s: s) if args.keep_markup else display_text

    rows: dict[int, tuple[str, str]] = {}
    skipped_identity = skipped_empty = collisions = 0
    collision_samples: list[dict] = []

    for english, russian in sorted(pairs.items()):
        en = prepare(english)
        ru = prepare(russian)

        norm = normalize(en)
        if not norm or not ru:
            skipped_empty += 1
            continue

        # A translation identical to its source is either a legitimate passthrough (a number, a
        # code) or a translator that gave up and echoed. The second kind would count as coverage
        # while showing English, which is worse than a clean miss.
        if en == ru and has_latin_letters(en) and not args.keep_identity:
            skipped_identity += 1
            continue

        key = norm_hash(norm)
        if key in rows:
            collisions += 1
            if len(collision_samples) < 20 and rows[key][1] != ru:
                collision_samples.append({"kept": rows[key], "dropped": [en, ru]})
            continue

        rows[key] = (en, ru)

    print(f"  usable rows        {len(rows):,}")
    print(f"  identical to en    {skipped_identity:,}   (use --keep-identity to import anyway)")
    print(f"  empty after strip  {skipped_empty:,}")
    print(f"  same normalized key{collisions:>7,}   first one wins")

    flagged = [
        {"en": en, "ru": ru, "problems": [f.problem.value for f in findings]}
        for en, ru in rows.values()
        if (findings := validate(en, ru))
    ]
    print(f"  validator findings {len(flagged):,}")

    args.report.write_text(
        json.dumps({"collisions": collision_samples, "flagged": flagged[:500]},
                   ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    if args.dry_run:
        print(f"\nDry run, nothing written. Report -> {args.report}")
        return 0

    database = Database(args.db)
    database.ensure_created()

    with database.connect() as connection:
        for key, (en, ru) in rows.items():
            # A synthetic key off the normalized hash makes re-runs idempotent: without one,
            # keyless rows insert afresh every time and the base grows a duplicate per import.
            database.upsert_string(
                connection,
                en=en,
                ru=ru,
                key=f"pairs:{key}",
                table_name=args.pairs.name,
                status=args.status,
                src=args.src,
            )

        database.rebuild_fts(connection)
        total = connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0]

    print(f"\n📦 {args.db} — {len(rows):,} row(s) imported, {total:,} in the base")
    print(f"   status='{args.status}' src='{args.src}'"
          f"{'' if args.keep_markup else ', markup stripped for display'}")
    print(f"   report -> {args.report}")

    if args.status == STATUS_MT:
        print("\n   These sit in the review queue. python review.py --db "
              f"{args.db} to work through them.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
