"""Masking game markup so a plain translator only ever sees sentences.

The problem this solves: asking a model to preserve ``<#F9385D>``, ``</>``, ``{0}`` and ``$Shield$``
works most of the time, and "most of the time" across 60,000 strings is a lot of quietly broken
UI. Professional localization does not ask — it replaces every marker with a numbered sentinel,
translates the remaining text, and substitutes the markers back. A lost sentinel is then a
detectable event rather than a silent corruption.

    '<#F9385D>Deal $Fixed Damage$ to all</>'
      ->  '[0]Deal [1] to all[2]'          # this is what the translator sees
      ->  '[0]Наносит [1] всем[2]'         # this is what comes back
      ->  '<#F9385D>Наносит $Фикс. урон$ всем</>'

Three groups of marker, handled differently:

* **structural** — ``<...>``, ``{...}``, ``[...]``, ``#var#``, ``%s``. A closed set of ~640 across
  the whole game. Never shown to a translator, restored verbatim.
* **keywords** — ``$Shield$``, ``$Discard Pile$``. 520 distinct terms whose *contents* are real
  game vocabulary. Translated once via the glossary so the rules text stays self-consistent.
* **line breaks** — ``<br>``. A segment boundary, not a marker: the text on either side is
  translated separately, which reads better and keeps sentinels away from sentence edges.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

# Order matters and is load-bearing. <...> comes first so that <$shake> is consumed whole;
# otherwise the '$' inside it opens a keyword match that runs to the next '$' several words
# later, swallowing a sentence. Keywords also forbid angle brackets internally so an unpaired
# '$' cannot jump across a <br> to pair with a later one.
MARKER = re.compile(
    r'<[^>]*>'
    r'|\{[^{}]*\}'
    r'|#[A-Za-z0-9_]+#'
    r'|\[[a-zA-Z/]+\]'
    r'|%[sd]'
    r'|\$[A-Za-z0-9_][^$<>\n]*\$'
)

LINE_BREAK = re.compile(r'(<br\s*/?>)', re.IGNORECASE)

#: What the translator sees in place of a marker. Tolerant of the spaces MT engines add.
SENTINEL = "[{index}]"
SENTINEL_RE = re.compile(r'\[\s*(\d+)\s*\]')

_LETTER = re.compile(r'[^\W\d_]', re.UNICODE)


def is_keyword(marker: str) -> bool:
    return marker.startswith("$") and marker.endswith("$") and len(marker) > 2


def keyword_term(marker: str) -> str:
    return marker[1:-1]


@dataclass(frozen=True)
class Segment:
    """One translatable run of text plus the markers lifted out of it."""

    masked: str
    markers: tuple[str, ...]

    @property
    def translatable(self) -> bool:
        """False for segments that are pure markup, punctuation or numbers.

        Roughly 8% of segments are like this — ``#basic_ev_0# $Charm$`` and friends. Sending
        them to a translator wastes a request and invites it to invent words.
        """
        without = SENTINEL_RE.sub(" ", self.masked)
        return bool(_LETTER.search(without))


@dataclass(frozen=True)
class MaskedString:
    """A source string decomposed into segments and the separators between them."""

    source: str
    segments: tuple[Segment, ...]
    separators: tuple[str, ...]

    @property
    def translatable_segments(self) -> tuple[Segment, ...]:
        return tuple(s for s in self.segments if s.translatable)


def mask_segment(text: str) -> Segment:
    """Replaces every marker in one segment with a numbered sentinel."""
    markers: list[str] = []
    out: list[str] = []
    cursor = 0

    for match in MARKER.finditer(text):
        out.append(text[cursor:match.start()])
        out.append(SENTINEL.format(index=len(markers)))
        markers.append(match.group(0))
        cursor = match.end()

    out.append(text[cursor:])
    return Segment("".join(out), tuple(markers))


def mask_string(source: str) -> MaskedString:
    """Splits on ``<br>`` and masks each piece.

    The separators are kept verbatim rather than normalized, so ``<br>`` and ``<br/>`` both come
    back exactly as the game wrote them.
    """
    pieces = LINE_BREAK.split(source)

    segments = [mask_segment(pieces[i]) for i in range(0, len(pieces), 2)]
    separators = [pieces[i] for i in range(1, len(pieces), 2)]

    return MaskedString(source, tuple(segments), tuple(separators))


def restore_markers(
    segment: Segment,
    translated: str,
    glossary: dict[str, str] | None = None,
) -> tuple[str, list[int]]:
    """Puts the markers back. Returns the text and the indices that went missing.

    A missing sentinel is reported rather than patched over. Silently dropping a ``</>`` would
    leave the colour span open and repaint the rest of the screen, which is far worse than
    leaving one line in English.
    """
    glossary = glossary or {}
    seen: set[int] = set()

    def substitute(match: re.Match[str]) -> str:
        index = int(match.group(1))
        if index >= len(segment.markers):
            return match.group(0)

        seen.add(index)
        marker = segment.markers[index]

        if is_keyword(marker):
            term = keyword_term(marker)
            return f"${glossary.get(term, term)}$"

        return marker

    restored = SENTINEL_RE.sub(substitute, translated)
    missing = [i for i in range(len(segment.markers)) if i not in seen]

    return restored, missing


def rebuild(
    masked: MaskedString,
    translations: dict[str, str],
    glossary: dict[str, str] | None = None,
) -> tuple[str, list[str]]:
    """Reassembles a full source string. Returns the result and a list of problems.

    A segment with no translation, or one that lost a marker, falls back to its English. Partial
    translation of a string is normal and fine — the untranslated line simply stays readable.
    """
    problems: list[str] = []
    rendered: list[str] = []

    for segment in masked.segments:
        if not segment.translatable:
            rendered.append(render_untranslated(segment, glossary))
            continue

        translated = translations.get(segment.masked)
        if translated is None:
            rendered.append(render_untranslated(segment, glossary))
            continue

        text, missing = restore_markers(segment, translated, glossary)
        if missing:
            problems.append(
                f"lost sentinel(s) {missing} -> {[segment.markers[i] for i in missing]} "
                f"in {segment.masked[:60]!r}"
            )
            rendered.append(render_untranslated(segment, glossary))
            continue

        rendered.append(text)

    out = [rendered[0]] if rendered else []
    for index, separator in enumerate(masked.separators):
        out.append(separator)
        if index + 1 < len(rendered):
            out.append(rendered[index + 1])

    return "".join(out), problems


def render_untranslated(segment: Segment, glossary: dict[str, str] | None = None) -> str:
    """The segment as-is, with keywords still resolved through the glossary.

    Even an untranslated line should say ``$Щит$`` rather than ``$Shield$`` — the term is
    translated once and applies everywhere, independently of whether the sentence around it made
    it through.
    """
    text, _ = restore_markers(segment, segment.masked, glossary)
    return text


def collect_keywords(sources: list[str]) -> list[str]:
    """Every distinct ``$term$`` in the corpus, sorted — the glossary to translate once."""
    terms = {
        keyword_term(match.group(0))
        for source in sources
        for match in MARKER.finditer(source)
        if is_keyword(match.group(0))
    }
    return sorted(terms)
