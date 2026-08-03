using System.Text;
using System.Text.RegularExpressions;

namespace CznTranslator.Lookup;

public sealed record NormalizeOptions
{
    /// <summary>
    /// Collapse OCR-confusable glyphs (l/I/1/¦ → 1, O/0 → 0, S/5 → 5, B/8 → 8), TZ §5.
    /// <para>
    /// Turning this off is only meaningful together with a full re-import: <c>norm</c> and
    /// <c>norm_hash</c> are stored, so the flag has to match between import and runtime or
    /// nothing will ever hit the exact stage. It exists so the regression set from §12 can
    /// measure what the folding actually buys.
    /// </para>
    /// </summary>
    public bool FoldConfusableGlyphs { get; init; } = true;

    public static readonly NormalizeOptions Default = new();
}

/// <summary>
/// The single normalization used by both the dump importer and the OCR path (TZ §5).
/// Any divergence between the two sides makes the exact stage silently useless, which is why
/// this lives in one place and is mirrored 1:1 by <c>tools/czn/normalize.py</c>.
/// </summary>
public static partial class TextNormalizer
{
    /// <summary>Unity rich text and sprite tags: &lt;color=#fff&gt;, &lt;/color&gt;, &lt;sprite=3&gt;, &lt;b&gt;.</summary>
    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z][^<>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex MarkupTagRegex();

    /// <summary>Escaped line breaks that survive a JSON dump as literal backslash-n.</summary>
    [GeneratedRegex(@"\\[rnt]", RegexOptions.CultureInvariant)]
    private static partial Regex EscapedBreakRegex();

    /// <summary>{0}, {value}, {0:N1}, %s, %d, %1$s — kept verbatim so they survive step 3.</summary>
    [GeneratedRegex(@"\{[^{}]*\}|%\d+\$[sdifux]|%[sdifux]", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    /// <summary>Apostrophes vanish rather than splitting a word: "don't" → "dont".</summary>
    private const string ApostropheChars = "'’ʼ`´";

    public static string Normalize(string? raw) => Normalize(raw, NormalizeOptions.Default);

    public static string Normalize(string? raw, NormalizeOptions options)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // Markup goes first. Stripping punctuation before the tags would turn <color=#ff0000>
        // into the word "color ff0000" and pollute every string that carries formatting.
        var text = EscapedBreakRegex().Replace(raw, " ");
        text = MarkupTagRegex().Replace(text, " ");
        text = text.ToLowerInvariant();

        var builder = new StringBuilder(text.Length);
        var cursor = 0;

        foreach (Match placeholder in PlaceholderRegex().Matches(text))
        {
            AppendFolded(builder, text.AsSpan(cursor, placeholder.Index - cursor), options);
            builder.Append(placeholder.Value);
            cursor = placeholder.Index + placeholder.Length;
        }

        AppendFolded(builder, text.AsSpan(cursor), options);

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    private static void AppendFolded(StringBuilder builder, ReadOnlySpan<char> span, NormalizeOptions options)
    {
        foreach (var ch in span)
        {
            if (ApostropheChars.Contains(ch))
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(options.FoldConfusableGlyphs ? FoldGlyph(ch) : ch);
                continue;
            }

            // Every other non-letter becomes a separator instead of disappearing, so that
            // "hp/mp" stays two tokens and the trigram index keeps a usable word boundary.
            builder.Append(' ');
        }
    }

    /// <summary>
    /// Runs on already-lowercased input, so the uppercase forms from the TZ table arrive here as
    /// their lowercase counterparts: I → i, O → o, S → s, B → b.
    /// </summary>
    private static char FoldGlyph(char ch) => ch switch
    {
        'l' or 'i' or '1' or '¦' or '|' or 'ı' => '1',
        'o' or '0' => '0',
        's' or '5' => '5',
        'b' or '8' => '8',
        _ => ch
    };

    /// <summary>
    /// Placeholders as they appear in a string, in order. The validator compares these sets
    /// between original and translation (TZ §8).
    /// </summary>
    public static IReadOnlyList<string> ExtractPlaceholders(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var result = new List<string>();
        foreach (Match match in PlaceholderRegex().Matches(raw))
            result.Add(match.Value);
        return result;
    }

    public static IReadOnlyList<string> ExtractTags(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var result = new List<string>();
        foreach (Match match in MarkupTagRegex().Matches(raw))
            result.Add(match.Value);
        return result;
    }

    /// <summary>True when the text contains at least one Cyrillic letter (validator check, §8).</summary>
    public static bool HasCyrillic(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var ch in text)
        {
            if (ch is >= 'Ѐ' and <= 'ӿ')
                return true;
        }

        return false;
    }

    public static bool HasLatinLetters(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var ch in text)
        {
            if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                return true;
        }

        return false;
    }
}
