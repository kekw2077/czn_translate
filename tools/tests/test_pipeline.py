"""diff_pack classification, the batch protocol, and translate.py end to end against a stub."""

import json

import pytest

import diff_pack
import translate
from czn.db import STATUS_MT, STATUS_NEW, STATUS_REVIEWED, STATUS_STALE, Database
from czn.ollama import (
    SYSTEM_PROMPT,
    BatchTranslationError,
    TranslationItem,
    build_prompt,
    chunk,
    parse_response,
    render_glossary,
)


class TestDiffClassification:
    def test_new_changed_removed_unchanged(self):
        current = {
            "a": ("Alpha", "T"),
            "b": ("Bravo changed", "T"),
            "d": ("Delta", "T"),
        }
        existing = {"a": "Alpha", "b": "Bravo", "c": "Charlie"}

        report = diff_pack.classify(current, existing)

        assert report.new == ["d"]
        assert report.changed == ["b"]
        assert report.removed == ["c"]
        assert report.unchanged == ["a"]

    def test_an_empty_base_makes_everything_new(self):
        report = diff_pack.classify({"a": ("Alpha", "T")}, {})
        assert report.new == ["a"]
        assert report.removed == []

    def test_an_empty_dump_makes_everything_removed(self):
        report = diff_pack.classify({}, {"a": "Alpha"})
        assert report.removed == ["a"]


class TestDiffApplication:
    @pytest.fixture
    def database(self, tmp_path):
        db = Database(tmp_path / "czn.db")
        db.ensure_created()
        with db.connect() as connection:
            db.record_pack_version(connection, "v1")
            db.upsert_string(connection, en="Alpha", ru="Альфа", key="a", status=STATUS_REVIEWED, pack_version=1)
            db.upsert_string(connection, en="Bravo", ru="Браво", key="b", status=STATUS_REVIEWED, pack_version=1)
            db.upsert_string(connection, en="Charlie", ru="Чарли", key="c", status=STATUS_REVIEWED, pack_version=1)
        return db

    def _apply(self, database, current):
        with database.connect() as connection:
            existing = {
                row["key"]: row["en"]
                for row in connection.execute("SELECT key, en FROM strings WHERE src = 'pack'")
            }
            report = diff_pack.classify(current, existing)
            version = database.record_pack_version(connection, "v2")
            diff_pack.apply(database, connection, current, report, version)
            return report, version

    def test_a_changed_string_goes_stale_but_keeps_its_translation(self, database):
        """The old ru is wrong for the new English, but it beats showing raw English until the
        re-translation lands — TZ §8."""
        current = {"a": ("Alpha", "T"), "b": ("Bravo Reforged", "T"), "c": ("Charlie", "T")}

        self._apply(database, current)

        with database.connect() as connection:
            row = database.get_by_key(connection, "b")
            assert row.status == STATUS_STALE
            assert row.en == "Bravo Reforged"
            assert row.ru == "Браво"

    def test_a_changed_string_gets_a_fresh_norm(self, database):
        from czn.normalize import normalize

        self._apply(database, {"b": ("Bravo Reforged", "T")})

        with database.connect() as connection:
            row = connection.execute("SELECT norm, norm_hash FROM strings WHERE key = 'b'").fetchone()
            assert row["norm"] == normalize("Bravo Reforged")

    def test_a_new_string_is_queued(self, database):
        self._apply(database, {"a": ("Alpha", "T"), "b": ("Bravo", "T"), "c": ("Charlie", "T"), "d": ("Delta", "T")})

        with database.connect() as connection:
            row = database.get_by_key(connection, "d")
            assert row.status == STATUS_NEW
            assert row.ru is None

    def test_a_removed_string_is_kept_for_rollback(self, database):
        _report, version = self._apply(database, {"a": ("Alpha", "T"), "b": ("Bravo", "T")})

        with database.connect() as connection:
            row = database.get_by_key(connection, "c")
            assert row is not None
            assert row.ru == "Чарли"

            # Its pack_version stays behind, which is what marks it absent from the current pack.
            assert row.pack_version < version

    def test_unchanged_strings_are_carried_forward(self, database):
        _report, version = self._apply(database, {"a": ("Alpha", "T"), "b": ("Bravo", "T"), "c": ("Charlie", "T")})

        with database.connect() as connection:
            row = database.get_by_key(connection, "a")
            assert row.status == STATUS_REVIEWED
            assert row.pack_version == version


