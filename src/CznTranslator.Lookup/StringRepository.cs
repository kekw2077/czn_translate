using CznTranslator.Core.Models;
using Microsoft.Data.Sqlite;

namespace CznTranslator.Lookup;

public sealed record StringRow(
    long Id,
    string? Key,
    string? TableName,
    string English,
    string? Russian,
    string Norm,
    StringStatus Status,
    StringSource Source,
    int? PackVersion);

/// <summary>Outcome of importing an external dictionary: entries considered, rows written, rows added, rows kept.</summary>
public readonly record struct ImportResult(int Entries, int RowsUpdated, int Inserted, int Skipped);

/// <summary>
/// Write access to the <c>strings</c> table. The Python conveyor owns bulk import; this exists
/// for the desktop side (review edits, LLM write-back) and for tests that need a populated base.
/// </summary>
public sealed class StringRepository(TranslationDatabase database)
{
    private readonly TranslationDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public TranslationDatabase Database => _database;

    public long Upsert(
        string english,
        string? russian,
        string? key = null,
        string? tableName = null,
        StringStatus status = StringStatus.New,
        StringSource source = StringSource.Pack,
        int? packVersion = null)
    {
        ArgumentNullException.ThrowIfNull(english);

        var norm = TextNormalizer.Normalize(english);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();

        // idx_key is a *partial* unique index, so the conflict target has to repeat its WHERE
        // clause verbatim — plain ON CONFLICT(key) is rejected with "does not match any PRIMARY
        // KEY or UNIQUE constraint". Rows without a key (OCR-only) always insert.
        command.CommandText = key is null
            ? """
              INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src, pack_version, updated_at)
              VALUES (NULL, $table, $en, $ru, $norm, $hash, $status, $src, $pack, $now)
              RETURNING id;
              """
            : """
              INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src, pack_version, updated_at)
              VALUES ($key, $table, $en, $ru, $norm, $hash, $status, $src, $pack, $now)
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
              RETURNING id;
              """;

        if (key is not null)
            command.Parameters.AddWithValue("$key", key);

        command.Parameters.AddWithValue("$table", (object?)tableName ?? DBNull.Value);
        command.Parameters.AddWithValue("$en", english);
        command.Parameters.AddWithValue("$ru", (object?)russian ?? DBNull.Value);
        command.Parameters.AddWithValue("$norm", norm);
        command.Parameters.AddWithValue("$hash", NormHash.ComputeSigned(norm));
        command.Parameters.AddWithValue("$status", ToDb(status));
        command.Parameters.AddWithValue("$src", ToDb(source));
        command.Parameters.AddWithValue("$pack", (object?)packVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        return Convert.ToInt64(command.ExecuteScalar());
    }

    public StringRow? GetById(long id)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, key, table_name, en, ru, norm, status, src, pack_version
            FROM strings WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public int Count()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM strings;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CountByStatus(StringStatus status)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM strings WHERE status = $s;";
        command.Parameters.AddWithValue("$s", ToDb(status));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>One page of rows in a status, oldest id first — the review queue and the translate feed.</summary>
    public IReadOnlyList<StringRow> Page(StringStatus status, int limit, int offset)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, key, table_name, en, ru, norm, status, src, pack_version
            FROM strings WHERE status = $s ORDER BY id LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$s", ToDb(status));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var rows = new List<StringRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Rows awaiting translation (new + stale), oldest first — the batch translator's feed.</summary>
    public IReadOnlyList<StringRow> Pending(int? limit = null)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, key, table_name, en, ru, norm, status, src, pack_version " +
            "FROM strings WHERE status IN ('new', 'stale') ORDER BY id" +
            (limit is int n ? $" LIMIT {n}" : string.Empty) + ";";

        var rows = new List<StringRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// A finished translation of the same normalized text under some other key (TZ §8). Gacha text
    /// repeats 20–40%, so this is checked before every model call. Only human-blessed rows qualify.
    /// </summary>
    public string? FindTranslationMemory(string norm)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT ru FROM strings
            WHERE norm_hash = $h AND norm = $norm AND ru IS NOT NULL AND ru <> ''
              AND status IN ('reviewed', 'locked')
            ORDER BY CASE status WHEN 'locked' THEN 0 ELSE 1 END, id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$h", NormHash.ComputeSigned(norm));
        command.Parameters.AddWithValue("$norm", norm);

        var result = command.ExecuteScalar();
        return result is string s ? s : null;
    }

