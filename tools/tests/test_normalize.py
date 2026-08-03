"""Normalization parity with the C# side.

The two implementations have to agree exactly. When they do not, the importer still writes rows
and the runtime still runs — the strings are simply unreachable from the exact stage, and
coverage drops without a single error anywhere. These tests are the thing that catches that.
"""

import json
from pathlib import Path

import pytest

from czn.normalize import (
    NormalizeOptions,
    has_cyrillic,
    has_latin_letters,
    norm_hash,
    normalize,
    placeholders,
    tags,
)

VECTORS_PATH = Path(__file__).parent / "normalization_vectors.json"
VECTORS = json.loads(VECTORS_PATH.read_text(encoding="utf-8"))["vectors"]


@pytest.mark.parametrize("vector", VECTORS, ids=lambda v: repr(v["input"])[:40])
def test_matches_the_csharp_fixture(vector):
    """Generated from CznTranslator.Lookup and asserted by both test suites."""
    assert normalize(vector["input"]) == vector["norm"]
    assert norm_hash(normalize(vector["input"])) == vector["normHash"]
    assert normalize(
        vector["input"], NormalizeOptions(fold_confusable_glyphs=False)
    ) == vector["normUnfolded"]


@pytest.mark.parametrize(
    "text,expected",
    [
        ("", 17241709254077376921 - (1 << 64)),
        ("deal 1 damag3", 13314471865346301693 - (1 << 64)),
        ("the quick brown fox", 1513236774081638803),
    ],
)
def test_pinned_hash_vectors(text, expected):
    """The same three vectors NormHashTests asserts on the C# side."""
    assert norm_hash(text) == expected


def test_markup_is_stripped_before_punctuation():
    result = normalize("<color=#ff0000>Blood Pact</color>")
    assert "color" not in result
    assert "ff0000" not in result


def test_glyph_folding_is_symmetric():
    assert normalize("Bloodletting") == normalize("8I00dIetting")


def test_placeholders_survive_folding():
    result = normalize("Deal {0} damage to {target}")
    assert "{0}" in result
    assert "{target}" in result


@pytest.mark.parametrize("placeholder", ["%s", "%d", "%1$s"])
def test_printf_placeholders_survive(placeholder):
    assert placeholder in normalize(f"Restores {placeholder} health")


def test_apostrophes_do_not_split_words():
    assert normalize("don't") == normalize("dont")
    assert normalize("don’t") == normalize("dont")


def test_other_punctuation_is_a_boundary():
    assert normalize("HP/MP", NormalizeOptions(fold_confusable_glyphs=False)) == "hp mp"


def test_is_idempotent():
    once = normalize("  <b>Deal {0} damage</b>, then draw 2 cards.\\n")
    assert normalize(once) == once


@pytest.mark.parametrize("value", [None, "", "   ", "<color=#fff></color>"])
def test_empty_inputs(value):
    assert normalize(value) == ""


def test_extracts_placeholders_in_order():
    assert placeholders("Deal {0} damage over {1} turns to {0}") == ["{0}", "{1}", "{0}"]


def test_extracts_tags():
    assert tags("<color=#ff0000>Burn</color> <sprite=4>") == [
        "<color=#ff0000>",
        "</color>",
        "<sprite=4>",
    ]


def test_script_detection():
    assert has_cyrillic("Кровавый пакт")
    assert not has_cyrillic("Blood Pact")
    assert has_latin_letters("Blood Pact")
    assert not has_latin_letters("Кровавый")
    assert not has_latin_letters("{0} 12 %")


def test_alnum_rule_matches_dotnet_not_python():
    """.NET IsLetterOrDigit accepts decimal digits only; str.isalnum() is wider.

    "½" is alnum to Python but not a letter-or-digit to .NET, so treating it as one would make
    the two sides disagree on any string containing it.
    """
    assert "½".isalnum()
    assert normalize("a½b", NormalizeOptions(fold_confusable_glyphs=False)) == "a b"
