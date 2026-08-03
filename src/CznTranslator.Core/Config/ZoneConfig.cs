using System.Text.Json.Serialization;
using CznTranslator.Core.Models;

namespace CznTranslator.Core.Config;

public enum ZoneMode
{
    /// <summary>Whole zone is one text block — dialogue boxes.</summary>
    Block,

    /// <summary>Detector decides the layout — tooltips, popups.</summary>
    Auto,

    /// <summary>Single line of text — top bar, resource counters.</summary>
    Line,

    /// <summary>Never polled automatically; only on the hotkey. Use for scrolling lists (TZ §11).</summary>
    Manual
}

public sealed class ZoneConfig
{
    public required string Id { get; set; }

    /// <summary>[x, y, w, h] normalized to the game client area.</summary>
    public double[] Rect { get; set; } = [0, 0, 1, 1];

    /// <summary>0 is highest. A priority-0 zone preempts background zones in the OCR queue.</summary>
    public int Priority { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ZoneMode Mode { get; set; } = ZoneMode.Auto;

    [JsonIgnore]
    public NormalizedRect NormalizedRect => Models.NormalizedRect.FromArray(Rect);

    public static ZoneConfig[] Defaults() =>
    [
        new() { Id = "dialogue", Rect = [0.15, 0.72, 0.70, 0.20], Priority = 0, Mode = ZoneMode.Block },
        new() { Id = "tooltip", Rect = [0.00, 0.00, 1.00, 1.00], Priority = 1, Mode = ZoneMode.Auto },
        new() { Id = "topbar", Rect = [0.00, 0.00, 1.00, 0.08], Priority = 2, Mode = ZoneMode.Line }
    ];
}
