import pytest

from czn.db import (
    SRC_OCR,
    SRC_PACK,
    STATUS_LOCKED,
    STATUS_MT,
    STATUS_NEW,
    STATUS_REVIEWED,
    Database,
)
from czn.normalize import norm_hash, normalize


@pytest.fixture
def database(tmp_path):
    db = Database(tmp_path / "czn.db")
    db.ensure_created()
    return db


def test_schema_creation_is_idempotent(database):
    database.ensure_created()


def test_upsert_replaces_a_row_with_the_same_key(database):
    with database.connect() as connection:
        first = database.upsert_string(connection, en="Blood Pact", key="card.bp")
        second = database.upsert_string(connection, en="Blood Contract", key="card.bp")

        assert first == second
        assert database.get_by_key(connection, "card.bp").en == "Blood Contract"
        assert connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0] == 1


def test_upsert_keeps_an_existing_translation_when_none_is_supplied(database):
    with database.connect() as connection:
        database.upsert_string(connection, en="Blood Pact", ru="Кровавый пакт", key="card.bp")
        database.upsert_string(connection, en="Blood Pact", key="card.bp")

        assert database.get_by_key(connection, "card.bp").ru == "Кровавый пакт"


def test_keyless_rows_always_insert(database):
    """OCR-discovered strings have no key, and the unique index is partial for exactly that."""
    with database.connect() as connection:
        first = database.upsert_string(connection, en="Ambient text", src=SRC_OCR)
        second = database.upsert_string(connection, en="Ambient text", src=SRC_OCR)

        assert first != second


def test_norm_and_hash_are_written(database):
    with database.connect() as connection:
        database.upsert_string(connection, en="<b>Blood Pact</b>", key="card.bp")
        row = connection.execute("SELECT norm, norm_hash FROM strings").fetchone()

        assert row["norm"] == normalize("<b>Blood Pact</b>")
        assert row["norm_hash"] == norm_hash(row["norm"])


def test_fts_index_is_populated_by_the_triggers(database):
    with database.connect() as connection:
        database.upsert_string(connection, en="Summon a skeletal warrior", key="s1")

        # The index holds normalized text, so "skeletal" is stored folded as "5ke1eta1".
        hits = connection.execute(
            "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"5ke\"'"
        ).fetchone()[0]

        assert hits > 0


def test_updating_a_string_keeps_the_index_in_step(database):
    with database.connect() as connection:
        database.upsert_string(connection, en="Summon a skeletal warrior", key="s1")
        database.upsert_string(connection, en="Summon a spectral archer", key="s1")

        stale = connection.execute(
            "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"5ke\"'"
        ).fetchone()[0]
        fresh = connection.execute(
            "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"arc\"'"
        ).fetchone()[0]

        assert stale == 0
        assert fresh > 0


def test_translation_memory_reuses_reviewed_text(database):
    with database.connect() as connection:
        database.upsert_string(
            connection, en="Blood Pact", ru="Кровавый пакт", key="a", status=STATUS_REVIEWED
        )

        assert database.find_translation_memory(connection, normalize("Blood Pact")) == "Кровавый пакт"


def test_translation_memory_ignores_machine_output(database):
    """Reusing another machine guess would multiply it across the base instead of catching it."""
    with database.connect() as connection:
        database.upsert_string(connection, en="Blood Pact", ru="Пакт крови", key="a", status=STATUS_MT)

        assert database.find_translation_memory(connection, normalize("Blood Pact")) is None


def test_translation_memory_prefers_a_locked_entry(database):
    with database.connect() as connection:
        database.upsert_string(connection, en="Boss", ru="Главарь", key="a", status=STATUS_REVIEWED)
        database.upsert_string(connection, en="Boss", ru="Босс", key="b", status=STATUS_LOCKED)

        assert database.find_translation_memory(connection, normalize("Boss")) == "Босс"


def test_translation_memory_matches_across_markup_differences(database):
    with database.connect() as connection:
        database.upsert_string(
            connection,
            en="<color=#f00>Blood Pact</color>",
            ru="Кровавый пакт",
            key="a",
            status=STATUS_REVIEWED,
        )

        assert database.find_translation_memory(connection, normalize("Blood Pact")) == "Кровавый пакт"


def test_pack_versions_increment(database):
    with database.connect() as connection:
        assert database.latest_pack_version(connection) is None

        first = database.record_pack_version(connection, "aaa", "initial")
        second = database.record_pack_version(connection, "bbb", "patch 1")

        assert (first, second) == (1, 2)

        latest = database.latest_pack_version(connection)
        assert latest["version"] == 2
        assert latest["pack_md5"] == "bbb"


def test_iter_by_status_filters_and_limits(database):
    with database.connect() as connection:
        for index in range(5):
            database.upsert_string(connection, en=f"String {index}", key=f"k{index}", status=STATUS_NEW)
        database.upsert_string(connection, en="Done", key="done", ru="Готово", status=STATUS_REVIEWED)

        pending = list(database.iter_by_status(connection, (STATUS_NEW,)))
        assert len(pending) == 5

        assert len(list(database.iter_by_status(connection, (STATUS_NEW,), limit=2))) == 2


def test_glossary_round_trip(database):
    with database.connect() as connection:
        database.replace_glossary(
            connection,
            {"Boss": {"ru": "Босс", "locked": True}, "Relic": {"ru": "Реликвия"}},
        )

        assert database.load_glossary(connection) == {"Boss": "Босс", "Relic": "Реликвия"}


def test_replacing_the_glossary_drops_removed_terms(database):
    with database.connect() as connection:
        database.replace_glossary(connection, {"Old": {"ru": "Старое"}})
        database.replace_glossary(connection, {"New": {"ru": "Новое"}})

        assert database.load_glossary(connection) == {"New": "Новое"}


def test_deleting_a_string_removes_its_correction(database):
    with database.connect() as connection:
        string_id = database.upsert_string(connection, en="Blood Pact", key="a", src=SRC_PACK)
        connection.execute(
            "INSERT INTO ocr_corrections (raw_norm, string_id) VALUES (?, ?)", ("8l00d pact", string_id)
        )

        connection.execute("DELETE FROM strings WHERE id = ?", (string_id,))

        # Without the cascade a correction would point at a missing row and every lookup that
        # hit it would return nothing while reporting a hit.
        assert connection.execute("SELECT COUNT(*) FROM ocr_corrections").fetchone()[0] == 0
