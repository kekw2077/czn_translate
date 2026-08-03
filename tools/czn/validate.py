"""Translation validator (TZ §8).

A string goes to review when any of these holds — the point is to catch the failures that make a
translation actively worse than the English it replaced, not to judge wording.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from enum import Enum

from .normalize import has_cyrillic, has_latin_letters, placeholders, tags

# Above this the Russian will not fit the widget the English was laid out for.
LENGTH_RATIO_LIMIT = 1.6


class Problem(str, Enum):
    PLACEHOLDER_MISMATCH = "placeholder_mismatch"
    TAG_MISMATCH = "tag_mismatch"
    TOO_LONG = "too_long"
    NOT_TRANSLATED = "not_translated"
    EMPTY = "empty"


@dataclass(frozen=True)
class Finding:
    problem: Problem
    detail: str


def validate(en: str, ru: str | None) -> list[Finding]:
    findings: list[Finding] = []

    if ru is None or not ru.strip():
        return [Finding(Problem.EMPTY, "translation is empty")]

    # Order-insensitive on purpose: Russian word order frequently moves {0} relative to the
    # English, and that is correct. What matters is that the same set survives.
    en_placeholders = Counter(placeholders(en))
    ru_placeholders = Counter(placeholders(ru))
    if en_placeholders != ru_placeholders:
        findings.append(
            Finding(
                Problem.PLACEHOLDER_MISMATCH,
                f"en={sorted(en_placeholders.elements())} ru={sorted(ru_placeholders.elements())}",
            )
        )

    en_tags = Counter(tags(en))
    ru_tags = Counter(tags(ru))
    if en_tags != ru_tags:
        findings.append(
            Finding(
                Problem.TAG_MISMATCH,
                f"en={sorted(en_tags.elements())} ru={sorted(ru_tags.elements())}",
            )
        )

    if en and len(ru) > LENGTH_RATIO_LIMIT * len(en):
        findings.append(
            Finding(
                Problem.TOO_LONG,
                f"{len(ru)} chars vs {len(en)} ({len(ru) / len(en):.2f}x, limit {LENGTH_RATIO_LIMIT})",
            )
        )

    # A model that echoed the English back is the most common silent failure, but a string that
    # was only ever punctuation and numbers has nothing to translate and is fine as-is.
    if has_latin_letters(en) and not has_cyrillic(ru):
        findings.append(Finding(Problem.NOT_TRANSLATED, "no Cyrillic in the translation"))

    return findings


def is_translatable(en: str) -> bool:
    """False for codes, numbers and empty strings — §8 says to return those unchanged."""
    return bool(en and en.strip()) and has_latin_letters(en)
