using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

public class NormHashTests
{
    /// <summary>
    /// Pinned xxHash64 vectors (seed 0, UTF-8). The Python conveyor asserts the same numbers in
    /// <c>tools/tests/test_normalize.py</c>: if the two implementations ever diverge, every row
    /// the importer writes becomes unreachable from the runtime exact stage, and nothing else
    /// in the system would report that.
    /// </summary>
    [Theory]
    [InlineData("", 17241709254077376921UL)]
    [InlineData("deal 1 damag3", 13314471865346301693UL)]
    [InlineData("the quick brown fox", 1513236774081638803UL)]
    public void Matches_the_pinned_cross_language_vectors(string input, ulong expected)
    {
        Assert.Equal(expected, NormHash.Compute(input));
    }

    [Fact]
    public void Signed_round_trip_survives_sqlite_storage()
    {
        // SQLite has no unsigned 64-bit integer, so the value is reinterpreted, not clamped.
        const ulong big = 17241709254077376921UL;

        var signed = NormHash.ToSigned(big);
        Assert.True(signed < 0);
        Assert.Equal(big, NormHash.ToUnsigned(signed));
    }

    [Fact]
    public void Signed_helper_agrees_with_the_unsigned_one()
    {
        const string text = "blood pact";
        Assert.Equal(NormHash.ToSigned(NormHash.Compute(text)), NormHash.ComputeSigned(text));
    }
}
