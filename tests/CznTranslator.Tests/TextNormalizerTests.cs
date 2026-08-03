using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Strips_unity_markup_before_touching_punctuation()
    {
        // The tag must disappear whole. If punctuation were removed first, "<color=#ff0000>"
        // would survive as the words "color ff0000".
        var normalized = TextNormalizer.Normalize("<color=#ff0000>Blood Pact</color>");

        Assert.DoesNotContain("color", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("ff0000", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Strips_sprite_tags()
    {
        var normalized = TextNormalizer.Normalize("Costs <sprite=12> 3 mana");
        Assert.DoesNotContain("sprite", normalized, StringComparison.Ordinal);
        Assert.Contains("mana", TextNormalizer.Normalize("mana"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Hello\\nWorld")]
    [InlineData("Hello\nWorld")]
    [InlineData("Hello   World")]
    public void Collapses_line_breaks_and_runs_of_space(string input)
    {
        Assert.Equal(TextNormalizer.Normalize("Hello World"), TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Folds_confusable_glyphs_symmetrically()
    {
        // OCR reading "Bloodletting" as "8I00dIetting" must land on the same key.
        Assert.Equal(
            TextNormalizer.Normalize("Bloodletting"),
            TextNormalizer.Normalize("8I00dIetting"));
    }

    [Fact]
    public void Folding_can_be_disabled()
    {
        var folded = TextNormalizer.Normalize("Boss");
        var unfolded = TextNormalizer.Normalize("Boss", new NormalizeOptions { FoldConfusableGlyphs = false });

        Assert.Equal("8055", folded);
        Assert.Equal("boss", unfolded);
    }

    [Fact]
    public void Keeps_placeholders_verbatim_and_unfolded()
    {
        var normalized = TextNormalizer.Normalize("Deal {0} damage to {target}");

        Assert.Contains("{0}", normalized, StringComparison.Ordinal);
        // Without the placeholder guard the folding would rewrite this as {targ3t}-ish garbage
        // and the importer and the OCR path would disagree on anything parameterised.
        Assert.Contains("{target}", normalized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%s")]
    [InlineData("%d")]
    [InlineData("%1$s")]
    public void Keeps_printf_placeholders(string placeholder)
    {
        Assert.Contains(placeholder, TextNormalizer.Normalize($"Restores {placeholder} health"), StringComparison.Ordinal);
    }

    [Fact]
    public void Apostrophes_do_not_split_words()
    {
        Assert.Equal(TextNormalizer.Normalize("dont"), TextNormalizer.Normalize("don't"));
        Assert.Equal(TextNormalizer.Normalize("dont"), TextNormalizer.Normalize("don’t"));
    }

    [Fact]
    public void Other_punctuation_becomes_a_word_boundary()
    {
        // "hp/mp" must stay two tokens: gluing them together would poison the trigram index.
        Assert.Equal("hp mp", TextNormalizer.Normalize("HP/MP", new NormalizeOptions { FoldConfusableGlyphs = false }));
    }

    [Fact]
    public void Is_idempotent()
    {
        const string raw = "  <b>Deal {0} damage</b>, then draw 2 cards.\\n";
        var once = TextNormalizer.Normalize(raw);
        Assert.Equal(once, TextNormalizer.Normalize(once));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<color=#fff></color>")]
    public void Empty_input_normalizes_to_empty(string? input)
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(input));
    }

    [Fact]
    public void Extracts_placeholders_in_order()
    {
        var placeholders = TextNormalizer.ExtractPlaceholders("Deal {0} damage over {1} turns to {0}");
        Assert.Equal(["{0}", "{1}", "{0}"], placeholders);
    }

    [Fact]
    public void Extracts_tags()
    {
        var tags = TextNormalizer.ExtractTags("<color=#ff0000>Burn</color> <sprite=4>");
        Assert.Equal(["<color=#ff0000>", "</color>", "<sprite=4>"], tags);
    }

    [Fact]
    public void Detects_cyrillic_and_latin()
    {
        Assert.True(TextNormalizer.HasCyrillic("Кровавый пакт"));
        Assert.False(TextNormalizer.HasCyrillic("Blood Pact"));
        Assert.True(TextNormalizer.HasLatinLetters("Blood Pact"));
        Assert.False(TextNormalizer.HasLatinLetters("Кровавый"));
        Assert.False(TextNormalizer.HasLatinLetters("{0} 12 %"));
    }
}
