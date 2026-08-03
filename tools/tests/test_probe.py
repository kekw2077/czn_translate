import json
import sqlite3

import pytest

from czn.probe import (
    HIGH_ENTROPY,
    extract_embedded,
    find_embedded_sqlite,
    identify,
    identify_file,
    shannon_entropy,
)


def make_database(path, rows=200):
    connection = sqlite3.connect(path)
    connection.execute("CREATE TABLE loc (key TEXT PRIMARY KEY, en TEXT)")
    connection.executemany(
        "INSERT INTO loc VALUES (?, ?)",
        [(f"card.{i}", f"Deal {i} damage to a random enemy") for i in range(rows)],
    )
    connection.commit()
    connection.close()
    return path


class TestIdentify:
    def test_sqlite(self, tmp_path):
        make_database(tmp_path / "master.db")
        assert identify_file(tmp_path / "master.db").kind == "sqlite3"

    @pytest.mark.parametrize(
        "payload,expected",
        [
            (b"PK\x03\x04rest", "zip"),
            (b"UnityFS\x00stuff", "unityfs"),
            (b"\x1f\x8b\x08\x00", "gzip"),
            (b"\x28\xb5\x2f\xfd\x00", "zstd"),
            (b"\x04\x22\x4d\x18\x00", "lz4"),
        ],
    )
    def test_magics(self, payload, expected):
        assert identify(payload).kind == expected

    def test_json(self):
        assert identify(json.dumps({"a": "b"}).encode()).kind == "json"

    def test_json_with_leading_whitespace(self):
        assert identify(b"\n\n  [1, 2, 3]").kind == "json"

    def test_csv(self):
        assert identify(b"key,en,ko\ncard.1,Attack,\xed\x8c\x8c").kind == "csv"

    def test_xml(self):
        assert identify(b"<?xml version='1.0'?><root/>").kind == "xml"

    def test_empty(self):
        assert identify(b"").kind == "empty"

    def test_random_bytes_are_reported_as_opaque(self):
        """An encrypted or compressed pack has to be called out, not silently parsed as binary."""
        import os

        guess = identify(os.urandom(64 * 1024))

        assert guess.kind == "opaque"
        assert guess.entropy >= HIGH_ENTROPY

    def test_low_entropy_binary_is_not_opaque(self):
        guess = identify(b"\x00\x01\x02" * 5000)

        assert guess.kind == "binary"
        assert guess.entropy < HIGH_ENTROPY

    def test_readability_flags(self):
        assert identify(b'{"a":1}').is_readable_now
        assert identify(b"PK\x03\x04").is_container
        assert not identify(b"PK\x03\x04").is_readable_now


class TestEntropy:
    def test_uniform_bytes_are_zero(self):
        assert shannon_entropy(b"\x00" * 1000) == 0.0

    def test_full_range_is_eight_bits(self):
        assert shannon_entropy(bytes(range(256))) == pytest.approx(8.0)

    def test_empty(self):
        assert shannon_entropy(b"") == 0.0


