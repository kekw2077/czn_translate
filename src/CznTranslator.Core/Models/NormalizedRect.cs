namespace CznTranslator.Core.Models;

/// <summary>
/// Rectangle in normalized window coordinates (0..1 of the client area).
/// Everything user-facing stores zones this way so a resolution change does not
/// invalidate the config (TZ §11 "Разные разрешения").
/// </summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public static NormalizedRect Full => new(0, 0, 1, 1);

    public bool IsValid =>
        Width > 0 && Height > 0 &&
        X >= 0 && Y >= 0 &&
        X + Width <= 1.0 + Epsilon &&
        Y + Height <= 1.0 + Epsilon;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// Projects onto a pixel surface. The result is clamped to the surface and
    /// always at least 1×1 so downstream GPU crops never get a degenerate box.
    /// </summary>
    public PixelRect ToPixels(int surfaceWidth, int surfaceHeight)
    {
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(surfaceWidth), "Surface must be non-empty.");

        var left = (int)Math.Round(X * surfaceWidth);
        var top = (int)Math.Round(Y * surfaceHeight);
        var width = (int)Math.Round(Width * surfaceWidth);
        var height = (int)Math.Round(Height * surfaceHeight);

        left = Math.Clamp(left, 0, surfaceWidth - 1);
        top = Math.Clamp(top, 0, surfaceHeight - 1);
        width = Math.Clamp(width, 1, surfaceWidth - left);
        height = Math.Clamp(height, 1, surfaceHeight - top);

        return new PixelRect(left, top, width, height);
    }

    public static NormalizedRect FromArray(IReadOnlyList<double> values)
    {
        if (values is not { Count: 4 })
            throw new ArgumentException("Zone rect must be [x, y, w, h].", nameof(values));
        return new NormalizedRect(values[0], values[1], values[2], values[3]);
    }

    public double[] ToArray() => [X, Y, Width, Height];
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int Area => Width * Height;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}
