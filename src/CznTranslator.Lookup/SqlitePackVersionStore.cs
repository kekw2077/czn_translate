using CznTranslator.Core.Abstractions;

namespace CznTranslator.Lookup;

public sealed class SqlitePackVersionStore(TranslationDatabase database) : IPackVersionStore
{
    private readonly TranslationDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<PackVersion?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version, pack_md5, ripped_at, note FROM pack_versions ORDER BY version DESC LIMIT 1;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new PackVersion(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>
    /// Appends a version. Old rows are never rewritten — §8 keeps superseded strings around for
    /// rollbacks, and they are useless without the version they belonged to.
    /// </summary>
    public async Task<int> RecordAsync(string packMd5, string? note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packMd5);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO pack_versions (version, pack_md5, ripped_at, note)
            VALUES ((SELECT COALESCE(MAX(version), 0) + 1 FROM pack_versions), $md5, $now, $note)
            RETURNING version;
            """;
        command.Parameters.AddWithValue("$md5", packMd5);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);

        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
