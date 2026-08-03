using System.Text.Json;
using CznTranslator.Core.Models;

namespace CznTranslator.Core.Config;

public sealed record ConfigLoadResult(AppConfig Config, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads <c>config.json</c> and applies the profile overrides from TZ §12.
/// <para>
/// Profile values are <em>defaults</em>, not forced settings: an override is applied only
/// where the JSON did not mention that key, so <c>"profile": "laptop"</c> plus an explicit
/// <c>"settleMs": 120</c> keeps 120. That is what makes the profile a set of overrides
/// rather than a separate build.
/// </para>
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    /// <summary>Recommended settle window from §3. Values outside it are allowed but warned about.</summary>
    private const int SettleMsSoftMin = 60;
    private const int SettleMsSoftMax = 260;

    public static ConfigLoadResult LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = new AppConfig();
            return new ConfigLoadResult(fresh, [$"Config '{path}' not found, using built-in defaults."]);
        }

        return Parse(File.ReadAllText(path));
    }

    public static ConfigLoadResult Parse(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions)
                     ?? throw new InvalidDataException("config.json deserialized to null.");

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPaths(document.RootElement, prefix: null, explicitPaths);

        var warnings = new List<string>();
        ApplyProfile(config, explicitPaths, warnings);
        Validate(config, warnings);

        return new ConfigLoadResult(config, warnings);
    }

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, SerializerOptions);

    private static void CollectPaths(JsonElement element, string? prefix, ISet<string> sink)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            var path = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            sink.Add(path);
            CollectPaths(property.Value, path, sink);
        }
    }

    /// <summary>
    /// Laptop profile, TZ §12. Int8 models stay on even for the DirectML path: on an iGPU the
    /// bottleneck is memory bandwidth, and the quantized weights cut traffic to shared RAM.
    /// </summary>
    private static void ApplyProfile(AppConfig config, ICollection<string> explicitPaths, ICollection<string> warnings)
    {
        if (!string.Equals(config.Profile, ProfileNames.Laptop, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(config.Profile, ProfileNames.Default, StringComparison.OrdinalIgnoreCase))
                warnings.Add($"Unknown profile '{config.Profile}', no overrides applied.");
            return;
        }

        SetUnlessExplicit(explicitPaths, "ocr.quantized", () => config.Ocr.Quantized = true);
        SetUnlessExplicit(explicitPaths, "ocr.det.limitSideLen", () => config.Ocr.Det.LimitSideLen = 640);
        SetUnlessExplicit(explicitPaths, "ocr.rec.batchSize", () => config.Ocr.Rec.BatchSize = 4);
        SetUnlessExplicit(explicitPaths, "detect.settleMs", () => config.Detect.SettleMs = 220);
        SetUnlessExplicit(explicitPaths, "detect.pollEveryNthFrame", () => config.Detect.PollEveryNthFrame = 4);
        SetUnlessExplicit(explicitPaths, "detect.autoZoneMaxPriority", () => config.Detect.AutoZoneMaxPriority = 0);
    }

    private static void SetUnlessExplicit(ICollection<string> explicitPaths, string path, Action apply)
    {
        if (!explicitPaths.Contains(path))
            apply();
    }

    private static void Validate(AppConfig config, ICollection<string> warnings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in config.Zones)
        {
            if (string.IsNullOrWhiteSpace(zone.Id))
                throw new InvalidDataException("Every zone needs a non-empty id.");
            if (!seen.Add(zone.Id))
                throw new InvalidDataException($"Duplicate zone id '{zone.Id}'.");
            if (zone.Rect.Length != 4)
                throw new InvalidDataException($"Zone '{zone.Id}': rect must be [x, y, w, h].");
            if (!zone.NormalizedRect.IsValid)
                throw new InvalidDataException($"Zone '{zone.Id}': rect {FormatRect(zone.Rect)} is not inside the 0..1 unit square.");
        }

        if (config.Detect.SettleMs is < SettleMsSoftMin or > SettleMsSoftMax)
        {
            warnings.Add(
                $"settleMs={config.Detect.SettleMs} is outside the {SettleMsSoftMin}..{SettleMsSoftMax} ms range " +
                "the TZ recommends; too low picks up fade-in frames, too high adds visible lag.");
        }

        if (config.Detect.SettleMs <= 0)
            throw new InvalidDataException("settleMs must be positive.");

        if (config.Detect.PollEveryNthFrame < 1)
            throw new InvalidDataException("pollEveryNthFrame must be at least 1.");

        if (config.Detect.HashDistanceThreshold is < 0 or > 64)
            throw new InvalidDataException("hashDistanceThreshold must be within 0..64 (pHash is 64 bits).");

        if (config.Lookup.FuzzyThreshold is <= 0 or > 1)
            throw new InvalidDataException("fuzzyThreshold must be within (0, 1].");

        if (config.Lookup.FuzzyThresholdLowConfidence > config.Lookup.FuzzyThreshold)
        {
            warnings.Add(
                "fuzzyThresholdLowConfidence is above fuzzyThreshold — low-confidence OCR would be judged " +
                "more strictly than clean OCR, which is backwards.");
        }

        if (config.Ocr.Det.LimitSideLen % 32 != 0)
        {
            warnings.Add(
                $"det.limitSideLen={config.Ocr.Det.LimitSideLen} is not a multiple of 32; " +
                "the detector input will be rounded up and the effective scale will differ.");
        }

        if (config.Sync.MaxParallelRequests is < 1 or > 8)
            throw new InvalidDataException("sync.maxParallelRequests must be within 1..8.");

        if (!string.Equals(config.Capture.Source, "wgc", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(config.Capture.Source, "folder", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"capture.source must be 'wgc' or 'folder', got '{config.Capture.Source}'.");
        }

        if (config.Overlay.MinFontScale is <= 0 or > 1)
            throw new InvalidDataException("overlay.minFontScale must be within (0, 1].");
    }

    private static string FormatRect(IReadOnlyList<double> rect) =>
        "[" + string.Join(", ", rect) + "]";
}

/// <summary>
/// Which settings can be re-applied without a restart (TZ §9). Everything else needs the
/// pipeline rebuilt, so the watcher reports it instead of silently half-applying.
/// </summary>
public static class LiveSettings
{
    public static IReadOnlyList<string> Compare(AppConfig old, AppConfig fresh)
    {
        var restartNeeded = new List<string>();

        if (old.Profile != fresh.Profile) restartNeeded.Add("profile");
        if (old.Capture.Source != fresh.Capture.Source) restartNeeded.Add("capture.source");
        if (old.Capture.Folder != fresh.Capture.Folder) restartNeeded.Add("capture.folder");
        if (old.Ocr.Provider != fresh.Ocr.Provider) restartNeeded.Add("ocr.provider");
        if (old.Ocr.AdapterIndex != fresh.Ocr.AdapterIndex) restartNeeded.Add("ocr.adapterIndex");
        if (old.Ocr.Quantized != fresh.Ocr.Quantized) restartNeeded.Add("ocr.quantized");
        if (old.Ocr.ModelsDirectory != fresh.Ocr.ModelsDirectory) restartNeeded.Add("ocr.modelsDirectory");
        if (old.Lookup.DatabasePath != fresh.Lookup.DatabasePath) restartNeeded.Add("lookup.databasePath");
        if (old.Logging.Directory != fresh.Logging.Directory) restartNeeded.Add("logging.directory");

        return restartNeeded;
    }
}
