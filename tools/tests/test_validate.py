import pytest

from czn.validate import LENGTH_RATIO_LIMIT, Problem, is_translatable, validate


def problems(en, ru):
    return {finding.problem for finding in validate(en, ru)}


def test_a_clean_translation_has_no_findings():
    assert validate("Blood Pact", "Кровавый пакт") == []


def test_missing_placeholder_is_caught():
    assert Problem.PLACEHOLDER_MISMATCH in problems("Deal {0} damage", "Наносит урон")


def test_extra_placeholder_is_caught():
    assert Problem.PLACEHOLDER_MISMATCH in problems("Deal damage", "Наносит {0} урона")


def test_placeholder_order_may_change():
    """Russian word order moves {0} around and that is correct — only the set has to survive."""
    assert validate("Deal {0} damage to {1}", "{1} получает {0} урона") == []


def test_repeated_placeholder_count_matters():
    assert Problem.PLACEHOLDER_MISMATCH in problems("{0} and {0}", "{0} и что-то")


def test_tag_mismatch_is_caught():
    assert Problem.TAG_MISMATCH in problems(
        "<color=#f00>Burn</color>", "<color=#0f0>Ожог</color>"
    )


def test_matching_tags_pass():
    assert validate("<color=#f00>Burn</color>", "<color=#f00>Ожог</color>") == []


def test_overlong_translation_is_caught():
    en = "Attack"
    ru = "Атака " * 10
    assert len(ru) > LENGTH_RATIO_LIMIT * len(en)
    assert Problem.TOO_LONG in problems(en, ru)


def test_a_slightly_longer_translation_passes():
    # Russian runs longer than English by default; the limit only fires on genuine overflow.
    assert Problem.TOO_LONG not in problems("Settings", "Настройки")


def test_untranslated_english_is_caught():
    """The most common silent failure: the model echoed the source back."""
    assert Problem.NOT_TRANSLATED in problems("Blood Pact", "Blood Pact")


def test_a_string_with_no_letters_is_not_expected_to_change():
    assert validate("{0}/{1}", "{0}/{1}") == []
    assert validate("12345", "12345") == []


def test_empty_translation_short_circuits():
    assert problems("Blood Pact", "") == {Problem.EMPTY}
    assert problems("Blood Pact", None) == {Problem.EMPTY}


def test_several_problems_are_reported_together():
    found = problems("Deal {0} damage", "Deal damage " * 5)
    assert Problem.PLACEHOLDER_MISMATCH in found
    assert Problem.NOT_TRANSLATED in found
    assert Problem.TOO_LONG in found


@pytest.mark.parametrize(
    "text,expected",
    [
        ("Blood Pact", True),
        ("", False),
        ("   ", False),
        ("12345", False),
        ("{0}", False),
        ("<color=#f00></color>", True),
    ],
)
def test_translatability(text, expected):
    assert is_translatable(text) is expected
