using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CznTranslator.Core.Models;

namespace CznTranslator.App;

/// <summary>
/// Renders a synthetic text ROI for the M2 benchmark (§10). The real regression frames come later
/// (§12 <c>testdata/frames</c>); this is a controlled zone with known English text so det + rec
/// latency can be measured before any screenshots exist. Dark text on a light panel matches what
/// PP-OCR was trained on and keeps the measurement about latency rather than a hard recognition
/// case.
/// </summary>
internal static class M2TestImage
{
    public static GrayImage Render(int width, int height, params string[] lines)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(235, 235, 235));
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var font = new Font("Segoe UI", 22f, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(20, 20, 20));

            var step = height / (float)Math.Max(1, lines.Length);
            var y = Math.Max(0f, (step - 26f) / 2f);
            foreach (var line in lines)
            {
                graphics.DrawString(line, font, brush, new PointF(8f, y));
                y += step;
            }
        }

        var pixels = new byte[width * height];
        var data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buffer = new byte[data.Stride * height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var offset = row * data.Stride + col * 4; // BGRA
                    var b = buffer[offset];
                    var g = buffer[offset + 1];
                    var r = buffer[offset + 2];

                    // Rec.601 luma — the same weighting the GPU ROI extractor uses (§2).
                    pixels[row * width + col] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new GrayImage(width, height, pixels);
    }
}