class TestBatchProtocol:
    def test_parses_a_well_formed_reply(self):
        reply = json.dumps([{"id": 1, "ru": "Раз"}, {"id": 2, "ru": "Два"}])
        assert parse_response(reply, {1, 2}) == {1: "Раз", 2: "Два"}

    def test_unwraps_a_fenced_reply(self):
        reply = '```json\n[{"id": 1, "ru": "Раз"}]\n```'
        assert parse_response(reply, {1}) == {1: "Раз"}

    def test_a_partial_reply_fails_the_batch(self):
        """A silently dropped string is indistinguishable later from one left untranslated."""
        with pytest.raises(BatchTranslationError, match="missing"):
            parse_response(json.dumps([{"id": 1, "ru": "Раз"}]), {1, 2})

    def test_an_unrequested_id_fails_the_batch(self):
        with pytest.raises(BatchTranslationError, match="unrequested"):
            parse_response(json.dumps([{"id": 1, "ru": "Раз"}, {"id": 9, "ru": "Девять"}]), {1})

    @pytest.mark.parametrize(
        "reply",
        [
            "not json at all",
            json.dumps({"id": 1, "ru": "Раз"}),
            json.dumps([{"id": 1}]),
            json.dumps([{"ru": "Раз"}]),
            json.dumps([{"id": "abc", "ru": "Раз"}]),
            json.dumps([{"id": 1, "ru": 42}]),
        ],
    )
    def test_malformed_replies_fail_the_batch(self, reply):
        with pytest.raises(BatchTranslationError):
            parse_response(reply, {1})

    def test_batches_are_capped_at_forty(self):
        items = [TranslationItem(i, f"String {i}") for i in range(95)]
        batches = chunk(items)

        assert [len(batch) for batch in batches] == [40, 40, 15]

    def test_prompt_carries_id_and_english_only(self):
        payload = json.loads(build_prompt([TranslationItem(7, "Blood Pact")]))
        assert payload == [{"id": 7, "en": "Blood Pact"}]

    def test_glossary_rendering(self):
        assert render_glossary({"Boss": "Босс"}) == "- Boss = Босс"
        assert render_glossary({}) == "(пусто)"

    def test_system_prompt_formats_with_a_glossary(self):
        """The prompt is only built inside a real model call, which no stub exercises — so the
        literal {0}/{value} examples in its rules text must stay escaped or .format() blows up."""
        rendered = SYSTEM_PROMPT.format(glossary=render_glossary({"Boss": "Босс"}))
        assert "- Boss = Босс" in rendered
        # The placeholder examples survive as literals for the model to read.
        assert "{0}" in rendered and "{value}" in rendered


class StubClient:
    """Stands in for OllamaClient; records what it was asked for."""

    def __init__(self, translate_fn=None, fail=False):
        self.batches = []
        self._translate = translate_fn or (lambda en: f"[ru] {en}")
        self._fail = fail

    def translate_batch(self, items, glossary, attempts=2):
        self.batches.append(list(items))
        if self._fail:
            raise BatchTranslationError("stub failure")
        return {item.id: self._translate(item.en) for item in items}


