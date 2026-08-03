"""Reading localization straight out of a game database.

Complements the AssetRipper JSON path from §8. Many Unity titles ship their master data as a
SQLite file rather than as serialized assets, and when they do this is a much shorter road: the
tables are already relational, the keys are already stable, and no GUI export step is involved.

Two layouts cover almost everything seen in the wild:

* **wide** — one row per string, one column per language::

      key            | ko        | en           | ja
      card.blood_pact| 피의 계약  | Blood Pact   | 血の契約

* **tall** — one row per string *and* language::

      key            | lang | text
      card.blood_pact| ko   | 피의 계약
      card.blood_pact| en   | Blood Pact

The scanner proposes candidates for both and writes them into ``tables.yaml``, which stays the
source of truth exactly as it does for the JSON path. Everything is opened read-only.
"""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass, field
from pathlib import Path

from .tables import has_name_hint, looks_like_sentence

SAMPLE_ROWS = 400

MIN_ROWS = 5

# Column names that identify the English source text outright.
ENGLISH_COLUMN_NAMES = {
    "en", "eng", "english", "en_us", "enus", "en-us",
    "text_en", "en_text", "name_en", "en_name", "desc_en", "en_desc",
}

# Column names that hold a language tag in the tall layout.
LANGUAGE_COLUMN_NAMES = {"lang", "language", "locale", "lang_code", "language_code", "culture"}

# Values in such a column that mean English.
ENGLISH_LANGUAGE_VALUES = {"en", "eng", "english", "en_us", "en-us", "enus", "us"}

# Column names that make a good stable key.
KEY_COLUMN_NAMES = {"key", "id", "code", "string_id", "text_id", "name_key", "key_id", "tid"}

# A language column has few distinct values; anything wider than this is not one.
MAX_LANGUAGE_VALUES = 40


@dataclass
class SqliteTableCandidate:
    file: str
    table: str
    text_column: str
    key_column: str | None
    lang_column: str | None
    lang_value: str | None
    rows: int
    sentence_ratio: float
    column_hint: bool
    table_hint: bool
    samples: list[str] = field(default_factory=list)

    @property
    def name_hint(self) -> bool:
        return self.column_hint or self.table_hint

    @property
    def include(self) -> bool:
        """A hint on the *column* counts; a hint on the table does not.

        A table called ``StringTable`` would otherwise drag in every column it has — the key
        column, and the Korean and Japanese translations along with it. Importing those as
        source English poisons the base, and the mistake is easy to miss in a long tables.yaml.
        The table-level hint still lifts the candidate up the list, it just no longer decides.
        """
        return self.column_hint or self.sentence_ratio >= 0.60

    @property
    def layout(self) -> str:
        return "tall" if self.lang_column else "wide"

    @property
    def reason(self) -> str:
        bits = [f"{self.layout} layout"]
        if self.column_hint:
            bits.append("column name says English")
        elif self.table_hint:
            bits.append("table name hint only")
        bits.append(f"{self.sentence_ratio:.0%} sentence-like")
        if self.key_column is None:
            bits.append("no key column, falling back to rowid")
        return ", ".join(bits)


def connect_readonly(path: Path) -> sqlite3.Connection:
    """Opens the game database in a mode SQLite itself refuses to write through.

    Read-only is not a nicety here. §0 draws the line at reading game files, and a URI-mode
    connection is what makes that a property of the connection rather than a promise about the
    code above it.
    """
    connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    connection.text_factory = lambda blob: blob.decode("utf-8", errors="replace")
    return connection


def list_tables(connection: sqlite3.Connection) -> list[str]:
    return [
        row[0]
        for row in connection.execute(
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
        )
    ]


def _columns(connection: sqlite3.Connection, table: str) -> list[sqlite3.Row]:
    # Table names cannot be parameterized; they come from sqlite_master, and the quoting keeps a
    # name with a space or a keyword in it from breaking the statement.
    return list(connection.execute(f'PRAGMA table_info("{table}")'))


def _sample(connection: sqlite3.Connection, table: str, limit: int = SAMPLE_ROWS) -> list[sqlite3.Row]:
    return list(connection.execute(f'SELECT * FROM "{table}" LIMIT {int(limit)}'))


def _row_count(connection: sqlite3.Connection, table: str) -> int:
    return int(connection.execute(f'SELECT COUNT(*) FROM "{table}"').fetchone()[0])


def _string_values(rows: list[sqlite3.Row], column: str) -> list[str]:
    values = []
    for row in rows:
        value = row[column]
        if isinstance(value, str) and value.strip():
            values.append(value)
    return values


