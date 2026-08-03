using CznTranslator.Core.Metrics;
using CznTranslator.Core.Models;
using Xunit;

namespace CznTranslator.Tests;

public class LatencyTrackerTests
{
    [Fact]
    public void Empty_tracker_reports_nan_rather_than_zero()
    {
        var tracker = new LatencyTracker();
        Assert.True(double.IsNaN(tracker.Percentile(0.95)));
        Assert.True(double.IsNaN(tracker.Mean()));
    }

    [Fact]
    public void Percentiles_use_nearest_rank()
    {
        var tracker = new LatencyTracker();
        for (var i = 1; i <= 100; i++)
            tracker.Record(i);

        Assert.Equal(50, tracker.Percentile(0.50));
        Assert.Equal(95, tracker.Percentile(0.95));
        Assert.Equal(100, tracker.Percentile(1.0));
        Assert.Equal(1, tracker.Percentile(0.0));
    }

    [Fact]
    public void The_ring_keeps_only_the_most_recent_samples()
    {
        // A session runs for hours; an unbounded tracker would report the warm-up forever.
        var tracker = new LatencyTracker(capacity: 10);
        for (var i = 0; i < 100; i++)
            tracker.Record(i);

        Assert.Equal(10, tracker.Count);
        Assert.Equal(99, tracker.Percentile(1.0));
        Assert.Equal(90, tracker.Percentile(0.0));
    }

    [Fact]
    public void Rejects_a_percentile_outside_zero_to_one()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyTracker().Percentile(1.5));
    }
}

public class MetricsCollectorTests
{
    [Fact]
    public void Coverage_counts_local_answers_against_every_resolution()
    {
        var metrics = new MetricsCollector();

        for (var i = 0; i < 80; i++) metrics.RecordZoneCacheHit();
        for (var i = 0; i < 10; i++) metrics.RecordResolution(LookupSource.Exact);
        for (var i = 0; i < 5; i++) metrics.RecordResolution(LookupSource.Fuzzy);
        for (var i = 0; i < 3; i++) metrics.RecordResolution(LookupSource.Llm);
        for (var i = 0; i < 2; i++) metrics.RecordResolution(LookupSource.Miss);

        Assert.Equal(0.95, metrics.Snapshot().Coverage, precision: 6);
    }

    [Fact]
    public void Corrections_count_as_exact_hits()
    {
        // A correction is a confirmed exact alias — counting it separately would understate
        // coverage exactly on the screens the system has learned best.
        var metrics = new MetricsCollector();
        metrics.RecordResolution(LookupSource.Correction);

        Assert.Equal(1, metrics.Snapshot().ExactHits);
    }

    [Fact]
    public void An_idle_session_reports_full_coverage_rather_than_a_divide_by_zero()
    {
        Assert.Equal(1.0, new MetricsCollector().Snapshot().Coverage);
    }

    [Fact]
    public void Average_latency_tracks_the_end_to_end_stage_only()
    {
        var metrics = new MetricsCollector();

        metrics.RecordStage(PipelineStage.ChangeToDraw, 100);
        metrics.RecordStage(PipelineStage.ChangeToDraw, 200);
        metrics.RecordStage(PipelineStage.Ocr, 5000);

        Assert.Equal(150, metrics.Snapshot().AverageMs);
    }

    [Fact]
    public void Counters_roll_over_at_midnight()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 3, 23, 59, 0, TimeSpan.Zero));
        var metrics = new MetricsCollector(clock);

        metrics.RecordResolution(LookupSource.Exact);

        MetricsSnapshot? rolled = null;
        metrics.DayRolled += (_, snapshot) => rolled = snapshot;

        clock.Advance(TimeSpan.FromMinutes(2));
        var today = metrics.Snapshot();

        Assert.NotNull(rolled);
        Assert.Equal(1, rolled!.ExactHits);
        Assert.Equal("2026-08-03", rolled.Day);

        Assert.Equal(0, today.ExactHits);
        Assert.Equal("2026-08-04", today.Day);
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
