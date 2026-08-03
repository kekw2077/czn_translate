"""Text normalization — the Python half of the shared key (TZ §5).

This is a 1:1 mirror of ``CznTranslator.Lookup.TextNormalizer``. The importer writes
``norm``/``norm_hash`` with this code and the running app looks strings up with the C# one, so
any divergence quietly makes the exact stage useless: rows are present, correct, and
unreachable. ``tools/tests/normalization_vectors.json`` pins both implementations to the same
outputs and is asserted by both test suites.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass

import xxhash

# Unity rich text and sprite tags: <color=#fff>, </color>, <sprite=3>, <b>.
_MARKUP_TAG = re.compile(r"<\s*/?\s*[a-zA-Z][^<>]*>")

# Escaped line breaks that survive a JSON dump as literal backslash-n.
_ESCAPED_BREAK = re.compile(r"\\[rnt]")

# {0}, {value}, {0:N1}, %s, %d, %1$s — kept verbatim so folding cannot touch them.
_PLACEHOLDER = re.compile(r"\{[^{}]*\}|%\d+\$[sdifux]|%[sdifux]")

_WHITESPACE = re.compile(r"\s+")

# Apostrophes vanish rather than splitting a word: "don't" -> "dont".
_APOSTROPHES = "'’ʼ`´"

_GLYPH_FOLD = {
    "l": "1", "i": "1", "1": "1", "¦": "1", "|": "1", "ı": "1",
    "o": "0", "0": "0",
    "s": "5", "5": "5",
    "b": "8", "8": "8",
}

_CYRILLIC_RANGE = (0x0400, 0x04FF)


@dataclass(frozen=True)
class NormalizeOptions:
    """See the C# ``NormalizeOptions``; the flag has to match on both sides or nothing matches."""

    fold_confusable_glyphs: bool = True


DEFAULT_OPTIONS = NormalizeOptions()


def _is_letter_or_digit(char: str) -> bool:
    """Matches .NET ``char.IsLetterOrDigit``: any Unicode letter, or a decimal digit.

    ``str.isalnum()`` is wider than that — it also accepts numerics like ``½`` — and the two
    sides have to agree on every character, not merely on the common ones.
    """
    category = unicodedata.category(char)
    return category.startswith("L") or category == "Nd"


def _fold_glyph(char: str) -> str:
    return _GLYPH_FOLD.get(char, char)


def _append_folded(parts: list[str], text: str, options: NormalizeOptions) -> None:
    for char in text:
        if char in _APOSTROPHES:
            continue
        if _is_letter_or_digit(char):
            parts.append(_fold_glyph(char) if options.fold_confusable_glyphs else char)
        else:
            # Every other non-letter becomes a separator instead of disappearing, so that
            # "hp/mp" stays two tokens and the trigram index keeps a usable word boundary.
            parts.append(" ")


def normalize(raw: str | None, options: NormalizeOptions = DEFAULT_OPTIONS) -> str:
    if not raw or not raw.strip():
        return ""

    # Markup first. Stripping punctuation before the tags would turn <color=#ff0000> into the
    # word "color ff0000" and pollute every string that carries formatting.
    text = _ESCAPED_BREAK.sub(" ", raw)
    text = _MARKUP_TAG.sub(" ", text)
    text = text.lower()

    parts: list[str] = []
    cursor = 0
    for match in _PLACEHOLDER.finditer(text):
        _append_folded(parts, text[cursor:match.start()], options)
        parts.append(match.group(0))
        cursor = match.end()

    _append_folded(parts, text[cursor:], options)

    return _WHITESPACE.sub(" ", "".join(parts)).strip()


def norm_hash(normalized: str) -> int:
    """xxHash64 (seed 0) over UTF-8, reinterpreted as signed — SQLite has no unsigned 64-bit."""
    unsigned = xxhash.xxh64(normalized.encode("utf-8")).intdigest()
    return unsigned - (1 << 64) if unsigned >= (1 << 63) else unsigned


def placeholders(raw: str | None) -> list[str]:
    """Placeholders in order of appearance; the validator compares these between en and ru."""
    return _PLACEHOLDER.findall(raw) if raw else []


def tags(raw: str | None) -> list[str]:
    return _MARKUP_TAG.findall(raw) if raw else []


def has_cyrillic(text: str | None) -> bool:
    return bool(text) and any(_CYRILLIC_RANGE[0] <= ord(c) <= _CYRILLIC_RANGE[1] for c in text)


def has_latin_letters(text: str | None) -> bool:
    return bool(text) and any("a" <= c <= "z" or "A" <= c <= "Z" for c in text)
