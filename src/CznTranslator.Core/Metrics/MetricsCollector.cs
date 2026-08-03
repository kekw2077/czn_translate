using CznTranslator.Core.Models;

namespace CznTranslator.Core.Metrics;

public sealed record MetricsSnapshot(
    string Day,
    long OcrCalls,
    long CacheHits,
    long ExactHits,
    long FuzzyHits,
    long LlmCalls,
    long Misses,
    double AverageMs)
{
    /// <summary>
    /// TZ §9 coverage: the share of resolutions that were answered from local data.
    /// <para>
    /// The TZ writes it as <c>(cache_hits + exact + fuzzy) / ocr_calls</c>, but cache hits by
    /// definition never reach OCR, so <c>ocr_calls</c> cannot be the denominator without the
    /// number exceeding 1 on a well-cached session. The denominator here is every resolution
    /// attempt — cache hits included — which is the quantity the 90% target is about.
    /// </para>
    /// </summary>
    public double Coverage
    {
        get
        {
            var total = CacheHits + ExactHits + FuzzyHits + LlmCalls + Misses;
            return total == 0 ? 1.0 : (double)(CacheHits + ExactHits + FuzzyHits) / total;
        }
    }
}

/// <summary>Persistence for the daily <c>metrics</c> table. Implemented in CznTranslator.Lookup.</summary>
public interface IMetricsStore
{
    Task UpsertAsync(MetricsSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<MetricsSnapshot?> LoadAsync(string day, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory daily counters plus per-stage latency. Rolls over at local midnight so a session
/// that runs past midnight does not smear two days together.
/// </summary>
public sealed class MetricsCollector
{
    private readonly object _gate = new();
    private readonly Dictionary<PipelineStage, LatencyTracker> _stages = new();
    private readonly TimeProvider _time;

    private string _day;
    private long _ocrCalls;
    private long _cacheHits;
    private long _exactHits;
    private long _fuzzyHits;
    private long _llmCalls;
    private long _misses;
    private long _latencySamples;
    private double _latencySum;

    public MetricsCollector(TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _day = Today();

        foreach (var stage in Enum.GetValues<PipelineStage>())
            _stages[stage] = new LatencyTracker();
    }

    /// <summary>Fires when the day rolls over, carrying the completed day's snapshot for persistence.</summary>
    public event EventHandler<MetricsSnapshot>? DayRolled;

    public LatencyTracker Stage(PipelineStage stage) => _stages[stage];

    public void RecordStage(PipelineStage stage, double milliseconds)
    {
        _stages[stage].Record(milliseconds);

        if (stage != PipelineStage.ChangeToDraw)
            return;

        lock (_gate)
        {
            RollIfNeeded();
            _latencySum += milliseconds;
            _latencySamples++;
        }
    }

    /// <summary>One OCR inference actually ran (i.e. the zone cache did not answer).</summary>
    public void RecordOcrCall()
    {
        lock (_gate)
        {
            RollIfNeeded();
            _ocrCalls++;
        }
    }

    public void RecordResolution(LookupSource source)
    {
        lock (_gate)
        {
            RollIfNeeded();
            switch (source)
            {
                case LookupSource.Correction:
                case LookupSource.Exact:
                    _exactHits++;
                    break;
                case LookupSource.Fuzzy:
                    _fuzzyHits++;
                    break;
                case LookupSource.Llm:
                    _llmCalls++;
                    break;
                case LookupSource.Miss:
                    _misses++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(source), source, "Unhandled lookup source.");
            }
        }
    }

    public void RecordZoneCacheHit()
    {
        lock (_gate)
        {
            RollIfNeeded();
            _cacheHits++;
        }
    }

    public MetricsSnapshot Snapshot()
    {
        lock (_gate)
        {
            RollIfNeeded();
            return BuildSnapshot();
        }
    }

    private MetricsSnapshot BuildSnapshot() =>
        new(
            _day,
            _ocrCalls,
            _cacheHits,
            _exactHits,
            _fuzzyHits,
            _llmCalls,
            _misses,
            _latencySamples == 0 ? 0 : _latencySum / _latencySamples);

    private void RollIfNeeded()
    {
        var today = Today();
        if (today == _day)
            return;

        var finished = BuildSnapshot();

        _day = today;
        _ocrCalls = _cacheHits = _exactHits = _fuzzyHits = _llmCalls = _misses = 0;
        _latencySamples = 0;
        _latencySum = 0;

        DayRolled?.Invoke(this, finished);
    }

    private string Today() => _time.GetLocalNow().ToString("yyyy-MM-dd");
}
