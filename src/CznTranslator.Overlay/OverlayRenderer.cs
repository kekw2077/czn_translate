using System.Globalization;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using CznTranslator.Core.Overlay;
using Serilog;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace CznTranslator.Overlay;

/// <summary>
/// Draws the translated lines with DirectComposition + Direct2D (TZ §6).
/// <para>
/// Not <c>UpdateLayeredWindow</c>: that pushes a full-window bitmap through system memory on
/// every change, which at 1440p is a visible hitch each time a line updates. DComp keeps the
/// surface on the GPU and the compositor does the blending.
/// </para>
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private readonly ILogger _log;

    private readonly ID3D11Device _d3dDevice;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _d2dContext;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly IDCompositionTarget _compositionTarget;
    private readonly IDCompositionVisual _visual;
    private readonly IDWriteFactory _writeFactory;

    private IDCompositionSurface? _surface;
    private int _surfaceWidth;
    private int _surfaceHeight;

    private OverlaySection _settings;
    private IDWriteTextFormat _textFormat;
    private DirectWriteMeasurer _measurer;

    public OverlayRenderer(nint windowHandle, OverlaySection settings, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? Log.Logger;

        _d3dDevice = D3D11.D3D11CreateDevice(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);

        _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();

        using var d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = d2dFactory.CreateDevice(_dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _compositionTarget = _compositionDevice.CreateTargetForHwnd(windowHandle, topmost: true);
        _visual = _compositionDevice.CreateVisual();
        _compositionTarget.SetRoot(_visual);

        _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>();
        _textFormat = CreateTextFormat(_settings);
        _measurer = new DirectWriteMeasurer(_writeFactory, _settings);
    }

    /// <summary>Applies live-reloaded overlay settings without rebuilding the device stack.</summary>
    public void ApplySettings(OverlaySection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        _textFormat.Dispose();
        _textFormat = CreateTextFormat(settings);
        _measurer.Dispose();
        _measurer = new DirectWriteMeasurer(_writeFactory, settings);
    }

    private IDWriteTextFormat CreateTextFormat(OverlaySection settings) =>
        _writeFactory.CreateTextFormat(
            settings.FontFamily,
            FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            (float)settings.FontSize,
            CultureInfo.CurrentUICulture.Name);

    /// <summary>
    /// Redraws the whole overlay. <paramref name="zoneOrigins"/> maps a zone id to the top-left
    /// of its ROI in overlay-local pixels, since OCR boxes are ROI-relative.
    /// </summary>
    public void Draw(
        int width,
        int height,
        IReadOnlyList<ZoneResult> zones,
        IReadOnlyDictionary<string, PixelRect> zoneOrigins)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(zoneOrigins);

        if (width <= 0 || height <= 0)
            return;

        EnsureSurface(width, height);

        var surfacePointer = _surface!.BeginDraw(null, out var offset);
        try
        {
            using var texture = new ID3D11Texture2D(surfacePointer);
            using var dxgiSurface = texture.QueryInterface<IDXGISurface>();
            using var bitmap = _d2dContext.CreateBitmapFromDxgiSurface(dxgiSurface, new BitmapProperties1
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                BitmapOptions = BitmapOptions.Target | BitmapOptions.CannotDraw
            });

            _d2dContext.Target = bitmap;
            _d2dContext.BeginDraw();
            _d2dContext.Transform = Matrix3x2.CreateTranslation(offset.X, offset.Y);

            // Fully transparent everywhere we do not draw — the game has to show through.
            _d2dContext.Clear(new Color4(0, 0, 0, 0));

            using var textBrush = _d2dContext.CreateSolidColorBrush(ParseColor(_settings.TextColor, 1f));
            using var backdropBrush = _d2dContext.CreateSolidColorBrush(
                ParseColor(_settings.BackdropColor, (float)_settings.BackdropOpacity));
            using var debugBrush = _d2dContext.CreateSolidColorBrush(new Color4(0.2f, 1f, 0.4f, 0.9f));

            foreach (var zone in zones)
            {
                if (!zoneOrigins.TryGetValue(zone.ZoneId, out var origin))
                    continue;

                if (_settings.Debug)
                {
                    _d2dContext.DrawRectangle(
                        new Rect(origin.X, origin.Y, origin.Width, origin.Height), debugBrush, 1f);
                }

                foreach (var line in zone.Lines)
                {
                    DrawLine(line, origin, textBrush, backdropBrush, debugBrush);
                }
            }

            _d2dContext.EndDraw();
            _d2dContext.Target = null;
        }
        finally
        {
            _surface.EndDraw();
        }

        _compositionDevice.Commit();
    }

    private void DrawLine(
        TranslatedLine line,
        PixelRect zoneOrigin,
        ID2D1SolidColorBrush textBrush,
        ID2D1SolidColorBrush backdropBrush,
        ID2D1SolidColorBrush debugBrush)
    {
        var box = line.Box.Offset(zoneOrigin.X, zoneOrigin.Y);

        if (_settings.Debug)
            _d2dContext.DrawRectangle(new Rect(box.X, box.Y, box.Width, box.Height), debugBrush, 1f);

        // An untranslated line is left alone: covering the original English with the same English
        // only adds a rectangle and hides the game art behind it.
        if (!line.Hit.IsTranslated)
            return;

        var fit = TextFitter.Fit(line.Hit.Display, box.Width, box.Height, _settings, _measurer);

        using var format = _writeFactory.CreateTextFormat(
            _settings.FontFamily,
            FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            (float)fit.FontSize,
            CultureInfo.CurrentUICulture.Name);

        format.WordWrapping = fit.Wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
        format.TextAlignment = TextAlignment.Leading;
        format.ParagraphAlignment = ParagraphAlignment.Center;

        // The backdrop is what makes Cyrillic readable over arbitrary game art; the font is ours,
        // so the game's own glyph coverage never enters into it.
        _d2dContext.FillRectangle(new Rect(box.X - 2, box.Y - 1, box.Width + 4, box.Height + 2), backdropBrush);

        _d2dContext.DrawText(
            fit.Text,
            format,
            new Rect(box.X, box.Y, box.Width, box.Height),
            textBrush);
    }

    private void EnsureSurface(int width, int height)
    {
        if (_surface is not null && _surfaceWidth == width && _surfaceHeight == height)
            return;

        _surface?.Dispose();
        _surfaceWidth = width;
        _surfaceHeight = height;

        _surface = _compositionDevice.CreateSurface(
            (uint)width, (uint)height,
            Format.B8G8R8A8_UNorm,
            Vortice.DXGI.AlphaMode.Premultiplied);

        _visual.SetContent(_surface);
        _compositionDevice.Commit();
    }

    internal static Color4 ParseColor(string value, float alpha)
    {
        var text = value.TrimStart('#');
        if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            return new Color4(1f, 1f, 1f, alpha);

        if (text.Length == 6)
        {
            return new Color4(
                ((packed >> 16) & 0xFF) / 255f,
                ((packed >> 8) & 0xFF) / 255f,
                (packed & 0xFF) / 255f,
                alpha);
        }

        return new Color4(
            ((packed >> 16) & 0xFF) / 255f,
            ((packed >> 8) & 0xFF) / 255f,
            (packed & 0xFF) / 255f,
            ((packed >> 24) & 0xFF) / 255f * alpha);
    }

    public void Dispose()
    {
        _measurer.Dispose();
        _textFormat.Dispose();
        _surface?.Dispose();
        _visual.Dispose();
        _compositionTarget.Dispose();
        _compositionDevice.Dispose();
        _writeFactory.Dispose();
        _d2dContext.Dispose();
        _d2dDevice.Dispose();
        _dxgiDevice.Dispose();
        _d3dDevice.Dispose();
    }
}

