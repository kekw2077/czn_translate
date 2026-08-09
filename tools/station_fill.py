#!/usr/bin/env python3
"""Fills czn.db straight from a translation station, in place.

    python station_fill.py --check --station station.json
    python station_fill.py --db czn.db --station station.json --work station_work
    python station_fill.py --db czn.db --station station.json --limit 200

This is the one command the desktop "Станция" tab runs. It reads the pending (new/stale) rows out
of czn.db, masks every string down to plain text with ``[0] [1]`` where the markup was, sends only
that to the station (Ollama over Tailscale, model qwen3:4b), puts the markup back, strips it to
display text, and writes the Russian onto the *same* rows as ``mt`` — so the coverage number moves
instead of a parallel set of rows appearing.

The station only ever sees ``[0]Deal [1] to all[2]``; a translation that dropped a marker is caught
by czn.station and never enters the memory, so game markup cannot be damaged even in principle.

stdlib only, on purpose: it imports just czn.segment and czn.station (both pure stdlib) so the
bundled embeddable Python needs no pip packages. It talks to sqlite3 directly rather than through
czn.db, whose import chain pulls in xxhash.
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
import time
from pathlib import Path

from czn.segment import collect_keywords, display_text, mask_string, rebuild
from czn.station import build_station

# Kept out of czn.db so this file stays stdlib-only; these are the same string values schema uses.
PENDING_STATUSES = ("new", "stale")
STATUS_MT = "mt"

DEFAULT_STATION = {
    "kind": "ollama",
    "endpoint": "http://127.0.0.1:11434",
    "model": "qwen3:4b",
    "batch": 25,
    "timeoutSeconds": 300,
    "retries": 2,
    "chunk": 200,
}


def load_station_settings(path: Path | None) -> dict:
    settings = dict(DEFAULT_STATION)
    if path and path.exists():
        settings.update(json.loads(path.read_text(encoding="utf-8")))
    return settings


def load_memory(path: Path) -> dict[str, str]:
    if path.exists():
        payload = json.loads(path.read_text(encoding="utf-8"))
        if isinstance(payload, dict):
            return {k: v for k, v in payload.items() if isinstance(k, str) and isinstance(v, str)}
    return {}


def save_memory(path: Path, memory: dict[str, str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(memory, ensure_ascii=False, indent=2), encoding="utf-8")


def read_pending(connection: sqlite3.Connection, limit: int | None) -> list[sqlite3.Row]:
    placeholders = ",".join("?" for _ in PENDING_STATUSES)
    sql = (
        f"SELECT id, key, en FROM strings "
        f"WHERE status IN ({placeholders}) AND en IS NOT NULL AND TRIM(en) <> '' "
        f"ORDER BY id"
    )
    if limit is not None:
        sql += f" LIMIT {int(limit)}"
    return list(connection.execute(sql, PENDING_STATUSES))


def pending_segments(sources: list[str], memory: dict[str, str]) -> list[str]:
    """Unique translatable masked segments across the rows that are not already known."""
    seen: dict[str, None] = {}
    for source in sources:
        for segment in mask_string(source).segments:
            if segment.translatable and segment.masked not in memory:
                seen.setdefault(segment.masked, None)
    return list(seen)


def pending_terms(sources: list[str], glossary: dict[str, str]) -> list[str]:
    """Keyword terms ($Shield$) not yet in the glossary — translated once, reused everywhere."""
    return [term for term in collect_keywords(sources) if term not in glossary]


def translate_into(
    station,
    todo: list[str],
    memory: dict[str, str],
    memory_path: Path,
    chunk: int,
    done_before: int,
    total: int,
    progress,
) -> tuple[int, list[str]]:
    """Feeds ``todo`` to the station in chunks, folding results into ``memory`` and flushing it.

    Returns the running done-count and whatever the station could not translate. Flushing per
    chunk means an interrupted run keeps everything it had already earned.
    """
    done = done_before
    rejected: list[str] = []

    for start in range(0, len(todo), max(1, chunk)):
        group = todo[start:start + max(1, chunk)]
        result = station.translate(group)
        memory.update(result.translations)
        rejected.extend(result.rejected)
        save_memory(memory_path, memory)

        done += len(group)
        progress(f"PROGRESS {done} {total}")

    return done, rejected


def apply_rows(
    connection: sqlite3.Connection,
    rows: list[sqlite3.Row],
    segments: dict[str, str],
    glossary: dict[str, str],
) -> tuple[int, int, int, list[dict]]:
    """Rebuilds each row's Russian from the memory and writes the covered ones as ``mt``.

    A row is only touched when at least one of its translatable segments came back, so a string the
    station could not manage is left ``new`` for the next pass rather than being marked done.

    Keywords (``$Shield$``) are resolved through ``glossary``: a translated term is substituted and
    ``display_text`` strips it to its inner word regardless of script (``$Щит$`` -> ``Щит``); a term
    with no translation falls back to English (``$Shield$`` -> ``Shield``).
    """
    written = partial = untouched = 0
    problems: list[dict] = []
    now = int(time.time())

    for row in rows:
        source = row["en"]
        masked = mask_string(source)
        translatable = masked.translatable_segments
        covered = sum(1 for s in translatable if s.masked in segments)

        if not translatable or covered == 0:
            untouched += 1
            continue

        text, issues = rebuild(masked, segments, glossary)
        ru = display_text(text)
        if issues:
            problems.append({"en": source, "ru": ru, "issues": issues})
        if not ru:
            untouched += 1
            continue

        connection.execute(
            "UPDATE strings SET ru = ?, status = ?, updated_at = ? WHERE id = ?",
            (ru, STATUS_MT, now, row["id"]),
        )
        written += 1
        if covered < len(translatable):
            partial += 1

    connection.commit()
    return written, partial, untouched, problems


def fill(
    connection: sqlite3.Connection,
    station,
    *,
    limit: int | None,
    work: Path,
    chunk: int,
    progress=print,
) -> dict:
    rows = read_pending(connection, limit)
    sources = [row["en"] for row in rows]
    progress(f"Pending rows: {len(rows):,}")

    seg_path = work / "segments_ru.json"
    glo_path = work / "glossary_ru.json"
    segments = load_memory(seg_path)
    glossary = load_memory(glo_path)

    todo_segments = pending_segments(sources, segments)
    todo_terms = pending_terms(sources, glossary)
    total = len(todo_segments) + len(todo_terms)
    progress(
        f"Memory: {len(segments):,} segment(s), {len(glossary):,} term(s). "
        f"To do: {len(todo_segments):,} segment(s) + {len(todo_terms):,} term(s)."
    )
    progress(f"PROGRESS 0 {max(total, 1)}")

    done = 0
    rejected: list[str] = []
    # Keywords first: they are plain words, so they translate fast and the segment pass can then
    # reuse them. A term that comes back empty or with a stray marker is left English.
    if todo_terms:
        done, term_rejected = translate_into(
            station, todo_terms, glossary, glo_path, chunk, done, total, progress
        )
        rejected.extend(term_rejected)
    if todo_segments:
        done, seg_rejected = translate_into(
            station, todo_segments, segments, seg_path, chunk, done, total, progress
        )
        rejected.extend(seg_rejected)

    progress(f"PROGRESS {max(total, 1)} {max(total, 1)}")

    written, partial, untouched, problems = apply_rows(connection, rows, segments, glossary)

    if rejected:
        (work / "station_failed.txt").write_text("\n".join(rejected) + "\n", encoding="utf-8")
    if problems:
        (work / "station_report.json").write_text(
            json.dumps(problems[:500], ensure_ascii=False, indent=2), encoding="utf-8"
        )

    stats = {
        "rows": len(rows),
        "written": written,
        "partial": partial,
        "untouched": untouched,
        "rejected": len(rejected),
        "segments": len(segments),
        "terms": len(glossary),
    }
    progress(
        f"Wrote {written:,} row(s) as '{STATUS_MT}' "
        f"({partial:,} partial, {untouched:,} left for later, {len(rejected):,} rejected)."
    )
    return stats


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", type=Path, default=Path("czn.db"))
    parser.add_argument("--station", type=Path, default=Path("station.json"))
    parser.add_argument("--work", type=Path, help="folder for segment/glossary memory (default: next to --db)")
    parser.add_argument("--limit", type=int, help="stop after this many pending rows")
    parser.add_argument("--check", action="store_true", help="test the connection and exit")
    args = parser.parse_args(argv)

    settings = load_station_settings(args.station)
    station = build_station(settings)
    print(f"Station: {station.describe()}", flush=True)

    ok, detail = station.check()
    print(f"  {'ok' if ok else 'FAIL'} — {detail}", flush=True)
    if args.check:
        return 0 if ok else 2
    if not ok:
        print("Station is not usable — fix the connection first.", file=sys.stderr)
        return 2

    if not args.db.exists():
        print(f"{args.db} does not exist", file=sys.stderr)
        return 1

    work = args.work or args.db.resolve().parent / "station_work"
    chunk = int(settings.get("chunk", DEFAULT_STATION["chunk"]))

    def progress(line: str) -> None:
        print(line, flush=True)

    connection = sqlite3.connect(args.db)
    connection.row_factory = sqlite3.Row
    connection.execute("PRAGMA busy_timeout = 3000")
    try:
        fill(connection, station, limit=args.limit, work=work, chunk=chunk, progress=progress)
    finally:
        connection.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