def _pick_key_column(
    connection: sqlite3.Connection,
    table: str,
    columns: list[sqlite3.Row],
    rows: list[sqlite3.Row],
    exclude: set[str],
) -> str | None:
    """Prefers a declared primary key, then a conventional name, then any unique-looking column.

    Without a stable key every diff after a patch reports the whole table as changed, so this is
    worth getting right even though the rowid fallback technically works.
    """
    candidates = [column["name"] for column in columns if column["name"] not in exclude]

    primary = [column["name"] for column in columns if column["pk"] and column["name"] not in exclude]
    if primary:
        return primary[0]

    named = [name for name in candidates if name.lower() in KEY_COLUMN_NAMES]
    for name in named:
        if _is_unique(rows, name):
            return name

    for name in candidates:
        values = [row[name] for row in rows]
        if all(isinstance(value, (str, int)) for value in values) and _is_unique(rows, name):
            return name

    return None


def _is_unique(rows: list[sqlite3.Row], column: str) -> bool:
    if not rows:
        return False

    values = [row[column] for row in rows]
    if any(value is None for value in values):
        return False

    return len(set(values)) == len(values)


def _find_language_column(
    connection: sqlite3.Connection,
    table: str,
    columns: list[sqlite3.Row],
) -> tuple[str, str] | None:
    """Detects the tall layout and the value that selects English."""
    for column in columns:
        name = column["name"]
        if name.lower() not in LANGUAGE_COLUMN_NAMES:
            continue

        distinct = [
            row[0]
            for row in connection.execute(
                f'SELECT DISTINCT "{name}" FROM "{table}" LIMIT {MAX_LANGUAGE_VALUES + 1}'
            )
            if isinstance(row[0], str)
        ]

        if not distinct or len(distinct) > MAX_LANGUAGE_VALUES:
            continue

        for value in distinct:
            if value.strip().lower().replace("-", "_") in ENGLISH_LANGUAGE_VALUES:
                return name, value

    return None


def scan_database(path: Path) -> list[SqliteTableCandidate]:
    """Every plausible localization table in the file, most convincing first.

    Rejected candidates stay in the list with their ratio and samples, for the same reason the
    JSON scanner keeps them: a table the heuristic missed is far easier to spot here than by
    re-running with different thresholds.
    """
    candidates: list[SqliteTableCandidate] = []

    with connect_readonly(path) as connection:
        for table in list_tables(connection):
            try:
                columns = _columns(connection, table)
                total = _row_count(connection, table)
            except sqlite3.DatabaseError:
                continue

            if total < MIN_ROWS or not columns:
                continue

            rows = _sample(connection, table)
            language = _find_language_column(connection, table, columns)
            lang_column, lang_value = language if language else (None, None)

            for column in columns:
                name = column["name"]
                if name == lang_column:
                    continue

                values = _string_values(rows, name)
                if len(values) < MIN_ROWS:
                    continue

                # In the tall layout only the English rows say anything about this column.
                if lang_column is not None:
                    values = [
                        row[name]
                        for row in rows
                        if row[lang_column] == lang_value and isinstance(row[name], str) and row[name].strip()
                    ]
                    if len(values) < MIN_ROWS:
                        continue

                key_column = _pick_key_column(
                    connection, table, columns, rows, exclude={name} | ({lang_column} if lang_column else set())
                )

                sentence_ratio = sum(1 for value in values if looks_like_sentence(value)) / len(values)

                candidates.append(
                    SqliteTableCandidate(
                        file=path.name,
                        table=table,
                        text_column=name,
                        key_column=key_column if key_column != name else None,
                        lang_column=lang_column,
                        lang_value=lang_value,
                        rows=total,
                        sentence_ratio=sentence_ratio,
                        column_hint=name.lower() in ENGLISH_COLUMN_NAMES or has_name_hint(name),
                        table_hint=has_name_hint(table),
                        samples=values[:3],
                    )
                )

    candidates.sort(key=lambda c: (not c.include, not c.column_hint, -c.sentence_ratio, -c.rows))
    return candidates


def read_table(
    path: Path,
    table: str,
    text_column: str,
    key_column: str | None = None,
    lang_column: str | None = None,
    lang_value: str | None = None,
) -> list[tuple[str, str]]:
    """``(key, english)`` pairs for one configured table.

    Keys are prefixed with the table name so two tables in one database cannot collide silently;
    a genuine collision still raises from ``read_dump``.
    """
    selected_key = f'"{key_column}"' if key_column else "rowid"
    where = f'WHERE "{lang_column}" = ?' if lang_column else ""
    parameters = (lang_value,) if lang_column else ()

    entries: list[tuple[str, str]] = []

    with connect_readonly(path) as connection:
        query = f'SELECT {selected_key} AS k, "{text_column}" AS v FROM "{table}" {where}'
        for row in connection.execute(query, parameters):
            value = row["v"]
            if not isinstance(value, str) or not value.strip():
                continue

            key = row["k"]
            if key is None:
                continue

            entries.append((f"{table}.{key}", value))

    return entries
