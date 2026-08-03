using CznTranslator.Core.Metrics;

namespace CznTranslator.Lookup;

/// <summary>Daily counters in the <c>metrics</c> table (TZ §9).</summary>
public sealed class SqliteMetricsStore(TranslationDatabase database) : IMetricsStore
{
    private readonly TranslationDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task UpsertAsync(MetricsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO metrics (day, ocr_calls, cache_hits, exact_hits, fuzzy_hits, llm_calls, misses, avg_ms)
            VALUES ($day, $ocr, $cache, $exact, $fuzzy, $llm, $misses, $avg)
            ON CONFLICT(day) DO UPDATE SET
              ocr_calls  = excluded.ocr_calls,
              cache_hits = excluded.cache_hits,
              exact_hits = excluded.exact_hits,
              fuzzy_hits = excluded.fuzzy_hits,
              llm_calls  = excluded.llm_calls,
              misses     = excluded.misses,
              avg_ms     = excluded.avg_ms;
            """;
        command.Parameters.AddWithValue("$day", snapshot.Day);
        command.Parameters.AddWithValue("$ocr", snapshot.OcrCalls);
        command.Parameters.AddWithValue("$cache", snapshot.CacheHits);
        command.Parameters.AddWithValue("$exact", snapshot.ExactHits);
        command.Parameters.AddWithValue("$fuzzy", snapshot.FuzzyHits);
        command.Parameters.AddWithValue("$llm", snapshot.LlmCalls);
        command.Parameters.AddWithValue("$misses", snapshot.Misses);
        command.Parameters.AddWithValue("$avg", snapshot.AverageMs);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MetricsSnapshot?> LoadAsync(string day, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT day, ocr_calls, cache_hits, exact_hits, fuzzy_hits, llm_calls, misses, avg_ms
            FROM metrics WHERE day = $day;
            """;
        command.Parameters.AddWithValue("$day", day);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return new MetricsSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
            reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
            reader.IsDBNull(7) ? 0 : reader.GetDouble(7));
    }
}
