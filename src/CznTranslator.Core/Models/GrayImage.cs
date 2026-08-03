namespace CznTranslator.Core.Models;

/// <summary>
/// Tightly packed 8-bit grayscale buffer. This is the only pixel payload that
/// ever crosses from GPU to CPU: the capture stage crops, converts and downscales
/// on the GPU and reads back just the ROI (TZ §2).
/// </summary>
public sealed class GrayImage
{
    public GrayImage(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image must be non-empty.");
        if (pixels.Length < (long)width * height)
            throw new ArgumentException($"Buffer holds {pixels.Length} bytes, need {(long)width * height}.", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Row-major, stride == Width. May be longer than Width*Height (pooled buffer).</summary>
    public byte[] Pixels { get; }

    public byte this[int x, int y] => Pixels[y * Width + x];

    public static GrayImage Allocate(int width, int height) =>
        new(width, height, new byte[width * height]);

    public GrayImage Crop(PixelRect rect)
    {
        if (rect.X < 0 || rect.Y < 0 || rect.Right > Width || rect.Bottom > Height)
            throw new ArgumentOutOfRangeException(nameof(rect), "Crop falls outside the image.");

        var dst = new byte[rect.Width * rect.Height];
        for (var row = 0; row < rect.Height; row++)
        {
            Array.Copy(Pixels, (rect.Y + row) * Width + rect.X, dst, row * rect.Width, rect.Width);
        }

        return new GrayImage(rect.Width, rect.Height, dst);
    }

    /// <summary>
    /// Box-filter downscale. The production path does this in a compute shader;
    /// this CPU version backs <c>FolderFrameSource</c> and the tests.
    /// </summary>
    public GrayImage ResizeBox(int targetWidth, int targetHeight)
    {
        if (targetWidth <= 0 || targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetWidth == Width && targetHeight == Height)
            return this;

        var dst = new byte[targetWidth * targetHeight];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = ty * Height / targetHeight;
            var y1 = Math.Max(y0 + 1, (ty + 1) * Height / targetHeight);

            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = tx * Width / targetWidth;
                var x1 = Math.Max(x0 + 1, (tx + 1) * Width / targetWidth);

                var sum = 0;
                var count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var rowStart = y * Width;
                    for (var x = x0; x < x1; x++)
                    {
                        sum += Pixels[rowStart + x];
                        count++;
                    }
                }

                dst[ty * targetWidth + tx] = (byte)(sum / count);
            }
        }

        return new GrayImage(targetWidth, targetHeight, dst);
    }
}