    /// <summary>
    /// Applies an <c>English → Russian</c> map (e.g. from an external LLM) to every row carrying that
    /// English, in one transaction. Matching by exact English fans a translation out to duplicates,
    /// the same way the batch translator does. Returns the number of rows updated.
    /// </summary>
    public int ApplyTranslationsByEnglish(IReadOnlyDictionary<string, string> englishToRussian, StringStatus status)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE strings SET ru = $ru, status = $status, updated_at = $now WHERE en = $en;";
        var en = command.Parameters.Add("$en", SqliteType.Text);
        var ru = command.Parameters.Add("$ru", SqliteType.Text);
        command.Parameters.AddWithValue("$status", ToDb(status));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var affected = 0;
        foreach (var (english, russian) in englishToRussian)
        {
            if (string.IsNullOrEmpty(english) || string.IsNullOrWhiteSpace(russian))
                continue;
            en.Value = english;
            ru.Value = russian;
            affected += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return affected;
    }

    /// <summary>
    /// Imports an <c>English → Russian</c> map keyed by NORMALISED English — the same key the OCR
    /// overlay looks up on — so a dictionary of display text (no markup) still lands on rows whose
    /// stored English carries markup. For each entry: every row with that norm that is not yet
    /// human-blessed (new/stale/mt) gets the translation; an entry that matches no row at all is
    /// inserted so the overlay can still find it; rows already reviewed/locked are left untouched.
    /// One transaction, reports progress, cancellable.
    /// </summary>
    public ImportResult ImportByNormalizedEnglish(
        IReadOnlyDictionary<string, string> map,
        StringStatus status,
        IProgress<(int done, int total, int applied)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var update = connection.CreateCommand();
        update.CommandText =
            "UPDATE strings SET ru = $ru, status = $status, updated_at = $now " +
            "WHERE norm_hash = $h AND norm = $norm AND status IN ('new','stale','mt');";
        var uNorm = update.Parameters.Add("$norm", SqliteType.Text);
        var uHash = update.Parameters.Add("$h", SqliteType.Integer);
        var uRu = update.Parameters.Add("$ru", SqliteType.Text);
        update.Parameters.AddWithValue("$status", ToDb(status));
        update.Parameters.AddWithValue("$now", now);

        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM strings WHERE norm_hash = $h AND norm = $norm LIMIT 1;";
        var eNorm = exists.Parameters.Add("$norm", SqliteType.Text);
        var eHash = exists.Parameters.Add("$h", SqliteType.Integer);

        using var insert = connection.CreateCommand();
        insert.CommandText =
            "INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src, pack_version, updated_at) " +
            "VALUES ($key, NULL, $en, $ru, $norm, $h, $status, 'manual', NULL, $now) " +
            "ON CONFLICT(key) WHERE key IS NOT NULL DO UPDATE SET " +
            "ru = excluded.ru, status = excluded.status, updated_at = excluded.updated_at;";
        var iKey = insert.Parameters.Add("$key", SqliteType.Text);
        var iEn = insert.Parameters.Add("$en", SqliteType.Text);
        var iRu = insert.Parameters.Add("$ru", SqliteType.Text);
        var iNorm = insert.Parameters.Add("$norm", SqliteType.Text);
        var iHash = insert.Parameters.Add("$h", SqliteType.Integer);
        insert.Parameters.AddWithValue("$status", ToDb(status));
        insert.Parameters.AddWithValue("$now", now);

        int entries = 0, rowsUpdated = 0, inserted = 0, skipped = 0, done = 0;
        var total = map.Count;

        foreach (var (english, russian) in map)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;

            var ru = russian?.Trim();
            if (!string.IsNullOrEmpty(english) && !string.IsNullOrEmpty(ru))
            {
                var norm = TextNormalizer.Normalize(english);
                if (norm.Length > 0)
                {
                    entries++;
                    var h = NormHash.ComputeSigned(norm);

                    uNorm.Value = norm;
                    uHash.Value = h;
                    uRu.Value = ru;
                    var n = update.ExecuteNonQuery();
                    if (n > 0)
                    {
                        rowsUpdated += n;
                    }
                    else
                    {
                        eNorm.Value = norm;
                        eHash.Value = h;
                        if (exists.ExecuteScalar() is null)
                        {
                            iKey.Value = $"import:{h}";
                            iEn.Value = english;
                            iRu.Value = ru;
                            iNorm.Value = norm;
                            iHash.Value = h;
                            insert.ExecuteNonQuery();
                            inserted++;
                        }
                        else
                        {
                            skipped++; // this norm exists only as reviewed/locked — keep the human text
                        }
                    }
                }
            }

            if (done % 1000 == 0)
                progress?.Report((done, total, rowsUpdated + inserted));
        }

