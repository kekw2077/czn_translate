"""SQLite access for the offline conveyor.

Same file and same schema as the desktop app — ``tools/schema.sql`` is the single source of
truth and is embedded into ``CznTranslator.Lookup`` from here.
"""

from __future__ import annotations

import sqlite3
import time
from collections.abc import Iterator
from dataclasses import dataclass
from pathlib import Path

from .normalize import norm_hash, normalize

SCHEMA_PATH = Path(__file__).resolve().parent.parent / "schema.sql"

# FTS5 trigram landed in 3.34; without it the fuzzy stage silently matches nothing.
MIN_SQLITE = (3, 34)

STATUS_NEW = "new"
STATUS_MT = "mt"
STATUS_REVIEWED = "reviewed"
STATUS_LOCKED = "locked"
STATUS_STALE = "stale"

SRC_PACK = "pack"
SRC_OCR = "ocr"
SRC_MANUAL = "manual"


@dataclass(frozen=True)
class StringRow:
    id: int
    key: str | None
    table_name: str | None
    en: str
    ru: str | None
    norm: str
    status: str
    src: str
    pack_version: int | None


class Database:
    def __init__(self, path: str | Path) -> None:
        self.path = Path(path)

    def connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(self.path)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        connection.execute("PRAGMA busy_timeout = 3000")
        return connection

    def ensure_created(self) -> None:
        version = tuple(int(part) for part in sqlite3.sqlite_version.split(".")[:2])
        if version < MIN_SQLITE:
            raise RuntimeError(
                f"SQLite {sqlite3.sqlite_version} is too old; FTS5 trigram needs "
                f"{MIN_SQLITE[0]}.{MIN_SQLITE[1]}+."
            )

        with self.connect() as connection:
            connection.executescript(SCHEMA_PATH.read_text(encoding="utf-8"))

    # ------------------------------------------------------------------ strings

    def upsert_string(
        self,
        connection: sqlite3.Connection,
        *,
        en: str,
        ru: str | None = None,
        key: str | None = None,
        table_name: str | None = None,
        status: str = STATUS_NEW,
        src: str = SRC_PACK,
        pack_version: int | None = None,
    ) -> int:
        norm = normalize(en)
        now = int(time.time())

        if key is None:
            cursor = connection.execute(
                """
                INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src,
                                     pack_version, updated_at)
                VALUES (NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                RETURNING id
                """,
                (table_name, en, ru, norm, norm_hash(norm), status, src, pack_version, now),
            )
            return int(cursor.fetchone()[0])

        # idx_key is a partial unique index, so the conflict target has to repeat its WHERE
        # clause — plain ON CONFLICT(key) is rejected outright.
        cursor = connection.execute(
            """
            INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src,
                                 pack_version, updated_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(key) WHERE key IS NOT NULL DO UPDATE SET
              table_name   = excluded.table_name,
              en           = excluded.en,
              ru           = COALESCE(excluded.ru, strings.ru),
              norm         = excluded.norm,
              norm_hash    = excluded.norm_hash,
              status       = excluded.status,
              src          = excluded.src,
              pack_version = excluded.pack_version,
              updated_at   = excluded.updated_at
            RETURNING id
            """,
            (key, table_name, en, ru, norm, norm_hash(norm), status, src, pack_version, now),
        )
        return int(cursor.fetchone()[0])

    def get_by_key(self, connection: sqlite3.Connection, key: str) -> StringRow | None:
        row = connection.execute(
            """
            SELECT id, key, table_name, en, ru, norm, status, src, pack_version
            FROM strings WHERE key = ?
            """,
            (key,),
        ).fetchone()
        return _to_row(row) if row else None

    def iter_by_status(
        self,
        connection: sqlite3.Connection,
        statuses: tuple[str, ...],
        limit: int | None = None,
    ) -> Iterator[StringRow]:
        placeholders = ",".join("?" for _ in statuses)
        sql = f"""
            SELECT id, key, table_name, en, ru, norm, status, src, pack_version
            FROM strings WHERE status IN ({placeholders}) ORDER BY id
        """
        if limit is not None:
            sql += f" LIMIT {int(limit)}"

        for row in connection.execute(sql, statuses):
            yield _to_row(row)

    def set_translation(
        self,
        connection: sqlite3.Connection,
        string_id: int,
        ru: str,
        status: str,
    ) -> None:
        connection.execute(
            "UPDATE strings SET ru = ?, status = ?, updated_at = ? WHERE id = ?",
            (ru, status, int(time.time()), string_id),
        )

    def set_status(self, connection: sqlite3.Connection, string_id: int, status: str) -> None:
        connection.execute(
            "UPDATE strings SET status = ?, updated_at = ? WHERE id = ?",
            (status, int(time.time()), string_id),
        )

    def find_translation_memory(self, connection: sqlite3.Connection, norm: str) -> str | None:
        """A finished translation of the same normalized text under some other key.

        Gacha text repeats 20–40%, so this is checked before every model call (TZ §8).
        Only human-blessed rows qualify — reusing another machine guess would just multiply it.
        """
        row = connection.execute(
            """
            SELECT ru FROM strings
            WHERE norm_hash = ? AND norm = ? AND ru IS NOT NULL AND ru <> ''
              AND status IN ('reviewed', 'locked')
            ORDER BY CASE status WHEN 'locked' THEN 0 ELSE 1 END, id
            LIMIT 1
            """,
            (norm_hash(norm), norm),
        ).fetchone()
        return row[0] if row else None

    # ------------------------------------------------------------ pack versions

    def latest_pack_version(self, connection: sqlite3.Connection) -> sqlite3.Row | None:
        return connection.execute(
            "SELECT version, pack_md5, ripped_at, note FROM pack_versions ORDER BY version DESC LIMIT 1"
        ).fetchone()

    def record_pack_version(
        self,
        connection: sqlite3.Connection,
        pack_md5: str,
        note: str | None = None,
    ) -> int:
        cursor = connection.execute(
            """
            INSERT INTO pack_versions (version, pack_md5, ripped_at, note)
            VALUES ((SELECT COALESCE(MAX(version), 0) + 1 FROM pack_versions), ?, ?, ?)
            RETURNING version
            """,
            (pack_md5, int(time.time()), note),
        )
        return int(cursor.fetchone()[0])

    # ---------------------------------------------------------------- glossary

    def load_glossary(self, connection: sqlite3.Connection) -> dict[str, str]:
        return {
            row["en"]: row["ru"]
            for row in connection.execute("SELECT en, ru FROM glossary ORDER BY en")
        }

    def replace_glossary(self, connection: sqlite3.Connection, entries: dict[str, dict]) -> None:
        connection.execute("DELETE FROM glossary")
        connection.executemany(
            "INSERT INTO glossary (en, ru, locked, note) VALUES (?, ?, ?, ?)",
            [
                (en, value["ru"], int(value.get("locked", False)), value.get("note"))
                for en, value in entries.items()
            ],
        )

    def rebuild_fts(self, connection: sqlite3.Connection) -> None:
        """After a bulk import — cheaper than letting the triggers do it row by row."""
        connection.execute("INSERT INTO strings_fts(strings_fts) VALUES ('rebuild')")


def _to_row(row: sqlite3.Row) -> StringRow:
    return StringRow(
        id=row["id"],
        key=row["key"],
        table_name=row["table_name"],
        en=row["en"],
        ru=row["ru"],
        norm=row["norm"],
        status=row["status"],
        src=row["src"],
        pack_version=row["pack_version"],
    )
