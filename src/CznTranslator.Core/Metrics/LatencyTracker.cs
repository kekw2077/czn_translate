namespace CznTranslator.Core.Metrics;

public enum PipelineStage
{
    Capture,
    Ocr,
    Lookup,
    Draw,

    /// <summary>End to end: zone hash changed → translation drawn. This is the §10 acceptance number.</summary>
    ChangeToDraw
}

/// <summary>
/// Fixed-size ring of recent samples with percentile readout. Bounded on purpose — a session
/// runs for hours at 60 fps and an unbounded list would only measure the beginning of it.
/// </summary>
public sealed class LatencyTracker(int capacity = 2048)
{
    private readonly double[] _samples = new double[capacity];
    private readonly object _gate = new();
    private int _count;
    private int _next;

    public void Record(double milliseconds)
    {
        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }
    }

    public int Count
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Nearest-rank percentile; <paramref name="percentile"/> is 0..1. NaN when empty.</summary>
    public double Percentile(double percentile)
    {
        if (percentile is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Percentile must be within 0..1.");

        double[] copy;
        lock (_gate)
        {
            if (_count == 0)
                return double.NaN;
            copy = new double[_count];
            Array.Copy(_samples, copy, _count);
        }

        Array.Sort(copy);
        var rank = (int)Math.Ceiling(percentile * copy.Length) - 1;
        return copy[Math.Clamp(rank, 0, copy.Length - 1)];
    }

    public double Mean()
    {
        lock (_gate)
        {
            if (_count == 0)
                return double.NaN;

            var sum = 0.0;
            for (var i = 0; i < _count; i++)
                sum += _samples[i];
            return sum / _count;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _count = 0;
            _next = 0;
        }
    }
}
