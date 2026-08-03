using System.Reflection;
using Microsoft.Data.Sqlite;

namespace CznTranslator.Lookup;

/// <summary>
/// Owns the SQLite file: connection string, schema bootstrap and the capability checks that
/// have to happen before the fuzzy stage is trusted (TZ §11 — FTS5 trigram needs SQLite 3.34+).
/// </summary>
public sealed class TranslationDatabase
{
    private readonly string _connectionString;

    public TranslationDatabase(string databasePath, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    public static string SchemaSql
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("CznTranslator.Lookup.schema.sql")
                               ?? throw new InvalidOperationException("Embedded schema.sql is missing from the assembly.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Creates the schema if it is not there yet. Safe to call on every startup.</summary>
    public void EnsureCreated()
    {
        using var connection = OpenConnection();
        EnsureFtsAvailable(connection);

        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Fails loudly at startup rather than degrading to a silently empty fuzzy stage: a missing
    /// trigram tokenizer would look exactly like "the base has no match for this screen".
    /// </summary>
    public static void EnsureFtsAvailable(SqliteConnection connection)
    {
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version();";
        var version = (string)(versionCommand.ExecuteScalar() ?? "0.0.0");

        if (!IsAtLeast(version, 3, 34))
        {
            throw new NotSupportedException(
                $"SQLite {version} is too old: FTS5 trigram needs 3.34+. Reference " +
                "SQLitePCLRaw.bundle_e_sqlite3 so a current native library is used.");
        }

        try
        {
            using var probe = connection.CreateCommand();
            probe.CommandText =
                "CREATE VIRTUAL TABLE IF NOT EXISTS temp.__fts_probe USING fts5(x, tokenize='trigram');" +
                "DROP TABLE temp.__fts_probe;";
            probe.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            throw new NotSupportedException(
                $"SQLite {version} was built without the FTS5 trigram tokenizer, so the fuzzy " +
                "lookup stage cannot work.", ex);
        }
    }

    public static bool IsAtLeast(string version, int major, int minor)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 ||
            !int.TryParse(parts[0], out var actualMajor) ||
            !int.TryParse(parts[1], out var actualMinor))
        {
            return false;
        }

        return actualMajor > major || (actualMajor == major && actualMinor >= minor);
    }

    /// <summary>Rebuilds the trigram index from the content table — used after a bulk import.</summary>
    public void RebuildFtsIndex()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO strings_fts(strings_fts) VALUES ('rebuild');";
        command.ExecuteNonQuery();
    }
}
