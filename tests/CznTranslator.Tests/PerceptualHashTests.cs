using CznTranslator.Core.Models;
using CznTranslator.Detect;
using Xunit;

namespace CznTranslator.Tests;

public class PerceptualHashTests
{
    private static GrayImage Tile(Func<int, int, byte> generator)
    {
        var image = GrayImage.Allocate(PerceptualHash.InputSize, PerceptualHash.InputSize);
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            image.Pixels[y * image.Width + x] = generator(x, y);
        return image;
    }

    /// <summary>
    /// Bright word-shaped runs on four text lines over a dark panel — a stand-in for a dialogue
    /// box. Deliberately not a strictly periodic pattern: a perfectly regular grid makes most DCT
    /// coefficients collapse onto the same value, and then float rounding alone flips bits around
    /// the median. Real UI content does not do that, and neither should the fixture.
    /// </summary>
    private static GrayImage TextLike(int seed)
    {
        var random = new Random(seed);
        var image = GrayImage.Allocate(PerceptualHash.InputSize, PerceptualHash.InputSize);

        for (var line = 0; line < 4; line++)
        {
            var top = line * 16 + 3;
            var x = 2;
            while (x < 60)
            {
                var width = random.Next(2, 6);
                if (random.NextDouble() < 0.65)
                {
                    for (var y = top; y < top + 9 && y < image.Height; y++)
                    for (var glyphX = x; glyphX < x + width && glyphX < image.Width; glyphX++)
                        image.Pixels[y * image.Width + glyphX] = (byte)random.Next(200, 250);
                }

                x += width + random.Next(1, 3);
            }
        }

        for (var i = 0; i < image.Pixels.Length; i++)
        {
            if (image.Pixels[i] == 0)
                image.Pixels[i] = (byte)random.Next(12, 30);
        }

        return image;
    }

    [Fact]
    public void Same_content_hashes_identically()
    {
        Assert.Equal(PerceptualHash.Compute(TextLike(1)), PerceptualHash.Compute(TextLike(1)));
    }

    [Fact]
    public void Different_content_is_far_apart()
    {
        var distance = PerceptualHash.Hamming(
            PerceptualHash.Compute(TextLike(7)),
            PerceptualHash.Compute(TextLike(9)));

        Assert.True(distance > 4, $"Distinct screens must exceed the default threshold, got {distance}.");
    }

    [Fact]
    public void A_uniform_brightness_shift_does_not_trip_the_detector()
    {
        // Excluding the DC coefficient from the median is what buys this. A dimmed panel or a
        // fade overlay must not look like new text.
        var baseline = TextLike(7);
        var brighter = Tile((x, y) => (byte)Math.Min(255, baseline[x, y] + 18));

        var distance = PerceptualHash.Hamming(
            PerceptualHash.Compute(baseline),
            PerceptualHash.Compute(brighter));

        Assert.True(distance <= 4, $"Brightness-only change produced a distance of {distance}.");
    }

    [Fact]
    public void Non_64px_input_is_downscaled_rather_than_rejected()
    {
        var large = new GrayImage(128, 128, new byte[128 * 128]);
        for (var i = 0; i < large.Pixels.Length; i++)
            large.Pixels[i] = (byte)(i % 251);

        var exception = Record.Exception(() => PerceptualHash.Compute(large));
        Assert.Null(exception);
    }

    [Fact]
    public void Hamming_covers_the_full_range()
    {
        Assert.Equal(0, PerceptualHash.Hamming(0xDEADBEEFUL, 0xDEADBEEFUL));
        Assert.Equal(64, PerceptualHash.Hamming(0UL, ulong.MaxValue));
    }

    [Fact]
    public void Rejects_a_short_buffer()
    {
        Assert.Throws<ArgumentException>(() => PerceptualHash.Compute(new byte[100].AsSpan()));
    }
}
