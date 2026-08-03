using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using CznTranslator.Ocr;
using Xunit;

namespace CznTranslator.Tests;

public class CharacterDictionaryTests
{
    [Fact]
    public void Blank_is_prepended_and_space_appended()
    {
        var dictionary = CharacterDictionary.FromLines(["a", "b", "c"]);

        Assert.Equal(5, dictionary.Count);
        Assert.True(dictionary.IsBlank(0));
        Assert.Equal("a", dictionary[1]);
        Assert.Equal(" ", dictionary[4]);
    }

    [Fact]
    public void Missing_file_is_reported_by_path()
    {
        var exception = Assert.Throws<FileNotFoundException>(
            () => CharacterDictionary.Load("/nonexistent/en_dict.txt"));

        Assert.Contains("en_dict.txt", exception.Message, StringComparison.Ordinal);
    }
}

public class CtcDecoderTests
{
    private static readonly CharacterDictionary Dictionary = CharacterDictionary.FromLines(["a", "b", "c"]);

    /// <summary>Builds a [timeSteps, classes] map where each step puts <paramref name="peak"/> on the given class.</summary>
    private static float[] Sequence(IReadOnlyList<int> argmax, double peak = 0.9)
    {
        var classes = Dictionary.Count;
        var buffer = new float[argmax.Count * classes];
        var rest = (float)((1.0 - peak) / (classes - 1));

        for (var step = 0; step < argmax.Count; step++)
        {
            for (var cls = 0; cls < classes; cls++)
                buffer[step * classes + cls] = rest;
            buffer[step * classes + argmax[step]] = (float)peak;
        }

        return buffer;
    }

    [Fact]
    public void Collapses_consecutive_duplicates()
    {
        var result = CtcDecoder.Decode(Sequence([1, 1, 1]), 3, Dictionary.Count, Dictionary);
        Assert.Equal("a", result.Text);
    }

    [Fact]
    public void A_blank_between_repeats_keeps_both_characters()
    {
        // This is the whole point of the blank class: without it "ll" could never be decoded.
        var result = CtcDecoder.Decode(Sequence([1, 0, 1]), 3, Dictionary.Count, Dictionary);
        Assert.Equal("aa", result.Text);
    }

    [Fact]
    public void Blanks_never_reach_the_output()
    {
        var result = CtcDecoder.Decode(Sequence([0, 1, 0, 2, 0]), 5, Dictionary.Count, Dictionary);
        Assert.Equal("ab", result.Text);
    }

