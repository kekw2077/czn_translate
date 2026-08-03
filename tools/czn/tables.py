"""Finding the localization tables in an AssetRipper dump (TZ §8).

A dump holds dozens of JSON files and only a handful carry user-facing English. The heuristic
here proposes candidates; the result is written to ``tools/tables.yaml``, which is then edited
by hand and becomes the source of truth. Nothing downstream re-runs the guess.
"""

from __future__ import annotations

import json
import re
from collections.abc import Iterator
from dataclasses import dataclass, field
from pathlib import Path

# Field or file names that give the table away regardless of its contents.
NAME_HINTS = ("locale", "text", "string", "desc", "name", "lang")

# Share of values that must look like English prose for a table to qualify.
SENTENCE_RATIO_THRESHOLD = 0.60

MIN_WORDS = 4

_LATIN_WORD = re.compile(r"^[A-Za-z][A-Za-z'’\-]*$")

# Things that are text-shaped but are not text: asset paths, GUIDs, enum-ish identifiers.
_TECHNICAL = re.compile(
    r"^(?:[A-Za-z]:[\\/]|assets[\\/]|[0-9a-f]{16,}$|#[0-9a-fA-F]{3,8}$)",
    re.IGNORECASE,
)


@dataclass
class TableCandidate:
    file: str
    path: str
    entries: int
    sentence_ratio: float
    name_hint: bool
    samples: list[str] = field(default_factory=list)

    @property
    def include(self) -> bool:
        return self.name_hint or self.sentence_ratio >= SENTENCE_RATIO_THRESHOLD

    @property
    def reason(self) -> str:
        if self.name_hint and self.sentence_ratio >= SENTENCE_RATIO_THRESHOLD:
            return f"name hint and {self.sentence_ratio:.0%} sentence-like"
        if self.name_hint:
            return "name hint"
        if self.sentence_ratio >= SENTENCE_RATIO_THRESHOLD:
            return f"{self.sentence_ratio:.0%} sentence-like"
        return f"only {self.sentence_ratio:.0%} sentence-like"


def looks_like_sentence(value: str) -> bool:
    """Latin words, more than three of them, and not obviously a technical identifier."""
    if not value or len(value) > 2000:
        return False
    if _TECHNICAL.match(value.strip()):
        return False

    words = value.split()
    if len(words) < MIN_WORDS:
        return False

    latin_words = sum(1 for word in words if _LATIN_WORD.match(word.strip(".,!?:;()[]\"")))
    return latin_words / len(words) >= 0.6


def has_name_hint(name: str) -> bool:
    lowered = name.lower()
    return any(hint in lowered for hint in NAME_HINTS)


def iter_string_entries(node: object, prefix: str = "") -> Iterator[tuple[str, str]]:
    """Walks arbitrary JSON and yields ``(dotted_path, value)`` for every string leaf.

    The dump shape varies between AssetRipper versions and between asset types, so this stays
    structure-agnostic rather than pattern-matching one particular layout.
    """
    if isinstance(node, dict):
        for name, value in node.items():
            child = f"{prefix}.{name}" if prefix else str(name)
            yield from iter_string_entries(value, child)
    elif isinstance(node, list):
        for index, value in enumerate(node):
            child = f"{prefix}[{index}]"
            yield from iter_string_entries(value, child)
    elif isinstance(node, str):
        yield prefix, node


def _container_path(entry_path: str) -> str:
    """Groups entries by their container: ``entries[3].text`` -> ``entries[].text``."""
    return re.sub(r"\[\d+\]", "[]", entry_path)


def scan_file(path: Path) -> list[TableCandidate]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeDecodeError):
        return []

    groups: dict[str, list[str]] = {}
    for entry_path, value in iter_string_entries(payload):
        groups.setdefault(_container_path(entry_path), []).append(value)

    candidates = []
    for container, values in groups.items():
        if not values:
            continue

        sentence_like = sum(1 for value in values if looks_like_sentence(value))
        candidates.append(
            TableCandidate(
                file=path.name,
                path=container,
                entries=len(values),
                sentence_ratio=sentence_like / len(values),
                name_hint=has_name_hint(path.name) or has_name_hint(container),
                samples=values[:3],
            )
        )

    return candidates


def _index_signature(entry_path: str) -> tuple[int, ...]:
    return tuple(int(match) for match in re.findall(r"\[(\d+)\]", entry_path))


def collect_entries(
    payload: object,
    value_path: str,
    key_path: str | None = None,
    key_prefix: str = "",
) -> list[tuple[str, str]]:
    """Pulls ``(key, english)`` pairs for one configured table out of a parsed dump file.

    When ``key_path`` names a sibling field, values are paired with it by array index — that is
    the stable identity across patches. Without one the key falls back to the JSON path, which
    works but shifts whenever entries are inserted, so a real key field is worth configuring.
    """
    values: dict[tuple[int, ...], tuple[str, str]] = {}
    keys: dict[tuple[int, ...], str] = {}

    for entry_path, value in iter_string_entries(payload):
        container = _container_path(entry_path)
        if container == value_path:
            values[_index_signature(entry_path)] = (entry_path, value)
        elif key_path is not None and container == key_path:
            keys[_index_signature(entry_path)] = value

    entries: list[tuple[str, str]] = []
    for signature, (entry_path, value) in sorted(values.items()):
        if key_path is not None:
            explicit = keys.get(signature)
            if explicit is None:
                continue
            key = f"{key_prefix}{explicit}" if key_prefix else explicit
        else:
            key = f"{key_prefix}{entry_path}" if key_prefix else entry_path

        entries.append((key, value))

    return entries


def scan_directory(directory: Path, min_entries: int = 5) -> list[TableCandidate]:
    """Every candidate in the dump, sorted with the most convincing first.

    Rejected candidates are kept in the output too — a table the heuristic missed is far easier
    to spot in a list with its ratio and samples than by re-running the scan with new thresholds.
    """
    candidates: list[TableCandidate] = []
    for path in sorted(directory.rglob("*.json")):
        candidates.extend(c for c in scan_file(path) if c.entries >= min_entries)

    candidates.sort(key=lambda c: (not c.include, -c.sentence_ratio, -c.entries))
    return candidates
