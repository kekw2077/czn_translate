using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

public class FtsQueryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    public void Text_shorter_than_a_trigram_produces_no_query(string input)
    {
        Assert.Null(FtsQuery.BuildTrigramQuery(input));
    }

    [Fact]
    public void Builds_an_or_of_quoted_trigrams()
    {
        var query = FtsQuery.BuildTrigramQuery("abcd");
        Assert.Equal("\"abc\" OR \"bcd\"", query);
    }

    [Fact]
    public void Repeated_trigrams_appear_once()
    {
        var query = FtsQuery.BuildTrigramQuery("ababab");
        Assert.Equal("\"aba\" OR \"bab\"", query);
    }

    [Fact]
    public void Long_input_is_capped_but_keeps_both_ends()
    {
        var text = new string('x', 20) + "needle" + new string('y', 20);
        var query = FtsQuery.BuildTrigramQuery(text, maxTerms: 5)!;

        Assert.Equal(5, query.Split(" OR ").Length);
        Assert.StartsWith("\"xxx\"", query, StringComparison.Ordinal);
        Assert.EndsWith("\"yyy\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_inside_the_text_are_escaped()
    {
        // Normalization strips quotes, but the builder must not be the thing that breaks if one
        // ever reaches it — an unescaped quote is a syntax error inside an FTS5 MATCH expression.
        var query = FtsQuery.BuildTrigramQuery("a\"bc")!;
        Assert.Contains("\"a\"\"b\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_zero_term_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FtsQuery.BuildTrigramQuery("abcdef", maxTerms: 0));
    }
}

public class TranslationDatabaseTests
{
    [Theory]
    [InlineData("3.45.0", true)]
    [InlineData("3.34.0", true)]
    [InlineData("3.33.9", false)]
    [InlineData("2.99.0", false)]
    [InlineData("garbage", false)]
    public void Version_gate_matches_the_fts5_trigram_requirement(string version, bool expected)
    {
        Assert.Equal(expected, TranslationDatabase.IsAtLeast(version, 3, 34));
    }

    [Fact]
    public void The_embedded_schema_is_present_and_carries_the_fts_triggers()
    {
        var sql = TranslationDatabase.SchemaSql;

        Assert.Contains("CREATE TABLE IF NOT EXISTS strings", sql, StringComparison.Ordinal);
        Assert.Contains("tokenize='trigram'", sql, StringComparison.Ordinal);
        Assert.Contains("strings_fts_ai", sql, StringComparison.Ordinal);
        Assert.Contains("strings_fts_au", sql, StringComparison.Ordinal);
        Assert.Contains("strings_fts_ad", sql, StringComparison.Ordinal);
    }
}
