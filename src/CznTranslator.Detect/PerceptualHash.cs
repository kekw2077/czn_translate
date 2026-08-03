using System.Numerics;
using CznTranslator.Core.Models;

namespace CznTranslator.Detect;

/// <summary>
/// DCT-based 64-bit perceptual hash (TZ §3).
/// <para>
/// The input is the 64×64 grayscale tile the capture stage reads back — 4 KB per zone per poll,
/// which is what makes per-frame change detection affordable. Only the top-left 8×8 DCT block is
/// needed, so the transform computes just those coefficients instead of a full 64×64 DCT.
/// </para>
/// </summary>
public static class PerceptualHash
{
    public const int InputSize = 64;
    private const int BlockSize = 8;

    /// <summary>cos((2x+1)·u·π/2N) for x in 0..63, u in 0..7.</summary>
    private static readonly float[] CosTable = BuildCosTable();

    private static float[] BuildCosTable()
    {
        var table = new float[InputSize * BlockSize];
        for (var x = 0; x < InputSize; x++)
        {
            for (var u = 0; u < BlockSize; u++)
            {
                table[x * BlockSize + u] = (float)Math.Cos((2 * x + 1) * u * Math.PI / (2.0 * InputSize));
            }
        }

        return table;
    }

    /// <summary>
    /// Hashes a tile. Anything other than <see cref="InputSize"/>² is box-downscaled first, so
    /// the CPU-side offline stand and the GPU path produce comparable hashes.
    /// </summary>
    public static ulong Compute(GrayImage tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if (tile.Width != InputSize || tile.Height != InputSize)
            tile = tile.ResizeBox(InputSize, InputSize);

        return Compute(tile.Pixels);
    }

    /// <summary>Hashes a raw 64×64 row-major grayscale buffer.</summary>
    public static ulong Compute(ReadOnlySpan<byte> tile64X64)
    {
        if (tile64X64.Length < InputSize * InputSize)
            throw new ArgumentException($"Expected at least {InputSize * InputSize} bytes, got {tile64X64.Length}.", nameof(tile64X64));

        // Rows first: 64 rows × 8 horizontal coefficients.
        Span<float> rows = stackalloc float[InputSize * BlockSize];
        for (var y = 0; y < InputSize; y++)
        {
            var rowStart = y * InputSize;
            for (var u = 0; u < BlockSize; u++)
            {
                var sum = 0f;
                for (var x = 0; x < InputSize; x++)
                    sum += tile64X64[rowStart + x] * CosTable[x * BlockSize + u];
                rows[y * BlockSize + u] = sum;
            }
        }

        // Then columns: 8 × 8 low-frequency block.
        Span<float> block = stackalloc float[BlockSize * BlockSize];
        for (var u = 0; u < BlockSize; u++)
        {
            for (var v = 0; v < BlockSize; v++)
            {
                var sum = 0f;
                for (var y = 0; y < InputSize; y++)
                    sum += rows[y * BlockSize + u] * CosTable[y * BlockSize + v];
                block[v * BlockSize + u] = sum;
            }
        }

        // The DC term carries overall brightness, which shifts with every fade and dimmer.
        // Excluding it from the median is what makes the hash survive brightness changes.
        Span<float> forMedian = stackalloc float[BlockSize * BlockSize - 1];
        forMedian.Clear();
        var written = 0;
        for (var i = 1; i < block.Length; i++)
            forMedian[written++] = block[i];

        var median = Median(forMedian);

        var hash = 0UL;
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] > median)
                hash |= 1UL << i;
        }

        return hash;
    }

    /// <summary>Number of differing bits. 0 means identical, 64 means inverted.</summary>
    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);

    private static float Median(Span<float> values)
    {
        Span<float> copy = stackalloc float[values.Length];
        values.CopyTo(copy);
        copy.Sort();

        var mid = copy.Length / 2;
        return copy.Length % 2 == 1
            ? copy[mid]
            : (copy[mid - 1] + copy[mid]) / 2f;
    }
}
