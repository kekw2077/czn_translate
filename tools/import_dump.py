#!/usr/bin/env python3
"""Loads a dump into the strings table (TZ §8).

Run with the game closed.

    python import_dump.py --scan --dump ./dump          # propose tables.yaml, then edit it
    python import_dump.py --dump ./dump --db ../czn.db  # import what tables.yaml selects

Two kinds of source feed the same path: an AssetRipper JSON export, and SQLite databases sitting
in the same folder — loose game master data, or databases carved out of a pack by probe_pack.py.
The scan proposes both into tables.yaml and everything downstream is unaware of the difference.

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
from czn.probe import identify_file
from czn.sqlite_source import scan_database
from czn.tables import scan_directory

# Enough for every magic the prober knows, and cheap over a directory of thousands of files.
SNIFF_BYTES = 8192

DEFAULT_TABLES = Path(__file__).resolve().parent / "tables.yaml"


def md5_of(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def find_databases(dump_dir: Path) -> list[Path]:
    """Any file whose header says SQLite, whatever its extension.

    Games name master data .bytes, .dat or nothing at all, so going by suffix would miss most of
    them; the magic is the only reliable signal.
    """
    databases = []
    for path in sorted(p for p in dump_dir.rglob("*") if p.is_file()):
        if path.suffix.lower() == ".json":
            continue
        try:
            if identify_file(path, window=SNIFF_BYTES).kind == "sqlite3":
                databases.append(path)
        except OSError:
            continue

    return databases


def run_scan(dump_dir: Path, tables_path: Path) -> int:
    json_candidates = scan_directory(dump_dir)

    sqlite_candidates = []
    for database in find_databases(dump_dir):
        sqlite_candidates.extend(scan_database(database))

    if not json_candidates and not sqlite_candidates:
        print(
            f"No JSON string tables and no SQLite databases found under {dump_dir}.\n"
            "Run probe_pack.py against the game directory to see what the files actually are.",
            file=sys.stderr,
        )
        return 1

    write_table_specs(tables_path, json_candidates, sqlite_candidates)

    total = len(json_candidates) + len(sqlite_candidates)
    included = sum(1 for c in json_candidates if c.include) + sum(1 for c in sqlite_candidates if c.include)
    print(f"Scanned {dump_dir}: {total} candidate table(s), {included} proposed for import.")
    print(f"Wrote {tables_path}. Review it by hand before importing.")

    for candidate in json_candidates[:12]:
        mark = "+" if candidate.include else "-"
        print(f"  {mark} json   {candidate.file}:{candidate.path} — {candidate.entries} entries, {candidate.reason}")

    for candidate in sqlite_candidates[:12]:
        mark = "+" if candidate.include else "-"
        print(
            f"  {mark} sqlite {candidate.file}:{candidate.table}.{candidate.text_column} — "
            f"{candidate.rows} rows, {candidate.reason}"
        )

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
