#!/usr/bin/env python3
"""Loads an AssetRipper dump into the strings table (TZ §8).

Run with the game closed.

    python import_dump.py --scan --dump ./dump          # propose tables.yaml, then edit it
    python import_dump.py --dump ./dump --db ../czn.db  # import what tables.yaml selects

The AssetRipper export itself is manual — the tool is GUI-only at present, so this script starts
from an already-exported folder.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

from czn.db import SRC_PACK, STATUS_NEW, Database
from czn.dump import load_table_specs, read_dump, write_table_specs
from czn.tables import scan_directory

DEFAULT_TABLES = Path(__file__).resolve().parent / "tables.yaml"


def md5_of(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_scan(dump_dir: Path, tables_path: Path) -> int:
    candidates = scan_directory(dump_dir)
    if not candidates:
        print(f"No JSON with string content found under {dump_dir}", file=sys.stderr)
        return 1

    write_table_specs(tables_path, candidates)

    included = sum(1 for candidate in candidates if candidate.include)
    print(f"Scanned {dump_dir}: {len(candidates)} candidate tables, {included} proposed for import.")
    print(f"Wrote {tables_path}. Review it by hand before importing.")

    for candidate in candidates[:15]:
        mark = "+" if candidate.include else "-"
        print(f"  {mark} {candidate.file}:{candidate.path} — {candidate.entries} entries, {candidate.reason}")

    return 0


def run_import(dump_dir: Path, tables_path: Path, db_path: Path, pack_path: Path | None, note: str | None) -> int:
    specs = load_table_specs(tables_path)
    included = [spec for spec in specs if spec.include]
    if not included:
        print(f"{tables_path} selects no tables (every include is false).", file=sys.stderr)
        return 1

    entries = read_dump(dump_dir, included)
    if not entries:
        print("Selected tables produced no strings.", file=sys.stderr)
        return 1

    database = Database(db_path)
    database.ensure_created()

    with database.connect() as connection:
        pack_md5 = md5_of(pack_path) if pack_path else "unknown"
        version = database.record_pack_version(connection, pack_md5, note)

        inserted = 0
        for key, (english, table_name) in sorted(entries.items()):
            database.upsert_string(
                connection,
                en=english,
                key=key,
                table_name=table_name,
                status=STATUS_NEW,
                src=SRC_PACK,
                pack_version=version,
            )
            inserted += 1

        # Rebuilding once beats letting the per-row triggers churn through a full import.
        database.rebuild_fts(connection)

    print(f"Imported {inserted} strings from {len(included)} table(s) as pack version {version}.")
    if pack_path is None:
        print("No --pack given, so pack_md5 is 'unknown' and patch detection stays off until the next import.")

    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dump", required=True, type=Path, help="AssetRipper export directory")
    parser.add_argument("--db", type=Path, default=Path("czn.db"), help="target SQLite file")
    parser.add_argument("--tables", type=Path, default=DEFAULT_TABLES, help="tables.yaml path")
    parser.add_argument("--pack", type=Path, help="data.pack, hashed into pack_versions")
    parser.add_argument("--note", help="free-form note stored with the pack version")
    parser.add_argument("--scan", action="store_true", help="only propose tables.yaml, import nothing")
    args = parser.parse_args(argv)

    if not args.dump.is_dir():
        print(f"{args.dump} is not a directory", file=sys.stderr)
        return 1

    if args.scan:
        return run_scan(args.dump, args.tables)

    return run_import(args.dump, args.tables, args.db, args.pack, args.note)


if __name__ == "__main__":
    raise SystemExit(main())
