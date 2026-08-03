using System.Diagnostics;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Serilog;

namespace CznTranslator.Ocr;

/// <summary>
/// det + rec over ONNX Runtime. The execution provider is decided by the caller (TZ §4) — the
/// pipeline itself is identical for DirectML and CPU, only the session options differ.
/// </summary>
public sealed class OnnxOcrBackend : IOcrBackend
{
    private readonly InferenceSession _detection;
    private readonly InferenceSession _recognition;
    private readonly CharacterDictionary _dictionary;
    private readonly OcrSection _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);

    private readonly string _detectionInput;
    private readonly string _recognitionInput;

    public OnnxOcrBackend(
        OcrModelSet models,
        SessionOptions detectionOptions,
        SessionOptions recognitionOptions,
        OcrSection settings,
        OcrBackendInfo info,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Info = info;
        _log = log ?? Log.Logger;

        models.EnsureFilesExist();

        _detection = new InferenceSession(models.DetectionPath, detectionOptions);
        _recognition = new InferenceSession(models.RecognitionPath, recognitionOptions);
        _dictionary = CharacterDictionary.Load(models.DictionaryPath);

        _detectionInput = _detection.InputMetadata.Keys.First();
        _recognitionInput = _recognition.InputMetadata.Keys.First();
    }

    public OcrBackendInfo Info { get; }

    /// <summary>
    /// First inference on DirectML compiles shaders and costs 1–3 s. Running both models on
    /// throwaway inputs at start-up moves that cost off the first real translation, which would
    /// otherwise look like the app hanging (TZ §4, §11).
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await Task.Run(() =>
        {
            var dummy = GrayImage.Allocate(320, 96);
            dummy.Pixels.AsSpan().Fill(30);

            var geometry = DetPreprocessor.ComputeGeometry(dummy.Width, dummy.Height, _settings.Det.LimitSideLen);
            RunDetection(dummy, geometry);
            RunRecognition([GrayImage.Allocate(160, _settings.Rec.Height)]);
        }, cancellationToken).ConfigureAwait(false);

        _log.Information("OCR warm-up finished in {Elapsed} ms on {Backend}.", stopwatch.ElapsedMilliseconds, Info);
    }

    public async Task<OcrResult> RecognizeAsync(
        GrayImage roi,
        OcrRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roi);
        options ??= OcrRequestOptions.Default;

        // One inference at a time. The scheduler already enforces this, but a stray caller must
        // not be able to run two sessions concurrently on the same DML device.
        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Recognize(roi, options, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private OcrResult Recognize(GrayImage roi, OcrRequestOptions options, CancellationToken cancellationToken)
    {
        var detectStopwatch = Stopwatch.StartNew();

        IReadOnlyList<PixelRect> boxes;
        if (options.WholeRoiAsOneBlock || options.SingleLine)
        {
            // A 'block' or 'line' zone is already exactly the text region, so detection would
            // only re-derive what the zone config states.
            boxes = [new PixelRect(0, 0, roi.Width, roi.Height)];
            detectStopwatch.Stop();
        }
        else
        {
            var geometry = DetPreprocessor.ComputeGeometry(roi.Width, roi.Height, _settings.Det.LimitSideLen);
            var (map, mapWidth, mapHeight) = RunDetection(roi, geometry);
            boxes = DbPostProcessor.ExtractBoxes(map, mapWidth, mapHeight, geometry, roi.Width, roi.Height, _settings.Det);
            detectStopwatch.Stop();
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (boxes.Count == 0)
            return new OcrResult([], detectStopwatch.Elapsed.TotalMilliseconds, 0);

        var recognizeStopwatch = Stopwatch.StartNew();

        var crops = boxes.Select(roi.Crop).ToList();
        var lines = new OcrLine[boxes.Count];

        foreach (var batch in RecPreprocessor.PlanBatches(crops, _settings.Rec.Height, _settings.Rec.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var decoded = RunRecognition([.. batch.Select(index => crops[index])]);
            for (var i = 0; i < batch.Count; i++)
            {
                var sourceIndex = batch[i];
                lines[sourceIndex] = new OcrLine(decoded[i].Text, decoded[i].Confidence, boxes[sourceIndex]);
            }
        }

        recognizeStopwatch.Stop();

        // Empty reads are dropped here rather than by the caller — a box the recognizer had
        // nothing to say about is noise, not a translatable line.
        var result = lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)).ToList();

        return new OcrResult(
            result,
            detectStopwatch.Elapsed.TotalMilliseconds,
            recognizeStopwatch.Elapsed.TotalMilliseconds);
    }

    private (float[] Map, int Width, int Height) RunDetection(GrayImage roi, DetPreprocessor.DetGeometry geometry)
    {
        var tensor = DetPreprocessor.BuildTensor(roi, geometry);

        using var outputs = _detection.Run([NamedOnnxValue.CreateFromTensor(_detectionInput, tensor)]);
        var probability = outputs.First().AsTensor<float>();

        // DB emits [1, 1, H, W]; the last two dimensions are the map.
        var dimensions = probability.Dimensions;
        var height = dimensions[^2];
        var width = dimensions[^1];

        return (probability.ToArray(), width, height);
    }

    private IReadOnlyList<CtcDecodeResult> RunRecognition(IReadOnlyList<GrayImage> crops)
    {
        var tensor = RecPreprocessor.BuildBatch(crops, _settings.Rec.Height);

        using var outputs = _recognition.Run([NamedOnnxValue.CreateFromTensor(_recognitionInput, tensor)]);
        var logits = outputs.First().AsTensor<float>();

        var dimensions = logits.Dimensions;
        var timeSteps = dimensions[^2];
        var classCount = dimensions[^1];

        return CtcDecoder.DecodeBatch(logits.ToArray(), crops.Count, timeSteps, classCount, _dictionary);
    }

    public void Dispose()
    {
        _detection.Dispose();
        _recognition.Dispose();
        _inferenceGate.Dispose();
    }
}