class TestTranslateScript:
    @pytest.fixture
    def db_path(self, tmp_path):
        path = tmp_path / "czn.db"
        db = Database(path)
        db.ensure_created()
        return path

    def _run(self, monkeypatch, db_path, client, extra_args=()):
        monkeypatch.setattr(translate, "OllamaClient", lambda *a, **k: client)
        return translate.main(["--db", str(db_path), "--glossary", "/nonexistent.yaml", *extra_args])

    def test_translates_pending_strings(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(connection, en="Blood Pact", key="a")

        client = StubClient()
        assert self._run(monkeypatch, db_path, client) == 0

        with database.connect() as connection:
            row = database.get_by_key(connection, "a")
            assert row.ru == "[ru] Blood Pact"
            assert row.status == STATUS_MT

    def test_memory_short_circuits_the_model(self, monkeypatch, db_path):
        """20–40% of a gacha base is duplicates, so this is most of the saving, not a detail."""
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(
                connection, en="Blood Pact", ru="Кровавый пакт", key="a", status=STATUS_REVIEWED
            )
            database.upsert_string(connection, en="<b>Blood Pact</b>", key="b")

        client = StubClient()
        self._run(monkeypatch, db_path, client)

        assert client.batches == []
        with database.connect() as connection:
            row = database.get_by_key(connection, "b")
            assert row.ru == "Кровавый пакт"
            assert row.status == STATUS_REVIEWED

    def test_untranslatable_strings_are_passed_through(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(connection, en="12345", key="num")
            database.upsert_string(connection, en="{0}/{1}", key="fmt")

        client = StubClient()
        self._run(monkeypatch, db_path, client)

        assert client.batches == []
        with database.connect() as connection:
            assert database.get_by_key(connection, "num").ru == "12345"
            assert database.get_by_key(connection, "fmt").status == STATUS_REVIEWED

    def test_stale_strings_are_re_translated(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(
                connection, en="Bravo Reforged", ru="Браво", key="b", status=STATUS_STALE
            )

        self._run(monkeypatch, db_path, StubClient())

        with database.connect() as connection:
            row = database.get_by_key(connection, "b")
            assert row.ru == "[ru] Bravo Reforged"
            assert row.status == STATUS_MT

    def test_a_failed_batch_leaves_strings_for_the_next_run(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(connection, en="Blood Pact", key="a")

        assert self._run(monkeypatch, db_path, StubClient(fail=True)) == 1

        with database.connect() as connection:
            row = database.get_by_key(connection, "a")
            assert row.ru is None
            assert row.status == STATUS_NEW

    def test_dry_run_never_calls_the_model(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(connection, en="Blood Pact", key="a")

        client = StubClient()
        assert self._run(monkeypatch, db_path, client, ["--dry-run"]) == 0
        assert client.batches == []

    def test_a_flawed_translation_is_still_written_for_the_reviewer(self, monkeypatch, db_path):
        """A wrong translation with a note beside it is more useful than a hole in the base."""
        database = Database(db_path)
        with database.connect() as connection:
            database.upsert_string(connection, en="Deal {0} damage", key="a")

        # Drops the placeholder, which the validator flags.
        self._run(monkeypatch, db_path, StubClient(translate_fn=lambda en: "Наносит урон"))

        with database.connect() as connection:
            row = database.get_by_key(connection, "a")
            assert row.ru == "Наносит урон"
            assert row.status == STATUS_MT

    def test_limit_is_respected(self, monkeypatch, db_path):
        database = Database(db_path)
        with database.connect() as connection:
            for index in range(10):
                database.upsert_string(connection, en=f"Unique string number {index}", key=f"k{index}")

        client = StubClient()
        self._run(monkeypatch, db_path, client, ["--limit", "3"])

        assert sum(len(batch) for batch in client.batches) == 3


class TestPairsDiff:
    """diff_pack.py --pairs: the patch path that works off our decoded pairs.json, not a dump."""

    def test_load_pairs_matches_read_dump_shape(self, tmp_path):
        path = tmp_path / "en.pairs.json"
        path.write_text(json.dumps({"a": "Alpha", "b": "Bravo", "blank": "  "}), encoding="utf-8")

        current = diff_pack.load_pairs_as_current(path)

        assert current == {"a": ("Alpha", "text/en"), "b": ("Bravo", "text/en")}  # blank dropped

    def test_pairs_diff_marks_new_changed_and_stale(self, tmp_path):
        db_path = tmp_path / "czn.db"
        database = Database(db_path)
        database.ensure_created()
        with database.connect() as connection:
            database.record_pack_version(connection, "v1")
            database.upsert_string(connection, en="Alpha", ru="Альфа", key="a", status=STATUS_REVIEWED, pack_version=1)
            database.upsert_string(connection, en="Bravo", ru="Браво", key="b", status=STATUS_REVIEWED, pack_version=1)

        pairs = tmp_path / "en.pairs.json"
        pairs.write_text(json.dumps({"a": "Alpha", "b": "Bravo changed", "c": "Charlie"}), encoding="utf-8")

        assert diff_pack.main(["--pairs", str(pairs), "--db", str(db_path)]) == 0

        with database.connect() as connection:
            changed = database.get_by_key(connection, "b")
            added = database.get_by_key(connection, "c")
            unchanged = database.get_by_key(connection, "a")

        assert changed.status == STATUS_STALE and changed.ru == "Браво"  # patched, old ru kept
        assert added.status == STATUS_NEW and added.en == "Charlie"
        assert unchanged.status == STATUS_REVIEWED  # untouched

    def test_pairs_and_dump_are_mutually_exclusive(self, tmp_path, capsys):
        db_path = tmp_path / "czn.db"
        Database(db_path).ensure_created()
        assert diff_pack.main(["--db", str(db_path)]) == 1  # neither given
        assert "exactly one" in capsys.readouterr().err
