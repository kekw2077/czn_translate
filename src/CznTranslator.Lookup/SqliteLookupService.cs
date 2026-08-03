using System.Text.Json;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Metrics;
using CznTranslator.Core.Models;
using Microsoft.Data.Sqlite;
using Serilog;

namespace CznTranslator.Lookup;

/// <summary>
/// The §5 cascade over SQLite: <c>ocr_corrections</c> → exact <c>norm_hash</c> → FTS5 trigram
/// plus Levenshtein → LLM fallback. Every stage that produces a translation writes it back so
/// the same screen resolves one stage earlier next time.
/// </summary>
public sealed class SqliteLookupService : ILookupService
{
    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        WriteIndented = false
    };

    private readonly TranslationDatabase _database;
    private readonly LookupSection _settings;
    private readonly ITranslationFallback? _fallback;
    private readonly MetricsCollector? _metrics;
    private readonly ILogger _log;

    public SqliteLookupService(
        TranslationDatabase database,
        LookupSection settings,
        ITranslationFallback? fallback = null,
        MetricsCollector? metrics = null,
        ILogger? log = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fallback = fallback;
        _metrics = metrics;
        _log = log ?? Log.Logger;
    }

    /// <summary>
    /// Live-applied thresholds are read through this, so a config reload changes behaviour
    /// without rebuilding the service.
    /// </summary>
    public LookupSection Settings => _settings;

    public async Task<LookupHit> ResolveAsync(
        string recognizedText,
        double confidence,
        CancellationToken cancellationToken = default)
    {
        var normalized = TextNormalizer.Normalize(recognizedText);
        if (normalized.Length == 0)
            return LookupHit.Missed(recognizedText ?? string.Empty);

        await using var connection = _database.OpenConnection();

        var correction = await TryCorrectionAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
        if (correction is not null)
        {
            _metrics?.RecordResolution(LookupSource.Correction);
            return correction;
        }

        var exact = await TryExactAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            _metrics?.RecordResolution(LookupSource.Exact);
            return exact;
        }

        var fuzzy = await TryFuzzyAsync(connection, normalized, confidence, cancellationToken).ConfigureAwait(false);
        if (fuzzy is not null)
        {
            // The recognized form is now a known alias of that string; next time it short-circuits.
            if (fuzzy.StringId is { } id)
                await RecordCorrectionAsync(connection, normalized, id, cancellationToken).ConfigureAwait(false);

            _metrics?.RecordResolution(LookupSource.Fuzzy);
            return fuzzy;
        }

        var llm = await TryFallbackAsync(connection, recognizedText, normalized, cancellationToken).ConfigureAwait(false);
        if (llm is not null)
        {
            _metrics?.RecordResolution(LookupSource.Llm);
            return llm;
        }

        _metrics?.RecordResolution(LookupSource.Miss);
        return LookupHit.Missed(recognizedText);
    }

    private static async Task<LookupHit?> TryCorrectionAsync(
        SqliteConnection connection,
        string normalized,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.en, s.ru
            FROM ocr_corrections c
            JOIN strings s ON s.id = c.string_id
            WHERE c.raw_norm = $norm
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$norm", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var id = reader.GetInt64(0);
        var en = reader.GetString(1);
        var ru = reader.IsDBNull(2) ? null : reader.GetString(2);

        await using var bump = connection.CreateCommand();
        bump.CommandText = "UPDATE ocr_corrections SET hits = hits + 1 WHERE raw_norm = $norm;";
        bump.Parameters.AddWithValue("$norm", normalized);
        await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new LookupHit(LookupSource.Correction, en, ru, id, 1.0);
    }

    private static async Task<LookupHit?> TryExactAsync(
        SqliteConnection connection,
        string normalized,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // norm_hash carries the index; the norm comparison guards against a hash collision.
        // Ordering prefers a row that actually has a translation, then the most trustworthy
        // status, then the most trustworthy source.
        command.CommandText =
            """
            SELECT id, en, ru
            FROM strings
            WHERE norm_hash = $hash AND norm = $norm
            ORDER BY
              (ru IS NULL OR ru = ''),
              CASE status
                WHEN 'locked'   THEN 0
                WHEN 'reviewed' THEN 1
                WHEN 'mt'       THEN 2
                WHEN 'new'      THEN 3
                ELSE 4
              END,
              CASE src
                WHEN 'pack'   THEN 0
                WHEN 'manual' THEN 1
                ELSE 2
              END,
              id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", NormHash.ComputeSigned(normalized));
        command.Parameters.AddWithValue("$norm", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var ru = reader.IsDBNull(2) ? null : reader.GetString(2);
        return new LookupHit(LookupSource.Exact, reader.GetString(1), ru, reader.GetInt64(0), 1.0);
    }

    private async Task<LookupHit?> TryFuzzyAsync(
        SqliteConnection connection,
        string normalized,
        double confidence,
        CancellationToken cancellationToken)
    {
        var query = FtsQuery.BuildTrigramQuery(normalized);
        if (query is null)
            return null;

        // Shaky OCR is judged more leniently — TZ §5, шаг 3.
        var threshold = confidence < _settings.LowConfidenceCutoff
            ? _settings.FuzzyThresholdLowConfidence
            : _settings.FuzzyThreshold;

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.id, s.en, s.ru, s.norm
            FROM strings_fts
            JOIN strings s ON s.id = strings_fts.rowid
            WHERE strings_fts MATCH $query
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$limit", _settings.FtsCandidateLimit);

        long? bestId = null;
        string? bestEn = null;
        string? bestRu = null;
        var bestScore = 0.0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var candidateNorm = reader.GetString(3);
            var score = StringSimilarity.ScoreAtLeast(normalized, candidateNorm, threshold);
            if (score < threshold || score <= bestScore)
                continue;

            bestScore = score;
            bestId = reader.GetInt64(0);
            bestEn = reader.GetString(1);
            bestRu = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (bestId is null || bestEn is null)
            return null;

        _log.Debug("Fuzzy hit {Score:F3} (threshold {Threshold:F2}) for {Norm}.", bestScore, threshold, normalized);
        return new LookupHit(LookupSource.Fuzzy, bestEn, bestRu, bestId, bestScore);
    }

    private async Task<LookupHit?> TryFallbackAsync(
        SqliteConnection connection,
        string recognizedText,
        string normalized,
        CancellationToken cancellationToken)
    {
        if (_fallback is not { IsEnabled: true })
            return null;

        var translated = await _fallback.TranslateAsync(recognizedText, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(translated))
            return null;

        // Written back as src='ocr'/status='mt' so the next occurrence is an exact hit and the
        // review queue can pick it up later (TZ §5 шаг 4, §7).
        var id = await InsertOcrStringAsync(connection, recognizedText, translated, normalized, cancellationToken)
            .ConfigureAwait(false);

        return new LookupHit(LookupSource.Llm, recognizedText, translated, id, 1.0);
    }

    private static async Task<long> InsertOcrStringAsync(
        SqliteConnection connection,
        string english,
        string russian,
        string normalized,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO strings (key, table_name, en, ru, norm, norm_hash, status, src, pack_version, updated_at)
            VALUES (NULL, NULL, $en, $ru, $norm, $hash, 'mt', 'ocr', NULL, $now)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$en", english);
        command.Parameters.AddWithValue("$ru", russian);
        command.Parameters.AddWithValue("$norm", normalized);
        command.Parameters.AddWithValue("$hash", NormHash.ComputeSigned(normalized));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id);
    }

    public async Task<ZoneResult?> TryGetCachedZoneAsync(
        string zoneId,
        ulong zoneHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM zone_cache WHERE zone_hash = $hash LIMIT 1;";
        command.Parameters.AddWithValue("$hash", NormHash.ToSigned(zoneHash));

        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (payload is null)
            return null;

        await using var bump = connection.CreateCommand();
        bump.CommandText =
            "UPDATE zone_cache SET hits = hits + 1, last_seen = $now WHERE zone_hash = $hash;";
        bump.Parameters.AddWithValue("$hash", NormHash.ToSigned(zoneHash));
        bump.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await bump.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        List<CachedLine>? lines;
        try
        {
            lines = JsonSerializer.Deserialize<List<CachedLine>>(payload, PayloadJson);
        }
        catch (JsonException ex)
        {
            _log.Warning(ex, "zone_cache payload for {Hash} is unreadable, treating as a miss.", zoneHash);
            return null;
        }

        if (lines is null)
            return null;

        _metrics?.RecordZoneCacheHit();

        var restored = lines
            .Select(line => new TranslatedLine(
                new PixelRect(line.X, line.Y, line.W, line.H),
                new LookupHit(line.Source, line.En, line.Ru, line.Id, line.Score),
                line.Confidence))
            .ToList();

        return new ZoneResult(zoneId, zoneHash, restored, FromCache: true);
    }

    public async Task StoreZoneAsync(ZoneResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var payload = JsonSerializer.Serialize(
            result.Lines.Select(line => new CachedLine(
                line.Box.X, line.Box.Y, line.Box.Width, line.Box.Height,
                line.Hit.Source, line.Hit.English, line.Hit.Russian, line.Hit.StringId,
                line.Hit.Score, line.Confidence)),
            PayloadJson);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO zone_cache (zone_hash, payload, hits, last_seen)
            VALUES ($hash, $payload, 1, $now)
            ON CONFLICT(zone_hash) DO UPDATE SET
              payload = excluded.payload,
              hits = zone_cache.hits + 1,
              last_seen = excluded.last_seen;
            """;
        command.Parameters.AddWithValue("$hash", NormHash.ToSigned(result.ZoneHash));
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await TrimZoneCacheAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task TrimZoneCacheAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_settings.ZoneCacheCapacity <= 0)
            return;

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM zone_cache
            WHERE zone_hash IN (
              SELECT zone_hash FROM zone_cache
              ORDER BY last_seen DESC, hits DESC
              LIMIT -1 OFFSET $capacity
            );
            """;
        command.Parameters.AddWithValue("$capacity", _settings.ZoneCacheCapacity);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearZoneCacheAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM zone_cache;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _log.Information("zone_cache cleared.");
    }

    public async Task RecordCorrectionAsync(
        string rawNormalized,
        long stringId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await RecordCorrectionAsync(connection, rawNormalized, stringId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordCorrectionAsync(
        SqliteConnection connection,
        string rawNormalized,
        long stringId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO ocr_corrections (raw_norm, string_id, hits)
            VALUES ($norm, $id, 1)
            ON CONFLICT(raw_norm) DO UPDATE SET
              string_id = excluded.string_id,
              hits = ocr_corrections.hits + 1;
            """;
        command.Parameters.AddWithValue("$norm", rawNormalized);
        command.Parameters.AddWithValue("$id", stringId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record CachedLine(
        int X, int Y, int W, int H,
        LookupSource Source, string En, string? Ru, long? Id,
        double Score, double Confidence);
}