class TestCarving:
    def test_finds_a_loose_database(self, tmp_path):
        make_database(tmp_path / "master.db")

        found = find_embedded_sqlite(tmp_path / "master.db")

        assert len(found) == 1
        assert found[0].offset == 0
        assert found[0].size == (tmp_path / "master.db").stat().st_size

    def test_finds_a_database_inside_a_container(self, tmp_path):
        """A data.pack that is a plain concatenation gives its databases up without knowing the
        container format at all — worth trying before reverse-engineering the layout."""
        database = make_database(tmp_path / "inner.db").read_bytes()
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"HEADERJUNK" * 100 + database + b"TRAILING" * 50)

        found = find_embedded_sqlite(pack)

        assert len(found) == 1
        assert found[0].offset == 1000
        assert found[0].size == len(database)

    def test_finds_several(self, tmp_path):
        first = make_database(tmp_path / "a.db", rows=10).read_bytes()
        second = make_database(tmp_path / "b.db", rows=300).read_bytes()
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"\x00" * 64 + first + b"\xff" * 32 + second)

        found = find_embedded_sqlite(pack)

        assert len(found) == 2
        assert found[0].size == len(first)
        assert found[1].size == len(second)

    def test_the_magic_string_alone_is_not_enough(self, tmp_path):
        """Sixteen bytes of ASCII turn up inside compressed data and in any file that merely
        mentions SQLite; without the header checks carving would return mostly junk."""
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"junk" + b"SQLite format 3\x00" + b"\x00" * 200)

        assert find_embedded_sqlite(pack) == []

    def test_a_truncated_database_is_rejected(self, tmp_path):
        database = make_database(tmp_path / "inner.db", rows=300).read_bytes()
        pack = tmp_path / "cut.pack"
        pack.write_bytes(database[: len(database) // 2])

        # The page count in the header runs past the end of the file, so this is not something
        # to hand to sqlite3 and call a find.
        assert find_embedded_sqlite(pack) == []

    def test_an_extracted_database_actually_opens(self, tmp_path):
        database = make_database(tmp_path / "inner.db").read_bytes()
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"PADDING!" * 16 + database)

        found = find_embedded_sqlite(pack)
        target = extract_embedded(pack, found[0], tmp_path / "out" / "carved.db")

        connection = sqlite3.connect(target)
        try:
            names = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")]
            count = connection.execute("SELECT COUNT(*) FROM loc").fetchone()[0]
        finally:
            connection.close()

        assert names == ["loc"]
        assert count == 200

    def test_extraction_leaves_the_source_alone(self, tmp_path):
        database = make_database(tmp_path / "inner.db").read_bytes()
        pack = tmp_path / "data.pack"
        original = b"PAD" * 8 + database
        pack.write_bytes(original)

        extract_embedded(pack, find_embedded_sqlite(pack)[0], tmp_path / "carved.db")

        # Reading game files is only acceptable while it stays reading.
        assert pack.read_bytes() == original

    def test_a_file_with_no_database_returns_nothing(self, tmp_path):
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"\x00\x01\x02" * 10000)

        assert find_embedded_sqlite(pack) == []

    def test_a_tiny_file_is_handled(self, tmp_path):
        pack = tmp_path / "small.pack"
        pack.write_bytes(b"abc")

        assert find_embedded_sqlite(pack) == []


class TestProbeCli:
    """The CLI is the part a person actually touches, so its exit codes and its advice matter."""

    def test_scan_reports_an_embedded_database_without_being_asked(self, tmp_path, capsys):
        import probe_pack

        game = tmp_path / "game"
        game.mkdir()
        database = make_database(tmp_path / "inner.db").read_bytes()
        (game / "data.pack").write_bytes(b"\x00" * 4096 + database)

        assert probe_pack.main(["--path", str(game)]) == 0

        output = capsys.readouterr().out
        assert "embedded database" in output
        assert "--carve" in output

    def test_no_deep_skips_the_content_scan(self, tmp_path, capsys):
        import probe_pack

        game = tmp_path / "game"
        game.mkdir()
        database = make_database(tmp_path / "inner.db").read_bytes()
        (game / "data.pack").write_bytes(b"\x00" * 4096 + database)

        probe_pack.main(["--path", str(game), "--no-deep"])

        output = capsys.readouterr().out
        assert "embedded database" not in output
        assert "No SQLite database found" in output

    def test_a_loose_database_is_summarised_by_table(self, tmp_path, capsys):
        import probe_pack

        game = tmp_path / "game"
        game.mkdir()
        make_database(game / "master.db")

        assert probe_pack.main(["--path", str(game)]) == 0

        output = capsys.readouterr().out
        assert "imported directly" in output
        assert "loc (200)" in output

    def test_carve_writes_openable_databases(self, tmp_path, capsys):
        import probe_pack

        database = make_database(tmp_path / "inner.db").read_bytes()
        pack = tmp_path / "data.pack"
        pack.write_bytes(b"JUNK" * 64 + database)

        assert probe_pack.main(["--path", str(pack), "--carve", "--out", str(tmp_path / "carved")]) == 0

        carved = list((tmp_path / "carved").glob("*.db"))
        assert len(carved) == 1

        connection = sqlite3.connect(carved[0])
        try:
            assert connection.execute("SELECT COUNT(*) FROM loc").fetchone()[0] == 200
        finally:
            connection.close()

    def test_carve_with_nothing_to_find_exits_distinctly(self, tmp_path, capsys):
        import probe_pack

        pack = tmp_path / "data.pack"
        pack.write_bytes(b"\x01\x02\x03" * 5000)

        # 2, not 1: "the file is fine, there is just no database in it" is a different outcome
        # from "the arguments were wrong", and a script wrapping this needs to tell them apart.
        assert probe_pack.main(["--path", str(pack), "--carve", "--out", str(tmp_path / "carved")]) == 2
        assert "rules out a plain concatenation" in capsys.readouterr().out

    def test_a_missing_path_is_reported(self, tmp_path):
        import probe_pack

        assert probe_pack.main(["--path", str(tmp_path / "nope")]) == 1
