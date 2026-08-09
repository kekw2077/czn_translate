"""Importing a finished en->ru dictionary into the base."""

import json

import pytest

import import_pairs
from czn.db import Database
from czn.normalize import normalize


def write_pairs(tmp_path, pairs):
    path = tmp_path / "all_ru.json"
    path.write_text(json.dumps(pairs, ensure_ascii=False), encoding="utf-8")
    return path


def run(tmp_path, pairs, *extra):
    source = write_pairs(tmp_path, pairs)
    db = tmp_path / "czn.db"
    code = import_pairs.main([
        "--pairs", str(source), "--db", str(db),
        "--report", str(tmp_path / "report.json"), *extra,
    ])
    return code, Database(db)


class TestDisplayText:
    @pytest.mark.parametrize(
        "raw,expected",
        [
            ("<#FFFBC9>Authorization confirmed.</>", "Authorization confirmed."),
            ("#basic_ev_0# Deal damage", "Deal damage"),
            ("[shake]Boo[/shake]", "Boo"),
            ("Move to the $Discard Pile$", "Move to the Discard Pile"),
            ("Deal {0} damage", "Deal {0} damage"),
            ("Restores %s health", "Restores %s health"),
            ("First<br>Second", "First\nSecond"),
            ("   <#D72144> \"...\"", '"..."'),
        ],
    )
    def test_markup_is_reduced_to_what_a_player_sees(self, raw, expected):
        assert import_pairs.display_text(raw) == expected

    def test_a_keyword_keeps_its_word(self):
        # $Shield$ renders as the word Shield with styling, so the word is the display text.
        assert import_pairs.display_text("Gain $Shield$") == "Gain Shield"

    def test_collapsing_never_glues_words(self):
        assert import_pairs.display_text("A<#F00>B") == "AB"
        assert import_pairs.display_text("A <#F00> B") == "A B"


