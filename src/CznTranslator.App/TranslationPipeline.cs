using System.Diagnostics;
using CznTranslator.Capture;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Metrics;
using CznTranslator.Core.Models;
using CznTranslator.Detect;
using CznTranslator.Overlay;
using Serilog;

namespace CznTranslator.App;

/// <summary>
/// The §1 data flow, wired end to end:
/// <code>
/// WGC frame (GPU) → crop + grayscale + resize (compute shader)
///   → pHash 64px (hash readback only) → settle → zone_cache probe
///   → ROI readback → det → rec → normalize → lookup → overlay
/// </code>
/// <para>
/// The ordering constraints are what this class exists to enforce: the frame never leaves the GPU
/// until a zone has both changed and settled, the cache is probed before OCR rather than after,
/// and at most one inference runs at a time.
/// </para>
/// </summary>
public sealed class TranslationPipeline : IAsyncDisposable
{
    private readonly IFrameSource _frameSource;
    private readonly GpuRoiExtractor? _roiExtractor;
    private readonly IOcrBackend _ocr;
    private readonly ILookupService _lookup;
    private readonly OcrScheduler _scheduler;
    private readonly ZoneChangeDetector _detector;
    private readonly MetricsCollector _metrics;
    private readonly ILogger _log;

    private readonly Dictionary<string, PixelRect> _zoneOrigins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ZoneResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _zoneChangedAtTicks = new(StringComparer.Ordinal);
    private readonly object _resultsGate = new();

    private AppConfig _config;
    private long _frameCounter;

    public TranslationPipeline(
        AppConfig config,
        IFrameSource frameSource,
        GpuRoiExtractor? roiExtractor,
        IOcrBackend ocr,
        ILookupService lookup,
        MetricsCollector metrics,
        ILogger? log = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _roiExtractor = roiExtractor;
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _log = log ?? Log.Logger;

        _detector = new ZoneChangeDetector(config.Detect.SettleMs, config.Detect.HashDistanceThreshold);
        _scheduler = new OcrScheduler(RunOcrAsync, _log);
        _frameSource.FrameArrived += OnFrameArrived;
    }

    /// <summary>Raised after a zone's translations change, so the overlay can redraw.</summary>
    public event EventHandler? ResultsChanged;

    public IReadOnlyDictionary<string, PixelRect> ZoneOrigins
    {
        get { lock (_resultsGate) return new Dictionary<string, PixelRect>(_zoneOrigins); }
    }

    public IReadOnlyList<ZoneResult> Results
    {
        get { lock (_resultsGate) return [.. _results.Values]; }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _scheduler.Start();
        await _frameSource.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Live config reload (TZ §9) — thresholds and zones take effect on the next frame.</summary>
    public void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _detector.SettleMs = config.Detect.SettleMs;
        _detector.HashDistanceThreshold = config.Detect.HashDistanceThreshold;

        // Zone geometry may have moved; anything cached against the old rects is meaningless.
        _detector.InvalidateAll();
        lock (_resultsGate)
        {
            _results.Clear();
            _zoneOrigins.Clear();
        }
    }

