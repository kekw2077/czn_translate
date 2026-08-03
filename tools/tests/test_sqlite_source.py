import sqlite3

import pytest

import import_dump
from czn.db import Database
from czn.dump import TableSpec, load_table_specs, read_dump, write_table_specs
from czn.sqlite_source import connect_readonly, read_table, scan_database

SENTENCES = [
    "Deal five points of damage to a random enemy",
    "Restore ten points of health to every ally",
    "Summon a skeletal warrior that guards you",
    "Draw two cards at the start of your turn",
    "Gain three shield whenever you take damage",
    "Inflict burn on all enemies for three turns",
    "Increase attack power of the whole party",
    "Remove one curse from the selected relic",
]


def wide_database(path, rows=None):
    """One row per string, one column per language — the most common master-data layout."""
    rows = rows or SENTENCES
    connection = sqlite3.connect(path)
    connection.execute("CREATE TABLE StringTable (key TEXT PRIMARY KEY, ko TEXT, en TEXT, ja TEXT)")
    connection.executemany(
        "INSERT INTO StringTable VALUES (?, ?, ?, ?)",
        [(f"card.{i}", f"한국어 {i}", text, f"日本語 {i}") for i, text in enumerate(rows)],
    )
    connection.execute("CREATE TABLE PrefabPaths (id INTEGER PRIMARY KEY, path TEXT)")
    connection.executemany(
        "INSERT INTO PrefabPaths VALUES (?, ?)",
        [(i, f"Assets/Resources/prefab_{i}.prefab") for i in range(20)],
    )
    connection.commit()
    connection.close()
    return path


def tall_database(path):
    """One row per string *and* language — the other layout seen in the wild."""
    connection = sqlite3.connect(path)
    connection.execute("CREATE TABLE Localization (id INTEGER PRIMARY KEY, key TEXT, lang TEXT, text TEXT)")

    rows = []
    identifier = 1
    for index, sentence in enumerate(SENTENCES):
        for lang, text in (("ko", f"한국어 {index}"), ("en", sentence), ("ja", f"日本語 {index}")):
            rows.append((identifier, f"card.{index}", lang, text))
            identifier += 1

    connection.executemany("INSERT INTO Localization VALUES (?, ?, ?, ?)", rows)
    connection.commit()
    connection.close()
    return path


class TestReadOnly:
    def test_the_connection_refuses_writes(self, tmp_path):
        """§0 draws the line at reading game files; a URI read-only connection makes that a
        property of the connection rather than a promise about the code above it."""
        wide_database(tmp_path / "master.db")

        with connect_readonly(tmp_path / "master.db") as connection, pytest.raises(sqlite3.OperationalError):
            connection.execute("UPDATE StringTable SET en = 'tampered'")

    def test_reading_leaves_the_file_byte_identical(self, tmp_path):
        path = wide_database(tmp_path / "master.db")
        before = path.read_bytes()

        read_table(path, "StringTable", "en", "key")

        assert path.read_bytes() == before


class TestWideLayout:
    def test_the_english_column_is_proposed(self, tmp_path):
        candidates = scan_database(wide_database(tmp_path / "master.db"))

        best = candidates[0]
        assert best.table == "StringTable"
        assert best.text_column == "en"
        assert best.include
        assert best.layout == "wide"

    def test_the_key_column_is_found(self, tmp_path):
        candidates = scan_database(wide_database(tmp_path / "master.db"))

        # Without a stable key every diff after a patch reports the whole table as changed.
        assert candidates[0].key_column == "key"

    def test_sibling_language_columns_are_not_proposed(self, tmp_path):
        """The table is called StringTable, so a table-level name hint would drag in the key
        column and the Korean and Japanese translations too. Importing those as source English
        poisons the base, and it is easy to miss in a long tables.yaml."""
        candidates = scan_database(wide_database(tmp_path / "master.db"))

        proposed = {(c.table, c.text_column) for c in candidates if c.include}

        assert ("StringTable", "en") in proposed
        assert ("StringTable", "ko") not in proposed
        assert ("StringTable", "ja") not in proposed
        assert ("StringTable", "key") not in proposed

    def test_a_technical_table_is_not_proposed(self, tmp_path):
        candidates = scan_database(wide_database(tmp_path / "master.db"))

        paths = next(c for c in candidates if c.table == "PrefabPaths")
        assert not paths.include

    def test_rejected_candidates_are_still_listed(self, tmp_path):
        candidates = scan_database(wide_database(tmp_path / "master.db"))

        assert any(not c.include for c in candidates)
        assert all(c.samples for c in candidates)

    def test_reading_yields_prefixed_keys(self, tmp_path):
        entries = read_table(wide_database(tmp_path / "master.db"), "StringTable", "en", "key")

        assert len(entries) == len(SENTENCES)
        assert entries[0] == ("StringTable.card.0", SENTENCES[0])

    def test_other_language_columns_are_not_the_source(self, tmp_path):
        """Importing Korean as the source English would poison the whole base."""
        entries = read_table(wide_database(tmp_path / "master.db"), "StringTable", "en", "key")

        assert all("한국어" not in text for _key, text in entries)