/// <summary>DirectWrite-backed measurement for <see cref="TextFitter"/>.</summary>
internal sealed class DirectWriteMeasurer(IDWriteFactory factory, OverlaySection settings) : ITextMeasurer, IDisposable
{
    public TextExtent Measure(string text, double fontSize, double maxWidth)
    {
        if (string.IsNullOrEmpty(text))
            return new TextExtent(0, 0);

        using var format = factory.CreateTextFormat(
            settings.FontFamily,
            FontWeight.SemiBold,
            Vortice.DirectWrite.FontStyle.Normal,
            FontStretch.Normal,
            (float)fontSize,
            CultureInfo.CurrentUICulture.Name);

        format.WordWrapping = maxWidth > 0 ? WordWrapping.Wrap : WordWrapping.NoWrap;

        using var layout = factory.CreateTextLayout(
            text,
            format,
            maxWidth > 0 ? (float)maxWidth : float.MaxValue,
            float.MaxValue);

        var metrics = layout.Metrics;

        // WidthIncludingTrailingWhitespace, not Width: a translation ending in a space still
        // occupies that space, and using the trimmed width lets it overflow the box by a glyph.
        return new TextExtent(metrics.WidthIncludingTrailingWhitespace, metrics.Height);
    }

    public void Dispose()
    {
    }
}