class TestImport:
    def test_rows_land_in_the_base(self, tmp_path):
        code, database = run(tmp_path, {"Blood Pact": "Кровавый пакт"})

        assert code == 0
        with database.connect() as connection:
            row = connection.execute("SELECT en, ru, status, src FROM strings").fetchone()

        assert (row["en"], row["ru"]) == ("Blood Pact", "Кровавый пакт")
        assert row["status"] == "mt"
        assert row["src"] == "manual"

    def test_markup_is_stripped_by_default(self, tmp_path):
        """The overlay draws ru with its own font — a stored tag would appear on screen."""
        _code, database = run(tmp_path, {"<#F00>Blood Pact</>": "<#F00>Кровавый пакт</>"})

        with database.connect() as connection:
            row = connection.execute("SELECT en, ru FROM strings").fetchone()

        assert row["en"] == "Blood Pact"
        assert row["ru"] == "Кровавый пакт"

    def test_keep_markup_stores_the_raw_form(self, tmp_path):
        _code, database = run(tmp_path, {"<#F00>Blood Pact</>": "<#F00>Кровавый пакт</>"},
                              "--keep-markup")

        with database.connect() as connection:
            row = connection.execute("SELECT ru FROM strings").fetchone()

        assert row["ru"] == "<#F00>Кровавый пакт</>"

    def test_importing_twice_does_not_duplicate(self, tmp_path):
        """Keyless rows insert afresh every time; a synthetic key off the norm makes it idempotent."""
        source = write_pairs(tmp_path, {"Blood Pact": "Кровавый пакт"})
        db = tmp_path / "czn.db"
        argv = ["--pairs", str(source), "--db", str(db), "--report", str(tmp_path / "r.json")]

        import_pairs.main(argv)
        import_pairs.main(argv)

        with Database(db).connect() as connection:
            assert connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0] == 1

    def test_a_re_import_updates_the_translation(self, tmp_path):
        source = write_pairs(tmp_path, {"Blood Pact": "Кровавый пакт"})
        db = tmp_path / "czn.db"
        report = str(tmp_path / "r.json")
        import_pairs.main(["--pairs", str(source), "--db", str(db), "--report", report])

        write_pairs(tmp_path, {"Blood Pact": "Пакт крови"})
        import_pairs.main(["--pairs", str(source), "--db", str(db), "--report", report])

        with Database(db).connect() as connection:
            assert connection.execute("SELECT ru FROM strings").fetchone()[0] == "Пакт крови"

    def test_an_echoed_translation_is_skipped(self, tmp_path):
        # Counting these as coverage would report success while showing English.
        _code, database = run(tmp_path, {"Blood Pact": "Blood Pact", "Attack": "Атака"})

        with database.connect() as connection:
            rows = [r["en"] for r in connection.execute("SELECT en FROM strings")]

        assert rows == ["Attack"]

    def test_a_legitimate_passthrough_is_kept(self, tmp_path):
        # Numbers and codes are correctly identical; only text that should have changed is dropped.
        _code, database = run(tmp_path, {"12345": "12345", "{0}/{1}": "{0}/{1}"})

        with database.connect() as connection:
            assert connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0] == 2

    def test_keep_identity_imports_echoes_too(self, tmp_path):
        _code, database = run(tmp_path, {"Blood Pact": "Blood Pact"}, "--keep-identity")

        with database.connect() as connection:
            assert connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0] == 1

    def test_strings_sharing_a_normalized_key_collapse_to_one(self, tmp_path):
        """After the markup comes off, '<#F00>Text</>' and '<#F00> "Text"' are the same string,
        and OCR could never have told them apart anyway."""
        _code, database = run(tmp_path, {
            '<#F00>Authorization confirmed.</>': '<#F00>Доступ подтверждён.</>',
            '   <#F00> "Authorization confirmed."': '   <#F00> "Доступ подтверждён."',
        })

        with database.connect() as connection:
            assert connection.execute("SELECT COUNT(*) FROM strings").fetchone()[0] == 1

    def test_the_stored_norm_matches_what_ocr_would_produce(self, tmp_path):
        _code, database = run(tmp_path, {"<#F00>Blood Pact</>": "<#F00>Кровавый пакт</>"})

        with database.connect() as connection:
            stored = connection.execute("SELECT norm FROM strings").fetchone()[0]

        # This is the whole point of the base: OCR reads the rendered line and has to land here.
        assert stored == normalize("Blood Pact")

    def test_the_fts_index_is_populated(self, tmp_path):
        _code, database = run(tmp_path, {"Summon a skeletal warrior": "Призывает скелета"})

        with database.connect() as connection:
            hits = connection.execute(
                "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"5ke\"'"
            ).fetchone()[0]

        assert hits > 0

    def test_status_and_src_are_configurable(self, tmp_path):
        _code, database = run(tmp_path, {"Attack": "Атака"}, "--status", "reviewed", "--src", "pack")

        with database.connect() as connection:
            row = connection.execute("SELECT status, src FROM strings").fetchone()

        assert (row["status"], row["src"]) == ("reviewed", "pack")

    def test_reviewed_rows_become_translation_memory(self, tmp_path):
        # find_translation_memory only trusts reviewed/locked, so the status choice decides
        # whether these can seed later conveyor runs.
        _code, database = run(tmp_path, {"Attack": "Атака"}, "--status", "reviewed")

        with database.connect() as connection:
            assert database.find_translation_memory(connection, normalize("Attack")) == "Атака"

    def test_dry_run_writes_nothing(self, tmp_path):
        source = write_pairs(tmp_path, {"Attack": "Атака"})
        db = tmp_path / "czn.db"

        import_pairs.main(["--pairs", str(source), "--db", str(db),
                           "--report", str(tmp_path / "r.json"), "--dry-run"])

        assert not db.exists()

    def test_the_report_lists_validator_findings(self, tmp_path):
        run(tmp_path, {"Deal {0} damage": "Наносит урон"})

        report = json.loads((tmp_path / "report.json").read_text(encoding="utf-8"))
        assert report["flagged"][0]["problems"] == ["placeholder_mismatch"]

    def test_a_missing_file_is_reported(self, tmp_path):
        assert import_pairs.main(["--pairs", str(tmp_path / "nope.json"),
                                  "--db", str(tmp_path / "czn.db")]) == 1

    def test_a_non_object_payload_is_rejected(self, tmp_path):
        source = tmp_path / "all_ru.json"
        source.write_text(json.dumps(["a", "b"]), encoding="utf-8")

        with pytest.raises(ValueError, match="mapping English"):
            import_pairs.main(["--pairs", str(source), "--db", str(tmp_path / "czn.db")])
