#!/usr/bin/env python3
"""Feeds the pending segments to a translation station and stores what comes back.

    python translate_station.py --check
    python translate_station.py --source all_en.json
    python translate_station.py --source all_en.json --station station.json --limit 2000

The station only ever receives masked text — ``[0]Deal [1] to all[2]`` — so it has no game markup
to damage. Every result is checked against its source markers before it is kept; anything that
lost one is left for another pass rather than written into the memory.

Results land in the same ``segments_ru.json`` the manual file route uses, so the two can be mixed
freely: run the station over the bulk, then handle whatever it choked on by hand.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

from czn.segment import mask_string
from czn.station import build_station

from extract_text import load_sources

DEFAULT_STATION = {
    "kind": "ollama",
    "endpoint": "http://127.0.0.1:11434",
    "model": "qwen2.5:7b-instruct",
    "batch": 25,
    "timeoutSeconds": 300,
    "retries": 2,
}


def load_station_settings(path: Path | None) -> dict:
    settings = dict(DEFAULT_STATION)
    if path and path.exists():
        settings.update(json.loads(path.read_text(encoding="utf-8")))
    return settings


def pending_segments(sources: list[str], memory: dict[str, str]) -> list[str]:
    seen: dict[str, None] = {}
    for source in sources:
        for segment in mask_string(source).segments:
            if segment.translatable and segment.masked not in memory:
                seen.setdefault(segment.masked, None)
    return list(seen)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--source", type=Path, help="all_en.json or similar")
    parser.add_argument("--station", type=Path, default=Path("station.json"))
    parser.add_argument("--memory", type=Path, default=Path("segments_ru.json"))
    parser.add_argument("--failed", type=Path, default=Path("station_failed.txt"))
    parser.add_argument("--limit", type=int, help="stop after this many segments")
    parser.add_argument("--chunk", type=int, default=500, help="segments per memory flush")
    parser.add_argument("--check", action="store_true", help="test the connection and exit")
    args = parser.parse_args(argv)

    station = build_station(load_station_settings(args.station))
    print(f"Station: {station.describe()}")

    ok, detail = station.check()
    print(f"  {'✓' if ok else '✗'} {detail}")

    if args.check:
        return 0 if ok else 1
    if not ok:
        print("\nStation is not usable — fix the connection first, or run --check for detail.", file=sys.stderr)
        return 1

    if not args.source:
        print("--source is required unless --check is given", file=sys.stderr)
        return 1
    if not args.source.exists():
        print(f"{args.source} does not exist", file=sys.stderr)
        return 1

    memory: dict[str, str] = {}
    if args.memory.exists():
        memory.update(json.loads(args.memory.read_text(encoding="utf-8")))

    sources = load_sources(args.source)
    todo = pending_segments(sources, memory)
    if args.limit:
        todo = todo[:args.limit]

    print(f"\nMemory holds {len(memory):,} segment(s); {len(todo):,} still to do")
    if not todo:
        print("Nothing to translate.")
        return 0

    started = time.monotonic()
    rejected: list[str] = []
    done = 0

    # Flushed in chunks so an interrupted run keeps everything it had already earned.
    for start in range(0, len(todo), args.chunk):
        group = todo[start:start + args.chunk]
        result = station.translate(group)

        memory.update(result.translations)
        rejected.extend(result.rejected)
        done += len(group)

        args.memory.write_text(json.dumps(memory, ensure_ascii=False, indent=2), encoding="utf-8")

        elapsed = time.monotonic() - started
        rate = done / elapsed if elapsed > 0 else 0
        remaining = (len(todo) - done) / rate if rate > 0 else 0
        print(
            f"  {done / len(todo):5.1%}  {done:,}/{len(todo):,}  "
            f"kept {result.ok}/{len(group)}  {rate:.1f} seg/s  "
            f"ETA {int(remaining // 3600)}h{int((remaining % 3600) // 60):02d}m"
        )

    if rejected:
        args.failed.write_text("\n".join(rejected) + "\n", encoding="utf-8")

    print(f"\nMemory: {len(memory):,} segment(s) -> {args.memory}")
    if rejected:
        print(f"Could not translate {len(rejected):,} segment(s) -> {args.failed}")
        print("  These kept a marker wrong every time. Run again, or translate that file by hand.")

    print("\nNext: python apply_text.py --source ... to rebuild the strings with their markup.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
