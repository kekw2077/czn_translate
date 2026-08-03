#!/usr/bin/env python3
"""Works out what the game's data files actually are, before anything tries to parse them.

    python probe_pack.py --path "C:/Games/CZN"
    python probe_pack.py --path "C:/Games/CZN/data.pack" --carve --out ./carved

The conveyor in §8 assumes an AssetRipper JSON export, which only helps when the strings live in
Unity assets. If the localization is in a database instead, this finds it — including databases
concatenated into a container like data.pack, which come out verbatim without knowing the
container format at all.

Read-only, and run with the game closed. That is the same access §7 already sanctions for the
data.pack MD5 check, and it is the only contact with game files anywhere in this project.
"""

from __future__ import annotations

import argparse
import sqlite3
import sys
from pathlib import Path

from czn.probe import extract_embedded, find_embedded_sqlite, identify_file
from czn.sqlite_source import connect_readonly, list_tables

# Enough for every magic in the table, and cheap over a directory of thousands of files.
SNIFF_BYTES = 8192

# Below this a "database" is a stub, not master data.
INTERESTING_ROWS = 5


def human(size: int) -> str:
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.0f} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} GB"


def describe_database(path: Path) -> str:
    try:
        with connect_readonly(path) as connection:
            tables = list_tables(connection)
            summary = []
            for table in tables[:8]:
                try:
                    rows = connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0]
                except sqlite3.DatabaseError:
                    continue
                if rows >= INTERESTING_ROWS:
                    summary.append(f"{table} ({rows})")

            extra = "" if len(tables) <= 8 else f" +{len(tables) - 8} more"
            return f"{len(tables)} table(s): " + ", ".join(summary) + extra
    except sqlite3.DatabaseError as error:
        return f"unreadable as SQLite: {error}"


def scan_path(root: Path, min_size: int, deep: bool) -> int:
    files = [root] if root.is_file() else sorted(p for p in root.rglob("*") if p.is_file())

    databases: list[Path] = []
    containers: list[tuple[Path, str]] = []
    opaque: list[Path] = []
    embedded: list[tuple[Path, int]] = []
    counts: dict[str, int] = {}

    for path in files:
        size = path.stat().st_size
        if size < min_size:
            continue

        guess = identify_file(path, window=SNIFF_BYTES)
        counts[guess.kind] = counts.get(guess.kind, 0) + 1

        if guess.kind == "sqlite3":
            databases.append(path)
            continue

        if guess.is_container:
            containers.append((path, guess.description))
        elif guess.kind == "opaque" and size > 1 << 20:
            opaque.append(path)

        # The single most useful thing this command can report is "the localization is in there
        # after all", so the header scan runs as part of the normal sweep rather than only when
        # the user already suspects it. mmap + memchr makes it cheap even on a multi-gigabyte pack.
        if deep:
            try:
                hits = find_embedded_sqlite(path)
            except (OSError, ValueError):
                hits = []
            if hits:
                embedded.append((path, len(hits)))

    print(f"Scanned {len(files)} file(s) under {root}\n")
    print("By format:")
    for kind, count in sorted(counts.items(), key=lambda item: -item[1]):
        print(f"  {kind:10s} {count}")

    if databases:
        print("\nSQLite databases — these can be imported directly:")
        for path in databases:
            print(f"  {path}  [{human(path.stat().st_size)}]")
            print(f"    {describe_database(path)}")

    if containers:
        print("\nContainers — unpack these before the conveyor can see inside:")
        for path, description in containers[:20]:
            print(f"  {path}  [{human(path.stat().st_size)}] {description}")

    if embedded:
        print("\nFiles with SQLite databases inside them — carve these out:")
        for path, count in embedded:
            print(f"  {path}  [{count} embedded database(s)]")
            print(f"    python probe_pack.py --path \"{path}\" --carve")

    if opaque:
        print("\nHigh-entropy blobs — compressed or encrypted, and not readable as they are:")
        for path in opaque[:20]:
            print(f"  {path}  [{human(path.stat().st_size)}]")
        print("  Try --carve on these: a concatenated container still gives its databases up.")

    if not databases and not embedded:
        print(
            "\nNo SQLite database found, loose or embedded. Next steps, in order of effort:\n"
            "  1. python probe_pack.py --path <the big pack file> --carve\n"
            "  2. If that finds nothing, the strings are most likely in Unity assets — the\n"
            "     AssetRipper route from §8 is the right one.\n"
            "  3. If the pack is high-entropy throughout, it is packed or encrypted and neither\n"
            "     route works until it is unpacked."
        )

    return 0


def carve(path: Path, out_dir: Path) -> int:
    if not path.is_file():
        print(f"--carve expects a file, got {path}", file=sys.stderr)
        return 1

    found = find_embedded_sqlite(path)
    if not found:
        print(f"No embedded SQLite database in {path}.")
        print(
            "That rules out a plain concatenation. Either the container compresses its entries,\n"
            "or the strings are not in a database at all."
        )
        return 2

    print(f"Found {len(found)} embedded database(s) in {path}:\n")

    for index, database in enumerate(found):
        target = out_dir / f"{path.stem}-{index:02d}.db"
        extract_embedded(path, database, target)

        print(f"  offset {database.offset} · {human(database.size)} · "
              f"{database.page_count} pages of {database.page_size} B")
        print(f"    -> {target}")
        print(f"    {describe_database(target)}")

    print(
        "\nNext: python import_dump.py --scan --dump "
        f"{out_dir} to have the localization tables proposed into tables.yaml."
    )
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--path", required=True, type=Path, help="game directory or a single file")
    parser.add_argument("--carve", action="store_true", help="extract SQLite databases embedded in the file")
    parser.add_argument("--out", type=Path, default=Path("carved"), help="where carved databases go")
    parser.add_argument("--min-size", type=int, default=1024, help="ignore files smaller than this")
    parser.add_argument(
        "--no-deep",
        action="store_true",
        help="skip scanning file contents for embedded databases (headers only)",
    )
    args = parser.parse_args(argv)

    if not args.path.exists():
        print(f"{args.path} does not exist", file=sys.stderr)
        return 1

    return carve(args.path, args.out) if args.carve else scan_path(args.path, args.min_size, not args.no_deep)


if __name__ == "__main__":
    raise SystemExit(main())