class TestTallLayout:
    def test_the_language_column_is_detected(self, tmp_path):
        candidates = scan_database(tall_database(tmp_path / "master.db"))

        best = candidates[0]
        assert best.table == "Localization"
        assert best.text_column == "text"
        assert best.lang_column == "lang"
        assert best.lang_value == "en"
        assert best.layout == "tall"

    def test_reading_filters_to_english(self, tmp_path):
        entries = read_table(
            tall_database(tmp_path / "master.db"),
            "Localization",
            "text",
            key_column="key",
            lang_column="lang",
            lang_value="en",
        )

        assert len(entries) == len(SENTENCES)
        assert all("한국어" not in text and "日本語" not in text for _key, text in entries)

    def test_the_ratio_is_computed_from_english_rows_only(self, tmp_path):
        """Two thirds of this table is Korean and Japanese; scoring the column as a whole would
        bury a perfectly good localization table under its own translations."""
        candidates = scan_database(tall_database(tmp_path / "master.db"))

        best = next(c for c in candidates if c.text_column == "text")
        assert best.sentence_ratio > 0.9


class TestKeylessTables:
    def test_rowid_is_the_fallback(self, tmp_path):
        path = tmp_path / "master.db"
        connection = sqlite3.connect(path)
        connection.execute("CREATE TABLE Lines (en TEXT)")
        connection.executemany("INSERT INTO Lines VALUES (?)", [(s,) for s in SENTENCES])
        connection.commit()
        connection.close()

        candidates = scan_database(path)
        best = next(c for c in candidates if c.text_column == "en")

        assert best.key_column is None
        assert "rowid" in best.reason

        entries = read_table(path, "Lines", "en")
        assert entries[0][0] == "Lines.1"

    def test_blank_and_null_values_are_skipped(self, tmp_path):
        path = tmp_path / "master.db"
        connection = sqlite3.connect(path)
        connection.execute("CREATE TABLE Lines (key TEXT PRIMARY KEY, en TEXT)")
        connection.executemany(
            "INSERT INTO Lines VALUES (?, ?)",
            [("a", "Alpha"), ("b", "   "), ("c", None), ("d", "Delta")],
        )
        connection.commit()
        connection.close()

        assert [key for key, _ in read_table(path, "Lines", "en", "key")] == ["Lines.a", "Lines.d"]


