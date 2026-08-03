using CznTranslator.Core.Models;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CznTranslator.Ocr;

/// <summary>Detector input geometry and tensor packing (TZ §4, «Препроцессинг det»).</summary>
public static class DetPreprocessor
{
    /// <summary>DB was trained with ImageNet statistics; changing these silently ruins recall.</summary>
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Std = [0.229f, 0.224f, 0.225f];

    public const int SideMultiple = 32;

    public readonly record struct DetGeometry(int Width, int Height, double ScaleX, double ScaleY);

    /// <summary>
    /// Scales the ROI so its longest side fits <paramref name="limitSideLen"/>, then rounds both
    /// sides up to a multiple of 32 as the network requires. A ROI already smaller than the limit
    /// is not upscaled — that would only cost time, the detector does not gain from it.
    /// </summary>
    public static DetGeometry ComputeGeometry(int width, int height, int limitSideLen)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "ROI must be non-empty.");
        if (limitSideLen < SideMultiple)
            throw new ArgumentOutOfRangeException(nameof(limitSideLen), $"limitSideLen must be at least {SideMultiple}.");

        var longest = Math.Max(width, height);
        var scale = longest > limitSideLen ? (double)limitSideLen / longest : 1.0;

        var targetWidth = RoundUpToMultiple((int)Math.Round(width * scale));
        var targetHeight = RoundUpToMultiple((int)Math.Round(height * scale));

        // The two axes round independently, so the effective scale differs slightly per axis.
        // Boxes have to be mapped back with these, not with the nominal scale.
        return new DetGeometry(
            targetWidth,
            targetHeight,
            (double)targetWidth / width,
            (double)targetHeight / height);
    }

    private static int RoundUpToMultiple(int value)
    {
        var rounded = (value + SideMultiple - 1) / SideMultiple * SideMultiple;
        return Math.Max(SideMultiple, rounded);
    }

    /// <summary>
    /// Packs the ROI into an NCHW float tensor. The source is single-channel, so the same
    /// grayscale value is written to all three planes with each plane's own normalization.
    /// </summary>
    public static DenseTensor<float> BuildTensor(GrayImage roi, DetGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(roi);

        var resized = roi.ResizeBox(geometry.Width, geometry.Height);
        var tensor = new DenseTensor<float>([1, 3, geometry.Height, geometry.Width]);
        var plane = geometry.Width * geometry.Height;
        var buffer = tensor.Buffer.Span;

        for (var i = 0; i < plane; i++)
        {
            var value = resized.Pixels[i] / 255f;
            buffer[i] = (value - Mean[0]) / Std[0];
            buffer[plane + i] = (value - Mean[1]) / Std[1];
            buffer[2 * plane + i] = (value - Mean[2]) / Std[2];
        }

        return tensor;
    }
}

/// <summary>Recognition input geometry and batching (TZ §4, «Препроцессинг rec»).</summary>
public static class RecPreprocessor
{
    /// <summary>Width the crop is scaled to at the model's fixed height, aspect preserved.</summary>
    public static int ScaledWidth(int cropWidth, int cropHeight, int targetHeight)
    {
        if (cropWidth <= 0 || cropHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(cropWidth), "Crop must be non-empty.");

        return Math.Max(1, (int)Math.Ceiling((double)targetHeight * cropWidth / cropHeight));
    }

    /// <summary>
    /// Packs a batch of line crops into one NCHW tensor at the batch's widest scaled width.
    /// <para>
    /// Padding is zero <em>after</em> normalization, i.e. mid-gray, which is what PaddleOCR does.
    /// Padding with black would put a hard edge next to the last glyph and the model reads
    /// phantom characters off it.
    /// </para>
    /// </summary>
    public static DenseTensor<float> BuildBatch(IReadOnlyList<GrayImage> crops, int targetHeight)
    {
        ArgumentNullException.ThrowIfNull(crops);
        if (crops.Count == 0)
            throw new ArgumentException("Batch must hold at least one crop.", nameof(crops));

        var widths = new int[crops.Count];
        var batchWidth = 1;
        for (var i = 0; i < crops.Count; i++)
        {
            widths[i] = ScaledWidth(crops[i].Width, crops[i].Height, targetHeight);
            batchWidth = Math.Max(batchWidth, widths[i]);
        }

        var tensor = new DenseTensor<float>([crops.Count, 3, targetHeight, batchWidth]);
        var buffer = tensor.Buffer.Span;
        var plane = targetHeight * batchWidth;
        var itemStride = 3 * plane;

        for (var item = 0; item < crops.Count; item++)
        {
            var resized = crops[item].ResizeBox(widths[item], targetHeight);
            var itemOffset = item * itemStride;

            for (var y = 0; y < targetHeight; y++)
            {
                for (var x = 0; x < widths[item]; x++)
                {
                    // Normalization to [-1, 1]: mean = 0.5, std = 0.5.
                    var value = resized.Pixels[y * widths[item] + x] / 127.5f - 1f;
                    var pixel = y * batchWidth + x;
                    buffer[itemOffset + pixel] = value;
                    buffer[itemOffset + plane + pixel] = value;
                    buffer[itemOffset + 2 * plane + pixel] = value;
                }
            }
        }

        return tensor;
    }

    /// <summary>
    /// Splits lines into batches, grouping by similar aspect so a short label is not padded out to
    /// the width of a full sentence — that padding is pure wasted inference (TZ §4).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> PlanBatches(
        IReadOnlyList<GrayImage> crops,
        int targetHeight,
        int maxBatchSize)
    {
        ArgumentNullException.ThrowIfNull(crops);
        if (maxBatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Batch size must be at least 1.");

        var order = Enumerable.Range(0, crops.Count)
            .OrderBy(i => ScaledWidth(crops[i].Width, crops[i].Height, targetHeight))
            .ToList();

        var batches = new List<IReadOnlyList<int>>();
        for (var start = 0; start < order.Count; start += maxBatchSize)
            batches.Add(order.GetRange(start, Math.Min(maxBatchSize, order.Count - start)));

        return batches;
    }
}