        transaction.Commit();
        progress?.Report((total, total, rowsUpdated + inserted));
        return new ImportResult(entries, rowsUpdated, inserted, skipped);
    }

    /// <summary>Promotes every machine-translated ('mt') row to 'reviewed' in one statement. Returns the count.</summary>
    public int AcceptAllMachineTranslated()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE strings SET status = 'reviewed', updated_at = $now WHERE status = 'mt';";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return command.ExecuteNonQuery();
    }

    /// <summary>Writes a translation and moves the row's status (review accept, or a model write-back).</summary>
    public void SetTranslation(long id, string? russian, StringStatus status)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE strings SET ru = $ru, status = $status, updated_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$ru", (object?)russian ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", ToDb(status));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    /// <summary>Every pack string's <c>key → English</c>, for diffing a freshly decoded pack (§8).</summary>
    public IReadOnlyDictionary<string, string> PackKeyEnglish()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, en FROM strings WHERE key IS NOT NULL AND src = 'pack';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(0)] = reader.GetString(1);
        return map;
    }

    public int RecordPackVersion(string packMd5, string? note)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pack_versions (version, pack_md5, ripped_at, note)
            VALUES ((SELECT COALESCE(MAX(version), 0) + 1 FROM pack_versions), $md5, $now, $note)
            RETURNING version;
            """;
        command.Parameters.AddWithValue("$md5", packMd5);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Raw <c>status → count</c> over the whole table, for the settings dashboard.</summary>
    public IReadOnlyDictionary<string, int> StatusCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, COUNT(*) FROM strings GROUP BY status;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            counts[reader.GetString(0)] = reader.GetInt32(1);

        return counts;
    }

    private static StringRow Read(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            FromDbStatus(reader.GetString(6)),
            FromDbSource(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetInt32(8));

    public static string ToDb(StringStatus status) => status switch
    {
        StringStatus.New => "new",
        StringStatus.MachineTranslated => "mt",
        StringStatus.Reviewed => "reviewed",
        StringStatus.Locked => "locked",
        StringStatus.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown status.")
    };

    public static string ToDb(StringSource source) => source switch
    {
        StringSource.Pack => "pack",
        StringSource.Ocr => "ocr",
        StringSource.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source.")
    };

    public static StringStatus FromDbStatus(string value) => value switch
    {
        "new" => StringStatus.New,
        "mt" => StringStatus.MachineTranslated,
        "reviewed" => StringStatus.Reviewed,
        "locked" => StringStatus.Locked,
        "stale" => StringStatus.Stale,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown status in the database.")
    };

    public static StringSource FromDbSource(string value) => value switch
    {
        "pack" => StringSource.Pack,
        "ocr" => StringSource.Ocr,
        "manual" => StringSource.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown source in the database.")
    };
}