class TestSpecPlumbing:
    def test_read_dump_handles_a_sqlite_spec(self, tmp_path):
        wide_database(tmp_path / "master.db")

        entries = read_dump(
            tmp_path,
            [TableSpec(file="master.db", kind="sqlite", table="StringTable", text_column="en", key_column="key")],
        )

        assert entries["StringTable.card.0"][0] == SENTENCES[0]
        assert entries["StringTable.card.0"][1] == "master.db:StringTable.en"

    def test_json_and_sqlite_sources_merge(self, tmp_path):
        import json

        wide_database(tmp_path / "master.db")
        (tmp_path / "Loc.json").write_text(
            json.dumps({"e": [{"k": "ui.ok", "t": "OK"}]}), encoding="utf-8"
        )

        entries = read_dump(
            tmp_path,
            [
                TableSpec(file="Loc.json", path="e[].t", key_path="e[].k"),
                TableSpec(file="master.db", kind="sqlite", table="StringTable", text_column="en", key_column="key"),
            ],
        )

        assert entries["ui.ok"][0] == "OK"
        assert entries["StringTable.card.0"][0] == SENTENCES[0]

    def test_an_excluded_sqlite_spec_is_skipped(self, tmp_path):
        wide_database(tmp_path / "master.db")

        specs = [
            TableSpec(file="master.db", kind="sqlite", table="StringTable", text_column="en", include=False)
        ]

        assert read_dump(tmp_path, specs) == {}

    def test_an_incomplete_sqlite_spec_is_rejected_at_load(self, tmp_path):
        tables = tmp_path / "tables.yaml"
        tables.write_text(
            "tables:\n  - kind: sqlite\n    file: master.db\n    table: StringTable\n",
            encoding="utf-8",
        )

        with pytest.raises(ValueError, match="text_column"):
            load_table_specs(tables)

    def test_an_unknown_kind_is_rejected(self, tmp_path):
        tables = tmp_path / "tables.yaml"
        tables.write_text("tables:\n  - kind: parquet\n    file: x\n", encoding="utf-8")

        with pytest.raises(ValueError, match="parquet"):
            load_table_specs(tables)

    def test_specs_survive_a_write_and_read_round_trip(self, tmp_path):
        candidates = scan_database(wide_database(tmp_path / "master.db"))
        tables = tmp_path / "tables.yaml"

        write_table_specs(tables, [], candidates)
        specs = load_table_specs(tables)

        best = next(s for s in specs if s.include and s.text_column == "en")
        assert best.kind == "sqlite"
        assert best.table == "StringTable"
        assert best.key_column == "key"


class TestScanIntegration:
    def test_databases_are_found_by_magic_not_extension(self, tmp_path):
        """Games name master data .bytes or .dat; going by suffix would miss most of them."""
        wide_database(tmp_path / "masterdata.bytes")

        assert import_dump.find_databases(tmp_path) == [tmp_path / "masterdata.bytes"]

    def test_scan_writes_sqlite_tables_into_tables_yaml(self, tmp_path):
        wide_database(tmp_path / "master.db")
        tables = tmp_path / "tables.yaml"

        assert import_dump.main(["--scan", "--dump", str(tmp_path), "--tables", str(tables)]) == 0

        specs = load_table_specs(tables)
        assert any(s.kind == "sqlite" and s.text_column == "en" and s.include for s in specs)

    def test_a_full_import_from_a_database(self, tmp_path):
        wide_database(tmp_path / "master.db")
        tables = tmp_path / "tables.yaml"
        db_path = tmp_path / "czn.db"

        import_dump.main(["--scan", "--dump", str(tmp_path), "--tables", str(tables)])

        # Trim to the one table a human would have kept, then import for real.
        document = tables.read_text(encoding="utf-8")
        assert "StringTable" in document

        specs = [s for s in load_table_specs(tables) if s.text_column == "en" and s.table == "StringTable"]
        write_table_specs(tables, [], [])
        tables.write_text(
            "tables:\n"
            "  - kind: sqlite\n"
            f"    file: {specs[0].file}\n"
            f"    table: {specs[0].table}\n"
            f"    text_column: {specs[0].text_column}\n"
            f"    key_column: {specs[0].key_column}\n"
            "    include: true\n",
            encoding="utf-8",
        )

        assert import_dump.main(["--dump", str(tmp_path), "--tables", str(tables), "--db", str(db_path)]) == 0

        database = Database(db_path)
        with database.connect() as connection:
            row = database.get_by_key(connection, "StringTable.card.0")
            count = connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0]

        assert row.en == SENTENCES[0]
        assert row.src == "pack"
        assert count == len(SENTENCES)

    def test_scan_reports_nothing_usable_rather_than_crashing(self, tmp_path):
        (tmp_path / "noise.bin").write_bytes(b"\x00\x01\x02" * 5000)

        assert import_dump.main(["--scan", "--dump", str(tmp_path), "--tables", str(tmp_path / "t.yaml")]) == 1
