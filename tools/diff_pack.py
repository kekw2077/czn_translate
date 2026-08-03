#!/usr/bin/env python3
"""Compares a fresh dump against the base and marks what the patch changed (TZ §8).

    new       -> imported as status='new', queued for translation
    changed   -> marked stale, the old ru is kept as a fallback, re-translated
    removed   -> left in place with its old pack_version, never deleted
    unchanged -> untouched

Nothing is deleted. A superseded string still has to render if the player rolls the client back,
and the review effort that went into it is not recoverable once it is gone.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass, field
from pathlib import Path

from czn.db import SRC_PACK, STATUS_NEW, STATUS_STALE, Database
from czn.dump import load_table_specs, read_dump
from czn.normalize import norm_hash, normalize
from import_dump import DEFAULT_TABLES, md5_of


@dataclass
class DiffReport:
    new: list[str] = field(default_factory=list)
    changed: list[str] = field(default_factory=list)
    removed: list[str] = field(default_factory=list)
    unchanged: list[str] = field(default_factory=list)

    def summary(self) -> str:
        return (
            f"new {len(self.new)}, changed {len(self.changed)}, "
            f"removed {len(self.removed)}, unchanged {len(self.unchanged)}"
        )


def classify(
    current: dict[str, tuple[str, str]],
    existing: dict[str, str],
) -> DiffReport:
    """Pure comparison of ``key -> english`` maps, so the rules are testable without a database."""
    report = DiffReport()

    for key, (english, _table) in current.items():
        if key not in existing:
            report.new.append(key)
        elif existing[key] != english:
            report.changed.append(key)
        else:
            report.unchanged.append(key)

    for key in existing:
        if key not in current:
            report.removed.append(key)

    for bucket in (report.new, report.changed, report.removed, report.unchanged):
        bucket.sort()

    return report


def apply(database: Database, connection, current: dict[str, tuple[str, str]], report: DiffReport, version: int) -> None:
    for key in report.new:
        english, table_name = current[key]
        database.upsert_string(
            connection,
            en=english,
            key=key,
            table_name=table_name,
            status=STATUS_NEW,
            src=SRC_PACK,
            pack_version=version,
        )

    for key in report.changed:
        english, table_name = current[key]
        norm = normalize(english)

        # ru is deliberately left alone: it is now wrong for the new English, but showing the
        # previous translation beats showing raw English until the re-translation lands.
        connection.execute(
            """
            UPDATE strings
            SET en = ?, table_name = ?, norm = ?, norm_hash = ?, status = ?, pack_version = ?
            WHERE key = ?
            """,
            (english, table_name, norm, norm_hash(norm), STATUS_STALE, version, key),
        )

    for key in report.unchanged:
        connection.execute("UPDATE strings SET pack_version = ? WHERE key = ?", (version, key))

    # Removed keys keep their old pack_version, which is what identifies them: a pack row whose
    # version is behind the current one was not present in the current pack.


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dump", required=True, type=Path, help="new AssetRipper export")
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--tables", type=Path, default=DEFAULT_TABLES)
    parser.add_argument("--pack", type=Path, help="new data.pack, hashed into pack_versions")
    parser.add_argument("--note", help="free-form note stored with the pack version")
    parser.add_argument("--dry-run", action="store_true", help="report only, change nothing")
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"{args.db} does not exist — run import_dump.py first.", file=sys.stderr)
        return 1

    specs = [spec for spec in load_table_specs(args.tables) if spec.include]
    current = read_dump(args.dump, specs)

    database = Database(args.db)
    with database.connect() as connection:
        existing = {
            row["key"]: row["en"]
            for row in connection.execute("SELECT key, en FROM strings WHERE key IS NOT NULL AND src = 'pack'")
        }

        report = classify(current, existing)
        print(f"Diff against the base: {report.summary()}")

        for key in report.changed[:10]:
            print(f"  changed: {key}")
        for key in report.removed[:10]:
            print(f"  removed: {key}")

        if args.dry_run:
            print("Dry run, nothing written.")
            return 0

        version = database.record_pack_version(
            connection,
            md5_of(args.pack) if args.pack else "unknown",
            args.note,
        )
        apply(database, connection, current, report, version)
        database.rebuild_fts(connection)

    print(f"Applied as pack version {version}. Run translate.py to fill new and stale strings.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
