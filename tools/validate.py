#!/usr/bin/env python3
"""Checks every translation in the base and reports what needs a human (TZ §8).

    python validate.py --db ../czn.db
    python validate.py --db ../czn.db --json report.json
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

from czn.db import Database
from czn.validate import Problem, validate


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--json", type=Path, help="write the full report here")
    parser.add_argument("--status", nargs="*", default=["mt", "reviewed", "locked"])
    parser.add_argument("--show", type=int, default=20, help="how many findings to print")
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"{args.db} does not exist.", file=sys.stderr)
        return 1

    database = Database(args.db)
    counts: Counter[Problem] = Counter()
    report = []

    with database.connect() as connection:
        placeholders = ",".join("?" for _ in args.status)
        rows = connection.execute(
            f"""
            SELECT id, key, en, ru, status FROM strings
            WHERE ru IS NOT NULL AND ru <> '' AND status IN ({placeholders})
            ORDER BY id
            """,
            args.status,
        ).fetchall()

        for row in rows:
            findings = validate(row["en"], row["ru"])
            if not findings:
                continue

            for finding in findings:
                counts[finding.problem] += 1

            report.append(
                {
                    "id": row["id"],
                    "key": row["key"],
                    "en": row["en"],
                    "ru": row["ru"],
                    "status": row["status"],
                    "problems": [
                        {"problem": finding.problem.value, "detail": finding.detail}
                        for finding in findings
                    ],
                }
            )

    print(f"Checked {len(rows)} translated string(s); {len(report)} need attention.")
    for problem, count in counts.most_common():
        print(f"  {problem.value}: {count}")

    for entry in report[:args.show]:
        problems = ", ".join(item["problem"] for item in entry["problems"])
        print(f"\n  #{entry['id']} [{problems}] {entry['key'] or '(no key)'}")
        print(f"    en: {entry['en']}")
        print(f"    ru: {entry['ru']}")

    if args.json:
        args.json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"\nFull report written to {args.json}")

    # Non-zero so this can gate a release step; findings are advisory, not fatal.
    return 2 if report else 0


if __name__ == "__main__":
    raise SystemExit(main())
