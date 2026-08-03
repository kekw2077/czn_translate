import json

import pytest

from czn.dump import TableSpec, read_dump
from czn.tables import (
    SENTENCE_RATIO_THRESHOLD,
    collect_entries,
    has_name_hint,
    iter_string_entries,
    looks_like_sentence,
    scan_directory,
)


@pytest.mark.parametrize(
    "value,expected",
    [
        ("Deal five points of damage to a random enemy", True),
        ("The quick brown fox jumps", True),
        ("Attack", False),
        ("card_blood_pact_desc", False),
        ("Assets/Resources/UI/panel.prefab", False),
        ("a1b2c3d4e5f6a7b8", False),
        ("#FF00AA", False),
        ("", False),
    ],
)
def test_sentence_heuristic(value, expected):
    assert looks_like_sentence(value) is expected


@pytest.mark.parametrize("name", ["LocaleTable.json", "ui_text", "cardDescription", "StringDb", "langData"])
def test_name_hints(name):
    assert has_name_hint(name)


def test_name_hint_does_not_fire_on_unrelated_names():
    assert not has_name_hint("PrefabRegistry.json")


def test_walks_nested_json():
    payload = {"a": {"b": ["x", "y"]}, "c": 3, "d": "z"}
    assert sorted(iter_string_entries(payload)) == [("a.b[0]", "x"), ("a.b[1]", "y"), ("d", "z")]


def test_collect_entries_pairs_values_with_their_key_field():
    payload = {
        "entries": [
            {"key": "card.blood_pact", "text": "Blood Pact"},
            {"key": "card.iron_will", "text": "Iron Will"},
        ]
    }

    entries = collect_entries(payload, "entries[].text", "entries[].key")

    assert entries == [("card.blood_pact", "Blood Pact"), ("card.iron_will", "Iron Will")]


def test_collect_entries_falls_back_to_the_json_path():
    payload = {"entries": [{"text": "Blood Pact"}]}

    entries = collect_entries(payload, "entries[].text", None, key_prefix="Loc.json:")

    assert entries == [("Loc.json:entries[0].text", "Blood Pact")]


def test_collect_entries_skips_values_with_no_matching_key():
    # A ragged table would otherwise pair value #2 with key #1 and mislabel everything after it.
    payload = {"entries": [{"key": "a", "text": "Alpha"}, {"text": "Beta"}]}

    assert collect_entries(payload, "entries[].text", "entries[].key") == [("a", "Alpha")]


def test_scan_ranks_localization_tables_above_technical_ones(tmp_path):
    (tmp_path / "LocaleTable.json").write_text(
        json.dumps(
            {
                "entries": [
                    {"text": "Deal five points of damage to a random enemy"},
                    {"text": "Restore ten points of health to every ally"},
                    {"text": "Summon a skeletal warrior that guards you"},
                    {"text": "Draw two cards at the start of your turn"},
                    {"text": "Gain three shield whenever you take damage"},
                ]
            }
        ),
        encoding="utf-8",
    )
    (tmp_path / "PrefabRegistry.json").write_text(
        json.dumps({"paths": [f"Assets/Resources/prefab_{i}.prefab" for i in range(8)]}),
        encoding="utf-8",
    )

    candidates = scan_directory(tmp_path)

    assert candidates[0].file == "LocaleTable.json"
    assert candidates[0].include
    assert candidates[0].sentence_ratio >= SENTENCE_RATIO_THRESHOLD

    registry = next(c for c in candidates if c.file == "PrefabRegistry.json")
    assert not registry.include


def test_rejected_candidates_are_still_reported(tmp_path):
    """A table the heuristic missed is far easier to find in a list than by re-tuning thresholds."""
    (tmp_path / "Buttons.json").write_text(
        json.dumps({"labels": ["Attack", "Defend", "Flee", "Wait", "Item", "Skip"]}),
        encoding="utf-8",
    )

    candidates = scan_directory(tmp_path)

    assert len(candidates) == 1
    assert not candidates[0].include
    assert candidates[0].samples


def test_malformed_json_is_skipped_not_fatal(tmp_path):
    (tmp_path / "broken.json").write_text("{ not json", encoding="utf-8")
    assert scan_directory(tmp_path) == []


def test_read_dump_merges_configured_tables(tmp_path):
    (tmp_path / "Loc.json").write_text(
        json.dumps({"entries": [{"key": "ui.ok", "text": "OK"}, {"key": "ui.cancel", "text": "Cancel"}]}),
        encoding="utf-8",
    )

    entries = read_dump(tmp_path, [TableSpec("Loc.json", "entries[].text", "entries[].key")])

    assert entries["ui.ok"][0] == "OK"
    assert entries["ui.cancel"][0] == "Cancel"


def test_read_dump_skips_excluded_tables(tmp_path):
    (tmp_path / "Loc.json").write_text(json.dumps({"entries": [{"key": "a", "text": "A"}]}), encoding="utf-8")

    specs = [TableSpec("Loc.json", "entries[].text", "entries[].key", include=False)]

    assert read_dump(tmp_path, specs) == {}


def test_read_dump_rejects_a_key_collision(tmp_path):
    """Two tables claiming one key means a bad config; silently keeping one would lose the other."""
    (tmp_path / "A.json").write_text(json.dumps({"e": [{"k": "dup", "t": "First"}]}), encoding="utf-8")
    (tmp_path / "B.json").write_text(json.dumps({"e": [{"k": "dup", "t": "Second"}]}), encoding="utf-8")

    specs = [TableSpec("A.json", "e[].t", "e[].k"), TableSpec("B.json", "e[].t", "e[].k")]

    with pytest.raises(ValueError, match="dup"):
        read_dump(tmp_path, specs)


def test_read_dump_reports_a_missing_file(tmp_path):
    with pytest.raises(FileNotFoundError, match="Gone.json"):
        read_dump(tmp_path, [TableSpec("Gone.json", "e[].t")])


def test_read_dump_drops_blank_values(tmp_path):
    (tmp_path / "Loc.json").write_text(
        json.dumps({"e": [{"k": "a", "t": "Alpha"}, {"k": "b", "t": "   "}]}),
        encoding="utf-8",
    )

    entries = read_dump(tmp_path, [TableSpec("Loc.json", "e[].t", "e[].k")])

    assert "a" in entries
    assert "b" not in entries