    [Fact]
    public void An_all_blank_sequence_decodes_to_nothing_with_zero_confidence()
    {
        var result = CtcDecoder.Decode(Sequence([0, 0, 0]), 3, Dictionary.Count, Dictionary);

        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Confidence_averages_only_the_steps_that_produced_characters()
    {
        // 20 blank steps at 0.99 around two characters at 0.60: averaging over every step would
        // report ~0.95 for a line that was actually read badly, and that number picks the fuzzy
        // threshold downstream.
        var classes = Dictionary.Count;
        var steps = 22;
        var buffer = new float[steps * classes];

        for (var step = 0; step < steps; step++)
        {
            for (var cls = 0; cls < classes; cls++)
                buffer[step * classes + cls] = 0.001f;

            var isCharacter = step is 5 or 15;
            buffer[step * classes + (isCharacter ? 1 : 0)] = isCharacter ? 0.60f : 0.99f;
        }

        var result = CtcDecoder.Decode(buffer, steps, classes, Dictionary);

        Assert.Equal("aa", result.Text);
        Assert.Equal(0.60, result.Confidence, precision: 5);
    }

    [Fact]
    public void Decodes_a_batch_independently_per_item()
    {
        var first = Sequence([1, 0, 2]);
        var second = Sequence([3, 3, 0]);
        var batch = first.Concat(second).ToArray();

        var results = CtcDecoder.DecodeBatch(batch, 2, 3, Dictionary.Count, Dictionary);

        Assert.Equal("ab", results[0].Text);
        Assert.Equal("c", results[1].Text);
    }

    [Fact]
    public void A_dictionary_that_does_not_match_the_model_is_rejected()
    {
        // Silently decoding with a short dictionary would produce plausible-looking garbage.
        var exception = Assert.Throws<ArgumentException>(
            () => CtcDecoder.Decode(new float[80], 10, 8, Dictionary));

        Assert.Contains("en_dict.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_buffer_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => CtcDecoder.Decode(new float[10], 5, Dictionary.Count, Dictionary));
    }
}

public class DetPreprocessorTests
{
    [Fact]
    public void Sides_are_rounded_up_to_a_multiple_of_32()
    {
        var geometry = DetPreprocessor.ComputeGeometry(400, 80, limitSideLen: 960);

        Assert.Equal(0, geometry.Width % 32);
        Assert.Equal(0, geometry.Height % 32);
        Assert.Equal(416, geometry.Width);
        Assert.Equal(96, geometry.Height);
    }

    [Fact]
    public void A_roi_smaller_than_the_limit_is_not_upscaled()
    {
        var geometry = DetPreprocessor.ComputeGeometry(400, 80, limitSideLen: 960);

        // 400 → 416 is the multiple-of-32 rounding, not a scale-up towards 960.
        Assert.True(geometry.Width < 960);
    }

    [Fact]
    public void The_longest_side_is_capped_by_the_limit()
    {
        var geometry = DetPreprocessor.ComputeGeometry(2560, 1440, limitSideLen: 640);

        Assert.True(geometry.Width <= 640 + 32);
        Assert.True(geometry.ScaleX < 0.3);
    }

    [Fact]
    public void Each_axis_reports_its_own_scale()
    {
        // Both sides round up independently, so one nominal scale would misplace every box.
        var geometry = DetPreprocessor.ComputeGeometry(400, 80, limitSideLen: 960);

        Assert.Equal(416.0 / 400, geometry.ScaleX, precision: 9);
        Assert.Equal(96.0 / 80, geometry.ScaleY, precision: 9);
    }

    [Fact]
    public void Never_produces_a_side_below_the_multiple()
    {
        var geometry = DetPreprocessor.ComputeGeometry(3, 2, limitSideLen: 640);

        Assert.Equal(32, geometry.Width);
        Assert.Equal(32, geometry.Height);
    }

    [Fact]
    public void Tensor_is_nchw_with_three_replicated_planes()
    {
        var roi = GrayImage.Allocate(64, 32);
        roi.Pixels.AsSpan().Fill(255);

        var geometry = DetPreprocessor.ComputeGeometry(64, 32, 640);
        var tensor = DetPreprocessor.BuildTensor(roi, geometry);

        Assert.Equal([1, 3, geometry.Height, geometry.Width], tensor.Dimensions.ToArray());

        // 255 → 1.0, then (1 - mean) / std per channel.
        Assert.Equal((1f - 0.485f) / 0.229f, tensor[0, 0, 0, 0], precision: 4);
        Assert.Equal((1f - 0.456f) / 0.224f, tensor[0, 1, 0, 0], precision: 4);
        Assert.Equal((1f - 0.406f) / 0.225f, tensor[0, 2, 0, 0], precision: 4);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void An_empty_roi_is_rejected(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DetPreprocessor.ComputeGeometry(width, height, 640));
    }

    [Fact]
    public void A_limit_below_one_tile_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DetPreprocessor.ComputeGeometry(400, 80, 16));
    }
}

public class RecPreprocessorTests
{
    [Fact]
    public void Width_scales_with_the_aspect_ratio()
    {
        Assert.Equal(96, RecPreprocessor.ScaledWidth(cropWidth: 200, cropHeight: 100, targetHeight: 48));
        Assert.Equal(48, RecPreprocessor.ScaledWidth(cropWidth: 50, cropHeight: 50, targetHeight: 48));
    }

    [Fact]
    public void Batch_is_padded_to_the_widest_member()
    {
        var crops = new[]
        {
            GrayImage.Allocate(200, 48),
            GrayImage.Allocate(80, 48)
        };

        var tensor = RecPreprocessor.BuildBatch(crops, 48);

        Assert.Equal(2, tensor.Dimensions[0]);
        Assert.Equal(3, tensor.Dimensions[1]);
        Assert.Equal(48, tensor.Dimensions[2]);
        Assert.Equal(200, tensor.Dimensions[3]);
    }

