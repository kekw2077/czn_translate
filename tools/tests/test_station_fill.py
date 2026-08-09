"""Filling czn.db straight from a station: masking, sentinel rejection, incrementality."""

import sqlite3

import pytest

import station_fill
from czn.db import STATUS_NEW, STATUS_STALE, Database
from czn.station import StationResult


class FakeStation:
    """A station stand-in: translates from a fixed map, rejects a named set, echoes the rest.

    It honours the real contract — anything in ``reject`` is reported as rejected and never returned
    as a translation — so station_fill sees exactly what a marker-losing answer would produce,
    without needing a live model.
    """

    def __init__(self, mapping=None, reject=(), reachable=True):
        self.mapping = dict(mapping or {})
        self.reject = set(reject)
        self.reachable = reachable
        self.seen: list[str] = []

    def describe(self):
        return "fake station"

    def check(self):
        return (self.reachable, "ok" if self.reachable else "unreachable")

    def translate(self, segments):
        self.seen.extend(segments)
        translations, rejected = {}, []
        for segment in segments:
            if segment in self.reject:
                rejected.append(segment)
            else:
                translations[segment] = self.mapping.get(segment, segment)
        return StationResult(translations, rejected)


def make_db(tmp_path, rows):
    """rows: list of (key, en, status). Returns the db path."""
    path = tmp_path / "czn.db"
    db = Database(path)
    db.ensure_created()
    with db.connect() as connection:
        for key, en, status in rows:
            db.upsert_string(connection, en=en, key=key, status=status)
    return path


def open_conn(path):
    connection = sqlite3.connect(path)
    connection.row_factory = sqlite3.Row
    return connection


def fetch(path, key):
    with open_conn(path) as connection:
        row = connection.execute("SELECT ru, status FROM strings WHERE key = ?", (key,)).fetchone()
    return (row["ru"], row["status"])


def test_fill_masks_translates_and_writes_display_text(tmp_path):
    path = make_db(tmp_path, [
        ("k1", "<#F9385D>Deal $Fixed Damage$ to all</>", STATUS_NEW),
        ("k2", "Hello world", STATUS_NEW),
        ("k3", "Attack", STATUS_STALE),
        ("k4", "{0}", STATUS_NEW),
    ])

    station = FakeStation(
        mapping={
            "[0]Deal [1] to all[2]": "[0]Наносит [1] всем[2]",
            "Fixed Damage": "Фикс. урон",  # the $Fixed Damage$ keyword, translated as a term
        },
        reject={"Hello world"},
    )
    # k3 comes from memory, not the station.
    (tmp_path / "work").mkdir()
    (tmp_path / "work" / "segments_ru.json").write_text('{"Attack": "Атака"}', encoding="utf-8")

    connection = open_conn(path)
    try:
        stats = station_fill.fill(
            connection, station, limit=None, work=tmp_path / "work", chunk=50, progress=lambda _: None
        )
    finally:
        connection.close()

    # k1: markup restored around the translation, then stripped for display; the $Fixed Damage$
    # keyword is translated as a term and its delimiters do not leak (display_text strips $Фикс…$).
    assert fetch(path, "k1") == ("Наносит Фикс. урон всем", "mt")
    # k2: its only segment was rejected, so it is left untouched for a later pass.
    assert fetch(path, "k2") == (None, "new")
    # k3: filled from the pre-seeded memory without the station being asked.
    assert fetch(path, "k3") == ("Атака", "mt")
    assert "Attack" not in station.seen
    # k4: pure placeholder, nothing translatable — untouched.
    assert fetch(path, "k4") == (None, "new")

    assert stats["written"] == 2
    assert stats["rejected"] == 1


def test_fill_is_incremental_via_the_sidecar(tmp_path):
    path = make_db(tmp_path, [("k1", "Continue", STATUS_NEW)])
    work = tmp_path / "work"

    first = FakeStation(mapping={"Continue": "Продолжить"})
    connection = open_conn(path)
    try:
        station_fill.fill(connection, first, limit=None, work=work, chunk=50, progress=lambda _: None)
    finally:
        connection.close()
    assert fetch(path, "k1") == ("Продолжить", "mt")
    assert first.seen == ["Continue"]

    # A second row with the same segment must reuse the sidecar, not ask the station again.
    with Database(path).connect() as c:
        Database(path).upsert_string(c, en="Continue", key="k2", status=STATUS_NEW)

    second = FakeStation(mapping={})
    connection = open_conn(path)
    try:
        station_fill.fill(connection, second, limit=None, work=work, chunk=50, progress=lambda _: None)
    finally:
        connection.close()
    assert fetch(path, "k2") == ("Продолжить", "mt")
    assert second.seen == []


def test_pending_segments_skips_known_and_untranslatable():
    sources = ["Deal {0} damage", "{0}", "Deal {0} damage"]
    todo = station_fill.pending_segments(sources, memory={})
    assert todo == ["Deal [0] damage"]  # deduped; the bare "{0}" row has nothing translatable

    already = station_fill.pending_segments(sources, memory={"Deal [0] damage": "x"})
    assert already == []


def test_check_only_returns_two_when_unreachable(tmp_path, monkeypatch):
    station = FakeStation(reachable=False)
    monkeypatch.setattr(station_fill, "build_station", lambda settings: station)
    code = station_fill.main(["--check", "--station", str(tmp_path / "nope.json")])
    assert code == 2
