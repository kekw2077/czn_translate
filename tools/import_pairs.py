#!/usr/bin/env python3
"""Loads a flat ``{key: english}`` pairs file into the strings table (TZ §8).

    python import_pairs.py --pairs ../extracted/text/en.pairs.json --db ../czn.db \
        --pack "C:/.../cznlive/data.pack"

This is the sibling of ``import_dump.py``. That one walks an AssetRipper JSON export or loose
SQLite master data; this one takes the already-decoded localization map produced by
``extracted/scripts/db_decode.py`` — one JSON object of ``key -> English string``. Everything
downstream (translate.py, the desktop lookup) is identical; only the front door differs.

Run with the game closed. Idempotent: re-running upserts by key, so a re-rip after a patch
updates changed strings in place and leaves any human ``ru`` alone (``upsert_string`` keeps the
existing translation via COALESCE).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

from czn.db import SRC_PACK, STATUS_NEW, Database


def md5_of(path: Path) -> str:
    digest = hashlib.md5()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_pairs(path: Path) -> dict[str, str]:
    payload = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(payload, dict):
        raise ValueError(f"{path} is not a JSON object of key -> string")
    return payload


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pairs", required=True, type=Path, help="key->english JSON (from db_decode.py)")
    parser.add_argument("--db", type=Path, default=Path("czn.db"), help="target SQLite file")
    parser.add_argument("--table-name", default="text/en", help="stored in strings.table_name for provenance")
    parser.add_argument("--pack", type=Path, help="data.pack, hashed into pack_versions")
    parser.add_argument("--note", help="free-form note stored with the pack version")
    args = parser.parse_args(argv)

    if not args.pairs.is_file():
        print(f"{args.pairs} is not a file", file=sys.stderr)
        return 1

    pairs = load_pairs(args.pairs)
    # ``en`` is NOT NULL and an empty string is a useless row that still costs an FTS entry;
    # drop blanks up front rather than translating whitespace later.
    usable = {key: text for key, text in pairs.items() if isinstance(text, str) and text.strip()}
    dropped = len(pairs) - len(usable)

    database = Database(args.db)
    database.ensure_created()

    with database.connect() as connection:
        pack_md5 = md5_of(args.pack) if args.pack else "unknown"
        version = database.record_pack_version(connection, pack_md5, args.note)

        inserted = 0
        for key, english in sorted(usable.items()):
            database.upsert_string(
                connection,
                en=english,
                key=key,
                table_name=args.table_name,
                status=STATUS_NEW,
                src=SRC_PACK,
                pack_version=version,
            )
            inserted += 1

        # One rebuild at the end beats the per-row triggers churning through a bulk load.
        database.rebuild_fts(connection)

    print(f"Imported {inserted} strings from {args.pairs.name} as pack version {version}.")
    if dropped:
        print(f"Skipped {dropped} blank value(s).")
    if args.pack is None:
        print("No --pack given, so pack_md5 is 'unknown' and patch detection stays off until the next import.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
