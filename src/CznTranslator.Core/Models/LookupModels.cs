namespace CznTranslator.Core.Models;

/// <summary>Which stage of the §5 cascade produced the translation.</summary>
public enum LookupSource
{
    /// <summary>Confirmed OCR correction, table <c>ocr_corrections</c>.</summary>
    Correction,

    /// <summary>Exact <c>norm_hash</c> hit.</summary>
    Exact,

    /// <summary>FTS5 trigram candidate accepted by the Levenshtein threshold.</summary>
    Fuzzy,

    /// <summary>Runtime LLM fallback (Ollama).</summary>
    Llm,

    /// <summary>Nothing matched — the original English is shown.</summary>
    Miss
}

public sealed record LookupHit(
    LookupSource Source,
    string English,
    string? Russian,
    long? StringId,
    double Score)
{
    public static LookupHit Missed(string english) => new(LookupSource.Miss, english, null, null, 0);

    /// <summary>What the overlay actually draws: translation when we have one, otherwise the original.</summary>
    public string Display => string.IsNullOrEmpty(Russian) ? English : Russian;

    public bool IsTranslated => !string.IsNullOrEmpty(Russian);
}

/// <summary>One drawable item — a source box plus whatever the cascade resolved for it.</summary>
public sealed record TranslatedLine(PixelRect Box, LookupHit Hit, double Confidence);

/// <summary>
/// Result for a whole zone, cached in <c>zone_cache</c> keyed by the zone pHash so a
/// screen we have already seen redraws without touching OCR at all (TZ §3 шаг 4).
/// </summary>
public sealed record ZoneResult(
    string ZoneId,
    ulong ZoneHash,
    IReadOnlyList<TranslatedLine> Lines,
    bool FromCache)
{
    public ZoneResult AsCached() => this with { FromCache = true };
}

public enum StringStatus
{
    New,
    MachineTranslated,
    Reviewed,
    Locked,
    Stale
}

public enum StringSource
{
    Pack,
    Ocr,
    Manual
}
