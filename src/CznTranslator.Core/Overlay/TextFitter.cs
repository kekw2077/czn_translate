using CznTranslator.Core.Config;

namespace CznTranslator.Core.Overlay;

/// <summary>Measured extent of a laid-out string at a given size and wrapping width.</summary>
public readonly record struct TextExtent(double Width, double Height);

/// <summary>
/// Text measurement, abstracted away from DirectWrite so the fitting policy can be tested.
/// </summary>
public interface ITextMeasurer
{
    /// <summary>
    /// Measures <paramref name="text"/> at <paramref name="fontSize"/>. When
    /// <paramref name="maxWidth"/> is positive the text wraps at that width and the reported
    /// height covers every resulting line.
    /// </summary>
    TextExtent Measure(string text, double fontSize, double maxWidth);
}

public enum FitOutcome
{
    /// <summary>Fits on one line at the configured size.</summary>
    AsIs,

    /// <summary>Fits on one line after shrinking, down to minFontScale.</summary>
    Shrunk,

    /// <summary>Wrapped across lines, possibly also shrunk.</summary>
    Wrapped,

    /// <summary>Still too big; the text is clipped with an ellipsis.</summary>
    Truncated
}

public sealed record TextFit(string Text, double FontSize, bool Wrap, FitOutcome Outcome)
{
    /// <summary>True when the drawn text no longer carries the whole translation.</summary>
    public bool NeedsHoverToRead => Outcome == FitOutcome.Truncated;
}

/// <summary>
/// The §6 auto-fit ladder: shrink to <c>minFontScale</c>, then wrap by words, then clip with an
/// ellipsis and leave the full text for the hold-hotkey tooltip.
/// <para>
/// The order matters and is not arbitrary. Shrinking first keeps one line, which reads fastest;
/// wrapping is tried before clipping because a wrapped translation is still complete, and
/// clipping is last because it is the only step that loses information.
/// </para>
/// </summary>
public static class TextFitter
{
    public const string Ellipsis = "…";

    public static TextFit Fit(
        string text,
        double boxWidth,
        double boxHeight,
        OverlaySection settings,
        ITextMeasurer measurer)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(measurer);

        if (boxWidth <= 0 || boxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(boxWidth), "Box must be non-empty.");

        var baseSize = settings.FontSize;
        var minSize = baseSize * settings.MinFontScale;

        var single = measurer.Measure(text, baseSize, maxWidth: 0);
        if (single.Width <= boxWidth && single.Height <= boxHeight)
            return new TextFit(text, baseSize, Wrap: false, FitOutcome.AsIs);

        // Step 1 — shrink, still on one line.
        var shrunk = ShrinkToFit(text, boxWidth, boxHeight, baseSize, minSize, measurer, wrap: false);
        if (shrunk is not null)
            return new TextFit(text, shrunk.Value, Wrap: false, FitOutcome.Shrunk);

        // Step 2 — wrap by words, shrinking again within the same floor.
        var wrapped = ShrinkToFit(text, boxWidth, boxHeight, baseSize, minSize, measurer, wrap: true);
        if (wrapped is not null)
            return new TextFit(text, wrapped.Value, Wrap: true, FitOutcome.Wrapped);

        // Step 3 — clip. The caller keeps the full string for the hold-hotkey tooltip.
        var clipped = Truncate(text, boxWidth, boxHeight, minSize, measurer);
        return new TextFit(clipped, minSize, Wrap: true, FitOutcome.Truncated);
    }

    private static double? ShrinkToFit(
        string text,
        double boxWidth,
        double boxHeight,
        double baseSize,
        double minSize,
        ITextMeasurer measurer,
        bool wrap)
    {
        // Half-point steps: finer than the eye resolves at UI sizes, coarse enough to stay cheap.
        for (var size = baseSize; size >= minSize - 1e-9; size -= 0.5)
        {
            var extent = measurer.Measure(text, size, wrap ? boxWidth : 0);
            if (extent.Width <= boxWidth && extent.Height <= boxHeight)
                return size;
        }

        return null;
    }

    /// <summary>
    /// Drops whole words from the end until what remains plus the ellipsis fits. Cutting on a
    /// word boundary keeps the fragment readable, which a mid-word cut does not.
    /// </summary>
    private static string Truncate(
        string text,
        double boxWidth,
        double boxHeight,
        double fontSize,
        ITextMeasurer measurer)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var count = words.Length - 1; count > 0; count--)
        {
            var candidate = string.Join(' ', words, 0, count) + Ellipsis;
            var extent = measurer.Measure(candidate, fontSize, boxWidth);
            if (extent.Width <= boxWidth && extent.Height <= boxHeight)
                return candidate;
        }

        // A single word too wide even for the box: fall back to characters so something shows.
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + Ellipsis;
            var extent = measurer.Measure(candidate, fontSize, boxWidth);
            if (extent.Width <= boxWidth && extent.Height <= boxHeight)
                return candidate;
        }

        return Ellipsis;
    }
}
