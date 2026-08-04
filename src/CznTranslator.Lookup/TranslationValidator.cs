namespace CznTranslator.Lookup;

/// <summary>
/// Mirrors <c>tools/czn/validate.py</c>: catches the failures that make a translation worse than
/// the English it replaces, rather than judging wording. Used by the review queue and the C#
/// translation write-back so the desktop side and the Python conveyor flag the same problems.
/// </summary>
public static class TranslationValidator
{
    // Above this the Russian will not fit the widget the English was laid out for.
    private const double LengthRatioLimit = 1.6;

    /// <summary>False for codes, numbers and empty strings — those come back unchanged (§8).</summary>
    public static bool IsTranslatable(string? english) =>
        !string.IsNullOrWhiteSpace(english) && TextNormalizer.HasLatinLetters(english);

    /// <summary>Human-readable warnings (empty when clean). Order-insensitive on placeholders/tags.</summary>
    public static IReadOnlyList<string> Validate(string english, string? russian)
    {
        if (string.IsNullOrWhiteSpace(russian))
            return ["перевод пуст"];

        var findings = new List<string>();

        if (!SameMultiset(TextNormalizer.ExtractPlaceholders(english), TextNormalizer.ExtractPlaceholders(russian)))
            findings.Add("не совпадают плейсхолдеры ({0}, %s …)");

        if (!SameMultiset(TextNormalizer.ExtractTags(english), TextNormalizer.ExtractTags(russian)))
            findings.Add("не совпадают теги (<color>, <sprite> …)");

        if (english.Length > 0 && russian.Length > LengthRatioLimit * english.Length)
            findings.Add($"слишком длинно ({russian.Length} против {english.Length} символов)");

        // A model that echoed the English back is the commonest silent failure; punctuation-only
        // strings have nothing to translate and are fine as-is.
        if (TextNormalizer.HasLatinLetters(english) && !TextNormalizer.HasCyrillic(russian))
            findings.Add("нет кириллицы — вероятно, не переведено");

        return findings;
    }

    private static bool SameMultiset(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in a)
            counts[item] = counts.GetValueOrDefault(item) + 1;
        foreach (var item in b)
        {
            if (!counts.TryGetValue(item, out var n))
                return false;
            if (n == 1)
                counts.Remove(item);
            else
                counts[item] = n - 1;
        }
        return counts.Count == 0;
    }
}
