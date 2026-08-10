using System;
using System.Collections.Generic;
using CznTranslator.Core.Models;
using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

/// <summary>The desktop write paths added for the native settings app.</summary>
public class StringRepositoryTests : IDisposable
{
    private readonly LookupFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ApplyTranslationsByEnglish_fans_out_to_every_row_with_that_english()
    {
        var repo = _fixture.Repository;
        var a1 = repo.Upsert("Attack", null, key: "a1");
        var a2 = repo.Upsert("Attack", null, key: "a2");
        var s1 = repo.Upsert("Select", null, key: "s1");
        var u1 = repo.Upsert("Untouched", null, key: "u1");

        var applied = repo.ApplyTranslationsByEnglish(
            new Dictionary<string, string> { ["Attack"] = "Атака", ["Select"] = "Выбрать", ["Missing"] = "нет" },
            StringStatus.MachineTranslated);

        Assert.Equal(3, applied); // both Attack rows + the one Select; Missing matches nothing
        Assert.Equal("Атака", repo.GetById(a1)!.Russian);
        Assert.Equal("Атака", repo.GetById(a2)!.Russian);
        Assert.Equal(StringStatus.MachineTranslated, repo.GetById(s1)!.Status);
        Assert.Null(repo.GetById(u1)!.Russian);
    }

    [Fact]
    public void ApplyTranslationsByEnglish_skips_blank_translations()
    {
        var repo = _fixture.Repository;
        var id = repo.Upsert("Attack", null, key: "a1");

        var applied = repo.ApplyTranslationsByEnglish(
            new Dictionary<string, string> { ["Attack"] = "   " }, StringStatus.MachineTranslated);

        Assert.Equal(0, applied);
        Assert.Null(repo.GetById(id)!.Russian);
    }

    [Fact]
    public void Pending_returns_new_and_stale_only()
    {
        var repo = _fixture.Repository;
        repo.Upsert("New one", null, key: "n", status: StringStatus.New);
        repo.Upsert("Stale one", "старое", key: "s", status: StringStatus.Stale);
        repo.Upsert("Done", "готово", key: "d", status: StringStatus.Reviewed);

        var pending = repo.Pending();

        Assert.Equal(2, pending.Count);
        Assert.DoesNotContain(pending, r => r.Key == "d");
    }

    [Fact]
    public void FindTranslationMemory_reuses_only_human_reviewed_text()
    {
        var repo = _fixture.Repository;
        repo.Upsert("Blood Pact", "Кровавый пакт", key: "reviewed", status: StringStatus.Reviewed);
        repo.Upsert("Blood Pact", "машинный", key: "mt", status: StringStatus.MachineTranslated);

        var norm = TextNormalizer.Normalize("Blood Pact");

        Assert.Equal("Кровавый пакт", repo.FindTranslationMemory(norm));
    }

    [Fact]
    public void ImportByNormalizedEnglish_matches_across_markup()
    {
        var repo = _fixture.Repository;
        // Stored English carries markup; the imported dictionary key is display text. They must still
        // meet, because both normalize to the same thing.
        var id = repo.Upsert("<b>Blood Pact</b>", null, key: "b", status: StringStatus.New);

        var result = repo.ImportByNormalizedEnglish(
            new Dictionary<string, string> { ["Blood Pact"] = "Кровавый пакт" }, StringStatus.MachineTranslated);

        Assert.Equal(1, result.RowsUpdated);
        Assert.Equal(0, result.Inserted);
        Assert.Equal("Кровавый пакт", repo.GetById(id)!.Russian);
    }

    [Fact]
    public void ImportByNormalizedEnglish_inserts_an_entry_that_matches_no_row()
    {
        var repo = _fixture.Repository;

        var result = repo.ImportByNormalizedEnglish(
            new Dictionary<string, string> { ["Brand New Line"] = "Новая строка" }, StringStatus.MachineTranslated);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.RowsUpdated);
        Assert.Equal(1, repo.CountByStatus(StringStatus.MachineTranslated));  // the inserted row
    }

    [Fact]
    public void ImportByNormalizedEnglish_never_overwrites_reviewed_text()
    {
        var repo = _fixture.Repository;
        var id = repo.Upsert("Attack", "Атака", key: "a", status: StringStatus.Reviewed);

        var result = repo.ImportByNormalizedEnglish(
            new Dictionary<string, string> { ["Attack"] = "ПЕРЕЗАПИСЬ" }, StringStatus.MachineTranslated);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.RowsUpdated);
        Assert.Equal("Атака", repo.GetById(id)!.Russian);  // human text kept
    }

    [Fact]
    public void AcceptAllMachineTranslated_promotes_mt_and_leaves_the_rest()
    {
        var repo = _fixture.Repository;
        repo.Upsert("A", "а", key: "a", status: StringStatus.MachineTranslated);
        repo.Upsert("B", "б", key: "b", status: StringStatus.MachineTranslated);
        repo.Upsert("C", null, key: "c", status: StringStatus.New);
        repo.Upsert("D", "д", key: "d", status: StringStatus.Reviewed);

        var accepted = repo.AcceptAllMachineTranslated();

        Assert.Equal(2, accepted);
        Assert.Equal(0, repo.CountByStatus(StringStatus.MachineTranslated));
        Assert.Equal(3, repo.CountByStatus(StringStatus.Reviewed)); // a, b, and the pre-existing d
        Assert.Equal(1, repo.CountByStatus(StringStatus.New));
    }
}
