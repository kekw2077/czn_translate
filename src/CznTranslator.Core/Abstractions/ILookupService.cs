using CznTranslator.Core.Models;

namespace CznTranslator.Core.Abstractions;

/// <summary>The §5 cascade: corrections → exact → fuzzy → LLM fallback.</summary>
public interface ILookupService
{
    /// <summary>
    /// Resolves one recognized line. <paramref name="confidence"/> is the mean CTC confidence and
    /// selects between the strict and the relaxed fuzzy threshold.
    /// </summary>
    Task<LookupHit> ResolveAsync(string recognizedText, double confidence, CancellationToken cancellationToken = default);

    /// <summary>Cached whole-zone result keyed by the zone pHash, or null on a miss.</summary>
    Task<ZoneResult?> TryGetCachedZoneAsync(string zoneId, ulong zoneHash, CancellationToken cancellationToken = default);

    Task StoreZoneAsync(ZoneResult result, CancellationToken cancellationToken = default);

    /// <summary>Ctrl+Alt+R after a patch — the base changed, cached screens are stale.</summary>
    Task ClearZoneCacheAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a confirmed OCR→string mapping so the next hit short-circuits the cascade.</summary>
    Task RecordCorrectionAsync(string rawNormalized, long stringId, CancellationToken cancellationToken = default);
}

/// <summary>Runtime LLM fallback (TZ §7). Returns null when unavailable or timed out.</summary>
public interface ITranslationFallback
{
    bool IsEnabled { get; }

    Task<string?> TranslateAsync(string english, CancellationToken cancellationToken = default);
}
