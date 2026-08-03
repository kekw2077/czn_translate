using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using OpenCvSharp;

namespace CznTranslator.Ocr;

/// <summary>
/// DB detector post-processing (TZ §4): threshold the probability map, trace contours, take the
/// minimum-area rectangle, expand it by the unclip ratio, drop specks.
/// </summary>
public static class DbPostProcessor
{
    /// <summary>
    /// Boxes in ROI pixel space, sorted top-to-bottom then left-to-right so the overlay draws
    /// lines in reading order.
    /// </summary>
    public static IReadOnlyList<PixelRect> ExtractBoxes(
        ReadOnlySpan<float> probabilityMap,
        int mapWidth,
        int mapHeight,
        DetPreprocessor.DetGeometry geometry,
        int roiWidth,
        int roiHeight,
        DetSection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (probabilityMap.Length < (long)mapWidth * mapHeight)
        {
            throw new ArgumentException(
                $"Probability map holds {probabilityMap.Length} values, expected {(long)mapWidth * mapHeight}.",
                nameof(probabilityMap));
        }

        using var binary = new Mat(mapHeight, mapWidth, MatType.CV_8UC1);
        var indexer = binary.GetGenericIndexer<byte>();

        for (var y = 0; y < mapHeight; y++)
        for (var x = 0; x < mapWidth; x++)
            indexer[y, x] = probabilityMap[y * mapWidth + x] > settings.Threshold ? (byte)255 : (byte)0;

        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        var boxes = new List<PixelRect>();
        foreach (var contour in contours)
        {
            if (contour.Length < 4)
                continue;

            var rotated = Cv2.MinAreaRect(contour);
            if (rotated.Size.Width < 1 || rotated.Size.Height < 1)
                continue;

            var expanded = Unclip(rotated, settings.UnclipRatio);
            var bounds = expanded.BoundingRect();

            var mapped = MapToRoi(bounds, geometry, roiWidth, roiHeight);
            if (mapped.Area < settings.MinArea)
                continue;

            boxes.Add(mapped);
        }

        boxes.Sort((a, b) =>
        {
            // Two boxes on the same visual line rarely share an exact top, so compare with a
            // tolerance of half a line height before falling back to horizontal order.
            var tolerance = Math.Max(4, Math.Min(a.Height, b.Height) / 2);
            if (Math.Abs(a.Y - b.Y) > tolerance)
                return a.Y.CompareTo(b.Y);
            return a.X.CompareTo(b.X);
        });

        return boxes;
    }

    /// <summary>
    /// Expands a box outward by <c>area · ratio / perimeter</c>.
    /// <para>
    /// PaddleOCR runs a real polygon offset (Clipper) here. For the axis-aligned text of a game
    /// UI the closed-form expansion of the rectangle is equivalent within a pixel, and it avoids
    /// dragging in a polygon-clipping dependency for that one call.
    /// </para>
    /// </summary>
    internal static RotatedRect Unclip(RotatedRect rect, double ratio)
    {
        var width = rect.Size.Width;
        var height = rect.Size.Height;
        var perimeter = 2 * (width + height);
        if (perimeter <= 0)
            return rect;

        var distance = width * height * ratio / perimeter;

        return new RotatedRect(
            rect.Center,
            new Size2f((float)(width + 2 * distance), (float)(height + 2 * distance)),
            rect.Angle);
    }

    /// <summary>
    /// Maps a box from detector-input space back to ROI pixels. The two axes have their own scale
    /// because each was rounded up to a multiple of 32 independently.
    /// </summary>
    internal static PixelRect MapToRoi(
        Rect box,
        DetPreprocessor.DetGeometry geometry,
        int roiWidth,
        int roiHeight)
    {
        var left = (int)Math.Floor(box.X / geometry.ScaleX);
        var top = (int)Math.Floor(box.Y / geometry.ScaleY);
        var right = (int)Math.Ceiling((box.X + box.Width) / geometry.ScaleX);
        var bottom = (int)Math.Ceiling((box.Y + box.Height) / geometry.ScaleY);

        left = Math.Clamp(left, 0, Math.Max(0, roiWidth - 1));
        top = Math.Clamp(top, 0, Math.Max(0, roiHeight - 1));
        right = Math.Clamp(right, left + 1, roiWidth);
        bottom = Math.Clamp(bottom, top + 1, roiHeight);

        return new PixelRect(left, top, right - left, bottom - top);
    }
}
