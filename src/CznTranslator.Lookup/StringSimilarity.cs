namespace CznTranslator.Lookup;

/// <summary>
/// Normalized Levenshtein distance used to rescore FTS5 candidates (TZ §5, шаг 3).
/// </summary>
public static class StringSimilarity
{
    /// <summary>
    /// 1.0 for identical strings, 0.0 for nothing in common.
    /// </summary>
    public static double Score(string a, string b)
    {
        if (ReferenceEquals(a, b))
            return 1.0;
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
            return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return 0.0;

        var longest = Math.Max(a.Length, b.Length);
        var distance = Distance(a, b);
        return 1.0 - (double)distance / longest;
    }

    /// <summary>
    /// Score with an early exit. Candidates that cannot reach <paramref name="minScore"/> stop
    /// early and return 0 — with 50 candidates per lookup this is the difference between a
    /// lookup that fits in the latency budget and one that does not.
    /// </summary>
    public static double ScoreAtLeast(string a, string b, double minScore)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b) ? 1.0 : 0.0;

        var longest = Math.Max(a.Length, b.Length);

        // A length gap alone already puts the score out of reach.
        var lengthGap = Math.Abs(a.Length - b.Length);
        var maxDistance = (int)Math.Floor((1.0 - minScore) * longest);
        if (lengthGap > maxDistance)
            return 0.0;

        var distance = Distance(a, b, maxDistance);
        if (distance < 0)
            return 0.0;

        return 1.0 - (double)distance / longest;
    }

    /// <summary>
    /// Two-row Levenshtein. When <paramref name="maxDistance"/> is non-negative and every cell in
    /// a row exceeds it, the computation stops and -1 is returned.
    /// </summary>
    public static int Distance(string a, string b, int maxDistance = -1)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Keep the shorter string on the inner axis so the rows stay small.
        if (a.Length > b.Length)
            (a, b) = (b, a);

        var previous = new int[a.Length + 1];
        var current = new int[a.Length + 1];

        for (var i = 0; i <= a.Length; i++)
            previous[i] = i;

        for (var j = 1; j <= b.Length; j++)
        {
            current[0] = j;
            var rowMin = current[0];
            var bChar = b[j - 1];

            for (var i = 1; i <= a.Length; i++)
            {
                var cost = a[i - 1] == bChar ? 0 : 1;
                var value = Math.Min(
                    Math.Min(current[i - 1] + 1, previous[i] + 1),
                    previous[i - 1] + cost);
                current[i] = value;
                if (value < rowMin)
                    rowMin = value;
            }

            if (maxDistance >= 0 && rowMin > maxDistance)
                return -1;

            (previous, current) = (current, previous);
        }

        return previous[a.Length];
    }
}
