using System.Text;

namespace CznTranslator.Lookup;

/// <summary>
/// Builds FTS5 queries against the trigram index.
/// <para>
/// A phrase query would only find exact substrings, which is useless when the whole point is
/// that OCR got a character wrong. Instead the normalized text is split into its trigrams and
/// they are OR'd together, so candidates rank by how many trigrams they share — the standard
/// way to get approximate matches out of a trigram index. The C# Levenshtein pass then decides.
/// </para>
/// </summary>
public static class FtsQuery
{
    public const int TrigramLength = 3;

    /// <summary>
    /// Trigram OR-query, or null when the text is too short to be indexed at all.
    /// <paramref name="maxTerms"/> caps the query size — a long tooltip can produce a hundred
    /// trigrams and the tail of them adds latency without changing the ranking.
    /// </summary>
    public static string? BuildTrigramQuery(string normalized, int maxTerms = 24)
    {
        ArgumentNullException.ThrowIfNull(normalized);

        if (maxTerms < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTerms), "At least one term is required.");

        if (normalized.Length < TrigramLength)
            return null;

        var trigrams = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i + TrigramLength <= normalized.Length; i++)
        {
            var trigram = normalized.Substring(i, TrigramLength);
            if (seen.Add(trigram))
                trigrams.Add(trigram);
        }

        if (trigrams.Count == 0)
            return null;

        var selected = Sample(trigrams, maxTerms);

        var builder = new StringBuilder();
        foreach (var trigram in selected)
        {
            if (builder.Length > 0)
                builder.Append(" OR ");
            builder.Append('"').Append(trigram.Replace("\"", "\"\"")).Append('"');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Evenly spaced subset, keeping the first and last trigram. Taking the first N instead would
    /// bias every long string towards its opening words.
    /// </summary>
    private static List<string> Sample(List<string> items, int max)
    {
        if (items.Count <= max)
            return items;

        var result = new List<string>(max);
        for (var i = 0; i < max; i++)
        {
            var index = (int)Math.Round((double)i * (items.Count - 1) / (max - 1));
            result.Add(items[index]);
        }

        return result;
    }
}
