using System.Text.Json;
using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

/// <summary>
/// The C# half of the cross-language pin. <c>tools/tests/test_normalize.py</c> asserts the exact
/// same file, so a change on either side that the other does not follow turns red here or there.
/// <para>
/// The failure this guards against is silent: with mismatched normalization the importer still
/// writes rows and the app still runs, the strings are simply unreachable from the exact stage
/// and coverage sags with no error anywhere.
/// </para>
/// </summary>
public class NormalizationVectorTests
{
    private sealed record Vector(string Input, string Norm, long NormHash, string NormUnfolded);

    private static readonly Vector[] Vectors = Load();

    private static Vector[] Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "normalization_vectors.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        return document.RootElement
            .GetProperty("vectors")
            .EnumerateArray()
            .Select(element => new Vector(
                element.GetProperty("input").GetString()!,
                element.GetProperty("norm").GetString()!,
                element.GetProperty("normHash").GetInt64(),
                element.GetProperty("normUnfolded").GetString()!))
            .ToArray();
    }

    public static TheoryData<int> VectorIndices()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Vectors.Length; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(VectorIndices))]
    public void Matches_the_shared_fixture(int index)
    {
        var vector = Vectors[index];

        Assert.Equal(vector.Norm, TextNormalizer.Normalize(vector.Input));
        Assert.Equal(vector.NormHash, NormHash.ComputeSigned(TextNormalizer.Normalize(vector.Input)));
        Assert.Equal(
            vector.NormUnfolded,
            TextNormalizer.Normalize(vector.Input, new NormalizeOptions { FoldConfusableGlyphs = false }));
    }

    [Fact]
    public void The_fixture_actually_covers_the_interesting_cases()
    {
        // A fixture that quietly shrank to three trivial strings would still pass every
        // assertion above while guarding nothing.
        Assert.True(Vectors.Length >= 15);
        Assert.Contains(Vectors, v => v.Input.Contains("<color", StringComparison.Ordinal));
        Assert.Contains(Vectors, v => v.Input.Contains("{0}", StringComparison.Ordinal));
        Assert.Contains(Vectors, v => v.Input.Contains('%'));
        Assert.Contains(Vectors, v => v.Input.Contains('\'') || v.Input.Contains('’'));
        Assert.Contains(Vectors, v => v.Norm != v.NormUnfolded);
    }
}