    /// <summary>Ctrl+Alt+R: after a patch, every cached screen is suspect.</summary>
    public async Task ClearCachesAsync()
    {
        _detector.InvalidateAll();
        _scheduler.Clear();

        lock (_resultsGate)
            _results.Clear();

        await _lookup.ClearZoneCacheAsync().ConfigureAwait(false);
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+Alt+A and the laptop profile's manual mode: force one pass over a zone.</summary>
    public void RequestManualZone(string zoneId)
    {
        _detector.Invalidate(zoneId);
    }

    private void OnFrameArrived(object? sender, CapturedFrame frame)
    {
        // The laptop profile hashes every 4th frame. On a 28 W chassis a 60 Hz poll simply
        // burns the thermal budget the OCR needs (TZ §12).
        var counter = Interlocked.Increment(ref _frameCounter);
        if (counter % _config.Detect.PollEveryNthFrame != 0)
            return;

        var captureStopwatch = Stopwatch.StartNew();

        try
        {
            foreach (var zone in _config.Zones)
            {
                if (!ZoneChangeDetector.IsPolledAutomatically(zone, _config.Detect))
                    continue;

                ProcessZone(frame, zone);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Frame processing failed.");
        }
        finally
        {
            _metrics.RecordStage(PipelineStage.Capture, captureStopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private void ProcessZone(CapturedFrame frame, ZoneConfig zone)
    {
        var region = zone.NormalizedRect.ToPixels(frame.Width, frame.Height);

        // Only the 64x64 tile crosses to the CPU here — 4 KB per zone per poll. The full ROI is
        // read back further down, and only once the zone has actually settled.
        var tile = ExtractTile(frame, region, PerceptualHash.InputSize, PerceptualHash.InputSize);
        if (tile is null)
            return;

        var hash = PerceptualHash.Compute(tile);
        var observation = _detector.Observe(zone.Id, hash, Environment.TickCount64);

        if (observation.State == ZoneState.Dirty)
        {
            _zoneChangedAtTicks[zone.Id] = Stopwatch.GetTimestamp();
            return;
        }

        if (observation.State != ZoneState.Settled)
            return;

        lock (_resultsGate)
            _zoneOrigins[zone.Id] = region;

        // Cache probe before OCR, not after: a screen we have already translated must redraw
        // without an inference at all (TZ §3, шаг 4).
        var cached = _lookup.TryGetCachedZoneAsync(zone.Id, observation.Hash).GetAwaiter().GetResult();
        if (cached is not null)
        {
            Publish(cached, zone.Id);
            return;
        }

        var roi = ExtractTile(frame, region, region.Width, region.Height);
        if (roi is null)
            return;

        _scheduler.Submit(new OcrJob(
            zone.Id,
            zone.Priority,
            observation.Hash,
            roi,
            OptionsFor(zone),
            Environment.TickCount64));
    }

    private static OcrRequestOptions OptionsFor(ZoneConfig zone) => zone.Mode switch
    {
        // A 'block' or 'line' zone is already exactly the text region, so detection would only
        // re-derive what the config states.
        ZoneMode.Block => new OcrRequestOptions(WholeRoiAsOneBlock: true),
        ZoneMode.Line => new OcrRequestOptions(SingleLine: true),
        _ => OcrRequestOptions.Default
    };

    /// <summary>
    /// Pulls a region off the frame. With a live capture this runs the compute shader and reads
    /// back only the result; with <c>FolderFrameSource</c> the frame is already on the CPU.
    /// </summary>
    private GrayImage? ExtractTile(CapturedFrame frame, PixelRect region, int width, int height)
    {
        if (frame.Cpu is not null)
            return frame.Cpu.Crop(region).ResizeBox(width, height);

        if (frame.GpuTexture is Vortice.Direct3D11.ID3D11Texture2D texture && _roiExtractor is not null)
            return _roiExtractor.Extract(texture, region, width, height);

        return null;
    }

    private async Task RunOcrAsync(OcrJob job, CancellationToken cancellationToken)
    {
        var ocrStopwatch = Stopwatch.StartNew();
        var result = await _ocr.RecognizeAsync(job.Roi, job.Options, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        _metrics.RecordOcrCall();
        _metrics.RecordStage(PipelineStage.Ocr, result.TotalMs);

        cancellationToken.ThrowIfCancellationRequested();

        var lookupStopwatch = Stopwatch.StartNew();
        var lines = new List<TranslatedLine>(result.Lines.Count);

        foreach (var line in result.Lines)
        {
            var hit = await _lookup.ResolveAsync(line.Text, line.Confidence, cancellationToken).ConfigureAwait(false);
            lines.Add(new TranslatedLine(line.Box, hit, line.Confidence));
        }

        lookupStopwatch.Stop();
        _metrics.RecordStage(PipelineStage.Lookup, lookupStopwatch.Elapsed.TotalMilliseconds);

        var zoneResult = new ZoneResult(job.ZoneId, job.ZoneHash, lines, FromCache: false);
        await _lookup.StoreZoneAsync(zoneResult, cancellationToken).ConfigureAwait(false);

        Publish(zoneResult, job.ZoneId);
    }

    private void Publish(ZoneResult result, string zoneId)
    {
        lock (_resultsGate)
            _results[zoneId] = result;

        // The §10 acceptance number is measured from the frame where the zone changed, not from
        // where OCR started — the settle wait is part of what the player perceives as lag.
        if (_zoneChangedAtTicks.TryGetValue(zoneId, out var changedAt))
        {
            var elapsed = Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds;
            _metrics.RecordStage(PipelineStage.ChangeToDraw, elapsed);
            _zoneChangedAtTicks.Remove(zoneId);
        }

        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        _frameSource.FrameArrived -= OnFrameArrived;
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        await _frameSource.DisposeAsync().ConfigureAwait(false);
        _roiExtractor?.Dispose();
        _ocr.Dispose();
    }
}