    [Fact]
    public void Padding_is_mid_gray_rather_than_black()
    {
        // Zero in normalized space is gray 127.5. Padding with black would leave a hard edge
        // beside the last glyph and the recognizer reads phantom characters off it.
        var crops = new[]
        {
            GrayImage.Allocate(200, 48),
            GrayImage.Allocate(80, 48)
        };
        crops[1].Pixels.AsSpan().Fill(255);

        var tensor = RecPreprocessor.BuildBatch(crops, 48);

        Assert.Equal(0f, tensor[1, 0, 0, 150]);
        Assert.Equal(1f, tensor[1, 0, 0, 10], precision: 2);
    }

    [Fact]
    public void Normalization_maps_the_full_range_onto_minus_one_to_one()
    {
        var black = GrayImage.Allocate(48, 48);
        var tensor = RecPreprocessor.BuildBatch([black], 48);

        Assert.Equal(-1f, tensor[0, 0, 0, 0]);
    }

    [Fact]
    public void Batches_respect_the_size_cap()
    {
        var crops = Enumerable.Range(0, 10).Select(_ => GrayImage.Allocate(100, 48)).ToList();

        var batches = RecPreprocessor.PlanBatches(crops, 48, maxBatchSize: 4);

        Assert.Equal(3, batches.Count);
        Assert.All(batches, batch => Assert.True(batch.Count <= 4));
        Assert.Equal(10, batches.Sum(b => b.Count));
    }

    [Fact]
    public void Batching_groups_similar_widths_together()
    {
        // Mixing a 20 px label with a 900 px sentence would pad the label out 45x — pure waste.
        var crops = new[]
        {
            GrayImage.Allocate(900, 48),
            GrayImage.Allocate(20, 48),
            GrayImage.Allocate(880, 48),
            GrayImage.Allocate(25, 48)
        };

        var batches = RecPreprocessor.PlanBatches(crops, 48, maxBatchSize: 2);

        Assert.Equal([1, 3], batches[0].OrderBy(i => i).ToArray());
        Assert.Equal([0, 2], batches[1].OrderBy(i => i).ToArray());
    }

    [Fact]
    public void Every_index_survives_the_plan()
    {
        var crops = Enumerable.Range(1, 7).Select(i => GrayImage.Allocate(i * 30, 48)).ToList();

        var planned = RecPreprocessor.PlanBatches(crops, 48, 3).SelectMany(b => b).OrderBy(i => i);

        Assert.Equal(Enumerable.Range(0, 7), planned);
    }

    [Fact]
    public void An_empty_batch_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => RecPreprocessor.BuildBatch([], 48));
    }
}

public class AdapterSelectionTests
{
    private static GraphicsAdapter Adapter(int index, string name, ulong vramMb, bool software = false) =>
        new(index, name, vramMb * 1024 * 1024, software);

    [Fact]
    public void Picks_the_adapter_with_the_most_dedicated_memory()
    {
        var decision = AdapterSelection.Decide(new OcrSection(),
        [
            Adapter(0, "Intel Iris Xe Graphics", 128),
            Adapter(1, "NVIDIA GeForce RTX 4070", 12288)
        ]);

        Assert.Equal(OcrProviderKind.DirectMl, decision.Kind);
        Assert.Equal(1, decision.Adapter!.Index);
    }

    [Fact]
    public void Software_adapters_are_discarded()
    {
        // WARP accepts the DML provider and then runs the model on the CPU through a graphics
        // driver — strictly worse than the CPU execution provider.
        var decision = AdapterSelection.Decide(new OcrSection(),
        [
            Adapter(0, "Microsoft Basic Render Driver", 0, software: true)
        ]);

        Assert.Equal(OcrProviderKind.Cpu, decision.Kind);
    }

    [Fact]
    public void The_laptop_igpu_is_selected_when_it_is_the_only_adapter()
    {
        // Iris Xe supports DirectML fully, so the cascade stops here and CpuOcrBackend stays
        // unused — TZ §12.
        var decision = AdapterSelection.Decide(new OcrSection(), [Adapter(0, "Intel Iris Xe Graphics", 128)]);

        Assert.Equal(OcrProviderKind.DirectMl, decision.Kind);
        Assert.Equal("Intel Iris Xe Graphics", decision.Adapter!.Description);
    }

