using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

public class StringSimilarityTests
{
    [Fact]
    public void Identical_strings_score_one()
    {
        Assert.Equal(1.0, StringSimilarity.Score("blood pact", "blood pact"));
    }

    [Fact]
    public void Disjoint_strings_score_zero()
    {
        Assert.Equal(0.0, StringSimilarity.Score("abc", ""));
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("flaw", "lawn", 2)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "abc", 0)]
    public void Distance_matches_known_values(string a, string b, int expected)
    {
        Assert.Equal(expected, StringSimilarity.Distance(a, b));
    }

    [Fact]
    public void Distance_is_symmetric()
    {
        Assert.Equal(
            StringSimilarity.Distance("deal 5 damage", "dea1 5 damaqe"),
            StringSimilarity.Distance("dea1 5 damaqe", "deal 5 damage"));
    }

    [Fact]
    public void One_wrong_character_in_a_long_string_clears_the_default_threshold()
    {
        // This is the case the fuzzy stage exists for: OCR misread a single glyph.
        var score = StringSimilarity.Score("restore 10 health to all allies", "restore 10 hea1th to all allies");
        Assert.True(score >= 0.85, $"Expected >= 0.85 but got {score:F3}.");
    }

    [Fact]
    public void Early_exit_returns_the_same_score_as_the_full_computation()
    {
        const string a = "deal 5 damage to a random enemy";
        const string b = "dea1 5 damage to a random enemv";

        var full = StringSimilarity.Score(a, b);
        Assert.Equal(full, StringSimilarity.ScoreAtLeast(a, b, 0.5), precision: 10);
    }

    [Fact]
    public void Early_exit_returns_zero_when_the_threshold_is_out_of_reach()
    {
        Assert.Equal(0.0, StringSimilarity.ScoreAtLeast("deal 5 damage", "completely unrelated string", 0.85));
    }

    [Fact]
    public void Length_gap_alone_short_circuits()
    {
        // "a" vs a 40-char string cannot reach 0.85 no matter what the characters are.
        Assert.Equal(0.0, StringSimilarity.ScoreAtLeast("a", new string('a', 40), 0.85));
    }
}
