-- CZN Overlay Translator — SQLite schema (TZ §5).
--
-- Single source of truth: this file is embedded into CznTranslator.Lookup and read by the
-- Python pipeline in tools/, so the desktop app and the offline conveyor cannot drift apart.
--
-- Deviations from the table list in the TZ, all additive:
--   * strings_fts sync triggers — an external-content FTS5 table is not populated by itself,
--     without these the trigram index stays empty and the fuzzy stage never returns anything.
--   * metrics.exact_hits — §9 defines coverage in terms of exact hits, which cannot be derived
--     from the other columns.
--   * schema_meta — schema version marker for migrations.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_meta (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS strings (
  id           INTEGER PRIMARY KEY,
  key          TEXT,               -- ключ из data.pack, NULL для OCR-only строк
  table_name   TEXT,               -- имя исходной таблицы дампа
  en           TEXT NOT NULL,
  ru           TEXT,
  norm         TEXT NOT NULL,      -- нормализованный en
  norm_hash    INTEGER NOT NULL,   -- xxHash64 от norm, reinterpreted as signed
  status       TEXT NOT NULL,      -- new | mt | reviewed | locked | stale
  src          TEXT NOT NULL,      -- pack | ocr | manual
  pack_version INTEGER,
  updated_at   INTEGER
);

CREATE INDEX IF NOT EXISTS idx_norm_hash ON strings(norm_hash);
CREATE UNIQUE INDEX IF NOT EXISTS idx_key ON strings(key) WHERE key IS NOT NULL;
CREATE INDEX IF NOT EXISTS idx_status ON strings(status);
CREATE INDEX IF NOT EXISTS idx_table_name ON strings(table_name);

CREATE VIRTUAL TABLE IF NOT EXISTS strings_fts USING fts5(
  norm, content='strings', content_rowid='id', tokenize='trigram'
);

CREATE TRIGGER IF NOT EXISTS strings_fts_ai AFTER INSERT ON strings BEGIN
  INSERT INTO strings_fts(rowid, norm) VALUES (new.id, new.norm);
END;

CREATE TRIGGER IF NOT EXISTS strings_fts_ad AFTER DELETE ON strings BEGIN
  INSERT INTO strings_fts(strings_fts, rowid, norm) VALUES ('delete', old.id, old.norm);
END;

CREATE TRIGGER IF NOT EXISTS strings_fts_au AFTER UPDATE OF norm ON strings BEGIN
  INSERT INTO strings_fts(strings_fts, rowid, norm) VALUES ('delete', old.id, old.norm);
  INSERT INTO strings_fts(rowid, norm) VALUES (new.id, new.norm);
END;

CREATE TABLE IF NOT EXISTS glossary (
  en     TEXT PRIMARY KEY,
  ru     TEXT NOT NULL,
  locked INTEGER DEFAULT 0,
  note   TEXT
);

CREATE TABLE IF NOT EXISTS ocr_corrections (
  raw_norm  TEXT PRIMARY KEY,
  string_id INTEGER NOT NULL,
  hits      INTEGER DEFAULT 1,
  FOREIGN KEY (string_id) REFERENCES strings(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS zone_cache (
  zone_hash INTEGER PRIMARY KEY,
  payload   TEXT NOT NULL,
  hits      INTEGER DEFAULT 1,
  last_seen INTEGER
);

CREATE INDEX IF NOT EXISTS idx_zone_cache_last_seen ON zone_cache(last_seen);

CREATE TABLE IF NOT EXISTS pack_versions (
  version   INTEGER PRIMARY KEY,
  pack_md5  TEXT NOT NULL,
  ripped_at INTEGER,
  note      TEXT
);

CREATE TABLE IF NOT EXISTS metrics (
  day         TEXT PRIMARY KEY,
  ocr_calls   INTEGER,
  cache_hits  INTEGER,
  exact_hits  INTEGER,
  fuzzy_hits  INTEGER,
  llm_calls   INTEGER,
  misses      INTEGER,
  avg_ms      REAL
);

INSERT OR IGNORE INTO schema_meta(key, value) VALUES ('schema_version', '1');