    [Fact]
    public void No_adapters_at_all_falls_back_to_cpu()
    {
        var decision = AdapterSelection.Decide(new OcrSection(), []);

        Assert.Equal(OcrProviderKind.Cpu, decision.Kind);
        Assert.Null(decision.Adapter);
    }

    [Fact]
    public void Provider_pinned_to_cpu_skips_enumeration_entirely()
    {
        var decision = AdapterSelection.Decide(
            new OcrSection { Provider = OcrProviderKind.Cpu },
            [Adapter(0, "NVIDIA GeForce RTX 4070", 12288)]);

        Assert.Equal(OcrProviderKind.Cpu, decision.Kind);
    }

    [Fact]
    public void A_pinned_adapter_index_wins_over_the_vram_ordering()
    {
        var decision = AdapterSelection.Decide(
            new OcrSection { AdapterIndex = 0 },
            [
                Adapter(0, "Intel Iris Xe Graphics", 128),
                Adapter(1, "NVIDIA GeForce RTX 4070", 12288)
            ]);

        Assert.Equal(0, decision.Adapter!.Index);
    }

    [Fact]
    public void A_stale_pinned_index_degrades_to_automatic_selection_in_auto_mode()
    {
        // Moving a card or a driver reshuffle changes indices; that should not brick startup.
        var decision = AdapterSelection.Decide(
            new OcrSection { AdapterIndex = 7 },
            [Adapter(0, "Intel Iris Xe Graphics", 128)]);

        Assert.Equal(OcrProviderKind.DirectMl, decision.Kind);
        Assert.Equal(0, decision.Adapter!.Index);
        Assert.Contains("falling back", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pinning_dml_with_no_usable_adapter_fails_loudly()
    {
        // The user explicitly asked for DirectML; silently running 5x slower on the CPU would be
        // the wrong kind of helpful.
        Assert.Throws<InvalidOperationException>(() => AdapterSelection.Decide(
            new OcrSection { Provider = OcrProviderKind.DirectMl },
            [Adapter(0, "Microsoft Basic Render Driver", 0, software: true)]));
    }

    [Fact]
    public void Pinning_dml_to_a_missing_index_fails_loudly()
    {
        Assert.Throws<InvalidOperationException>(() => AdapterSelection.Decide(
            new OcrSection { Provider = OcrProviderKind.DirectMl, AdapterIndex = 3 },
            [Adapter(0, "Intel Iris Xe Graphics", 128)]));
    }

    [Fact]
    public void The_reason_names_the_adapter_for_the_log_and_the_tray()
    {
        var decision = AdapterSelection.Decide(new OcrSection(), [Adapter(0, "AMD Radeon Graphics", 512)]);

        Assert.Contains("AMD Radeon Graphics", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("512 MB", decision.Reason, StringComparison.Ordinal);
    }
}

public class OcrModelSetTests
{
    [Fact]
    public void Full_precision_paths_match_the_names_in_the_spec()
    {
        var models = OcrModelSet.Resolve(new OcrSection { ModelsDirectory = "models" });

        Assert.EndsWith("ch_PP-OCRv4_det_infer.onnx", models.DetectionPath, StringComparison.Ordinal);
        Assert.EndsWith("en_PP-OCRv4_rec_infer.onnx", models.RecognitionPath, StringComparison.Ordinal);
        Assert.EndsWith("en_dict.txt", models.DictionaryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void The_quantized_profile_switches_both_models_but_not_the_dictionary()
    {
        var models = OcrModelSet.Resolve(new OcrSection { ModelsDirectory = "models", Quantized = true });

        Assert.EndsWith("ch_PP-OCRv4_det_infer_quant.onnx", models.DetectionPath, StringComparison.Ordinal);
        Assert.EndsWith("en_PP-OCRv4_rec_infer_quant.onnx", models.RecognitionPath, StringComparison.Ordinal);
        Assert.EndsWith("en_dict.txt", models.DictionaryPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_files_are_reported_together()
    {
        var models = OcrModelSet.Resolve(new OcrSection { ModelsDirectory = "/nonexistent-models" });

        var exception = Assert.Throws<FileNotFoundException>(models.EnsureFilesExist);

        Assert.Contains("det_infer.onnx", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rec_infer.onnx", exception.Message, StringComparison.Ordinal);
        Assert.Contains("en_dict.txt", exception.Message, StringComparison.Ordinal);
    }
}

public class CpuTopologyTests
{
    [Fact]
    public void Detection_always_yields_at_least_one_core()
    {
        var topology = CpuTopology.Detect();

        Assert.True(topology.PhysicalCores >= 1);
        Assert.True(topology.PerformanceCores >= 1);
        Assert.True(topology.PerformanceCores <= topology.PhysicalCores);
    }

    [Fact]
    public void A_hybrid_cpu_reports_only_its_performance_cores()
    {
        // 6P + 8E: ORT must be given 6, not 14 — the pool syncs to its slowest member.
        var topology = CpuTopology.Fixed(physicalCores: 14, performanceCores: 6, isHybrid: true);

        Assert.Equal(6, topology.PerformanceCores);
        Assert.True(topology.IsHybrid);
    }

    [Fact]
    public void Values_are_floored_at_one()
    {
        var topology = CpuTopology.Fixed(0, 0, false);

        Assert.Equal(1, topology.PhysicalCores);
        Assert.Equal(1, topology.PerformanceCores);
    }
}

public class DbPostProcessorTests
{
    [Fact]
    public void Unclip_expands_the_box_by_area_over_perimeter()
    {
        var rect = new OpenCvSharp.RotatedRect(
            new OpenCvSharp.Point2f(50, 50),
            new OpenCvSharp.Size2f(100, 20),
            0);

        var expanded = DbPostProcessor.Unclip(rect, 1.6);

        // distance = 100*20*1.6 / (2*(100+20)) = 13.33, applied to both sides of each axis.
        Assert.Equal(126.67f, expanded.Size.Width, precision: 1);
        Assert.Equal(46.67f, expanded.Size.Height, precision: 1);
        Assert.Equal(rect.Center.X, expanded.Center.X);
    }

    [Fact]
    public void Unclip_leaves_a_degenerate_box_alone()
    {
        var rect = new OpenCvSharp.RotatedRect(
            new OpenCvSharp.Point2f(0, 0),
            new OpenCvSharp.Size2f(0, 0),
            0);

        Assert.Equal(0, DbPostProcessor.Unclip(rect, 1.6).Size.Width);
    }

    [Fact]
    public void Boxes_map_back_through_the_per_axis_scale()
    {
        var geometry = new DetPreprocessor.DetGeometry(416, 96, 416.0 / 400, 96.0 / 80);

        var mapped = DbPostProcessor.MapToRoi(new OpenCvSharp.Rect(104, 24, 208, 48), geometry, 400, 80);

        Assert.Equal(100, mapped.X);
        Assert.Equal(20, mapped.Y);
        Assert.Equal(200, mapped.Width);
        Assert.Equal(40, mapped.Height);
    }

    [Fact]
    public void A_box_running_off_the_edge_is_clamped_into_the_roi()
    {
        var geometry = new DetPreprocessor.DetGeometry(416, 96, 416.0 / 400, 96.0 / 80);

        var mapped = DbPostProcessor.MapToRoi(new OpenCvSharp.Rect(-20, -20, 900, 900), geometry, 400, 80);

        Assert.Equal(0, mapped.X);
        Assert.Equal(0, mapped.Y);
        Assert.Equal(400, mapped.Width);
        Assert.Equal(80, mapped.Height);
    }

    [Fact]
    public void A_mapped_box_is_never_degenerate()
    {
        var geometry = new DetPreprocessor.DetGeometry(416, 96, 416.0 / 400, 96.0 / 80);

        var mapped = DbPostProcessor.MapToRoi(new OpenCvSharp.Rect(10, 10, 0, 0), geometry, 400, 80);

        Assert.True(mapped.Width >= 1);
        Assert.True(mapped.Height >= 1);
    }
}
