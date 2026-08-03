using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Models;

namespace CznTranslator.Detect;

/// <summary>One zone waiting for recognition.</summary>
public sealed record OcrJob(
    string ZoneId,
    int Priority,
    ulong ZoneHash,
    GrayImage Roi,
    OcrRequestOptions Options,
    long EnqueuedAtMs)
{
    /// <summary>Set when the job was pushed back after being preempted, so it is not re-preempted forever.</summary>
    public int PreemptionCount { get; init; }
}

/// <summary>
/// Ordering policy for the single OCR queue (TZ §3): lowest priority number first, FIFO within
/// a priority. At most one job per zone is pending — a zone that changes twice before it is
/// recognized should be OCR'd once, on its latest content, not twice.
/// </summary>
public sealed class OcrJobQueue
{
    private readonly List<OcrJob> _jobs = [];

    public int Count => _jobs.Count;

    /// <summary>
    /// Adds a job, replacing any pending job for the same zone.
    /// Returns true when an older job for that zone was displaced.
    /// </summary>
    public bool Enqueue(OcrJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var existing = _jobs.FindIndex(j => string.Equals(j.ZoneId, job.ZoneId, StringComparison.Ordinal));
        if (existing >= 0)
        {
            _jobs[existing] = job;
            return true;
        }

        _jobs.Add(job);
        return false;
    }

    public OcrJob? Dequeue()
    {
        if (_jobs.Count == 0)
            return null;

        var bestIndex = 0;
        for (var i = 1; i < _jobs.Count; i++)
        {
            if (Compare(_jobs[i], _jobs[bestIndex]) < 0)
                bestIndex = i;
        }

        var best = _jobs[bestIndex];
        _jobs.RemoveAt(bestIndex);
        return best;
    }

    public OcrJob? Peek()
    {
        if (_jobs.Count == 0)
            return null;

        var best = _jobs[0];
        for (var i = 1; i < _jobs.Count; i++)
        {
            if (Compare(_jobs[i], best) < 0)
                best = _jobs[i];
        }

        return best;
    }

    public bool Remove(string zoneId) =>
        _jobs.RemoveAll(j => string.Equals(j.ZoneId, zoneId, StringComparison.Ordinal)) > 0;

    public void Clear() => _jobs.Clear();

    private static int Compare(OcrJob a, OcrJob b)
    {
        var byPriority = a.Priority.CompareTo(b.Priority);
        return byPriority != 0 ? byPriority : a.EnqueuedAtMs.CompareTo(b.EnqueuedAtMs);
    }
}
