using System.Text.Json.Serialization;
using CznTranslator.Core.Models;

namespace CznTranslator.Core.Config;

/// <summary>
/// Contents of <c>config.json</c>. Section names mirror the dotted keys used in the TZ
/// (<c>capture.source</c>, <c>ocr.provider</c>, <c>det.limitSideLen</c>) as nested objects.
/// </summary>
public sealed class AppConfig
{
    /// <summary>"default" or "laptop" (TZ §12) — a set of overrides, not a separate build.</summary>
    public string Profile { get; set; } = ProfileNames.Default;

    public CaptureSection Capture { get; set; } = new();
    public DetectSection Detect { get; set; } = new();
    public OcrSection Ocr { get; set; } = new();
    public LookupSection Lookup { get; set; } = new();
    public OverlaySection Overlay { get; set; } = new();
    public SyncSection Sync { get; set; } = new();
    public LoggingSection Logging { get; set; } = new();

    public ZoneConfig[] Zones { get; set; } = ZoneConfig.Defaults();
}

public static class ProfileNames
{
    public const string Default = "default";
    public const string Laptop = "laptop";
}

public sealed class CaptureSection
{
    /// <summary>"wgc" for the real capture, "folder" for the offline stand (TZ §12).</summary>
    public string Source { get; set; } = "wgc";

    public string Folder { get; set; } = "testdata/frames";

    /// <summary>Frame rate for <c>FolderFrameSource</c> only; WGC is driven by the compositor.</summary>
    public double Fps { get; set; } = 2;

    public string ProcessName { get; set; } = "ChaosZeroNightmare";

    /// <summary>Window class filter; empty matches any class for the process.</summary>
    public string WindowClass { get; set; } = "UnityWndClass";

    /// <summary>How often to re-scan for the game window while it is not running.</summary>
    public int WindowSearchIntervalMs { get; set; } = 2000;
}

public sealed class DetectSection
{
    /// <summary>How long a zone hash must stay put before OCR runs. Live-applied.</summary>
    public int SettleMs { get; set; } = 100;

    /// <summary>Hamming distance above which a zone counts as changed.</summary>
    public int HashDistanceThreshold { get; set; } = 4;

    /// <summary>1 = hash every frame; 4 = every 4th frame (~15 Hz), the laptop profile.</summary>
    public int PollEveryNthFrame { get; set; } = 1;

    /// <summary>
    /// Zones in <see cref="ZoneMode.Auto"/> with a priority above this are demoted to manual.
    /// The laptop profile sets it to 0 so only the priority-0 zone is polled.
    /// </summary>
    public int AutoZoneMaxPriority { get; set; } = int.MaxValue;
}

public sealed class OcrSection
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OcrProviderKind Provider { get; set; } = OcrProviderKind.Auto;

    /// <summary>-1 = pick by the §4 cascade; otherwise force this DXGI adapter.</summary>
    public int AdapterIndex { get; set; } = -1;

    /// <summary>Use the int8 RapidOCR exports instead of fp32.</summary>
    public bool Quantized { get; set; }

    public string ModelsDirectory { get; set; } = "models";

    public DetSection Det { get; set; } = new();
    public RecSection Rec { get; set; } = new();
}

public sealed class DetSection
{
    /// <summary>Longest side the detector input is scaled to before rounding up to a multiple of 32.</summary>
    public int LimitSideLen { get; set; } = 960;

    /// <summary>Binarization threshold on the DB probability map.</summary>
    public double Threshold { get; set; } = 0.3;

    /// <summary>Box expansion after MinAreaRect.</summary>
    public double UnclipRatio { get; set; } = 1.6;

    /// <summary>Boxes smaller than this (in detector-input pixels) are dropped.</summary>
    public int MinArea { get; set; } = 24;
}

public sealed class RecSection
{
    public int Height { get; set; } = 48;

    /// <summary>Lines of equal height are padded and run together; 8 on GPU, 4 on the laptop.</summary>
    public int BatchSize { get; set; } = 8;
}

public sealed class LookupSection
{
    public string DatabasePath { get; set; } = "czn.db";

    /// <summary>Normalized Levenshtein score a fuzzy candidate must reach. Live-applied.</summary>
    public double FuzzyThreshold { get; set; } = 0.85;

    /// <summary>Relaxed threshold used when OCR confidence is below <see cref="LowConfidenceCutoff"/>.</summary>
    public double FuzzyThresholdLowConfidence { get; set; } = 0.80;

    public double LowConfidenceCutoff { get; set; } = 0.7;

    /// <summary>How many FTS5 trigram candidates to rescore in C#.</summary>
    public int FtsCandidateLimit { get; set; } = 50;

    /// <summary>Cap on zone_cache rows; oldest by last_seen are trimmed past this.</summary>
    public int ZoneCacheCapacity { get; set; } = 20000;
}

public sealed class OverlaySection
{
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 18;

    /// <summary>Lower bound for the auto-fit shrink before word wrapping kicks in (TZ §6).</summary>
    public double MinFontScale { get; set; } = 0.8;

    public double BackdropOpacity { get; set; } = 0.78;
    public string TextColor { get; set; } = "#FFF2E8";
    public string BackdropColor { get; set; } = "#141014";

    /// <summary>Draw zone rects, OCR boxes and stage latencies (Ctrl+Alt+D).</summary>
    public bool Debug { get; set; }
}

public sealed class SyncSection
{
    public string OllamaEndpoint { get; set; } = "http://127.0.0.1:11434";
    public string Model { get; set; } = "qwen3-loc";
    public int TimeoutSeconds { get; set; } = 3;

    /// <summary>Never more than this many concurrent runtime fallback requests (TZ §7).</summary>
    public int MaxParallelRequests { get; set; } = 2;

    /// <summary>Path to the game's data.pack, checked for MD5 drift at startup.</summary>
    public string PackPath { get; set; } = "";

    public bool FallbackEnabled { get; set; } = true;
}

public sealed class LoggingSection
{
    public string Level { get; set; } = "Information";
    public string Directory { get; set; } = "logs";
}
