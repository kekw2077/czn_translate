using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Metrics;
using CznTranslator.Core.Models;
using CznTranslator.Lookup;
using Xunit;

namespace CznTranslator.Tests;

/// <summary>Temp database with the real schema — these tests exercise SQLite, FTS5 and the triggers.</summary>
public sealed class LookupFixture : IDisposable
{
    public LookupFixture()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"czn-test-{Guid.NewGuid():N}.db");
        Database = new TranslationDatabase(Path);
        Database.EnsureCreated();
        Repository = new StringRepository(Database);
    }

    public string Path { get; }
    public TranslationDatabase Database { get; }
    public StringRepository Repository { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = Path + suffix;
            if (File.Exists(file))
            {
                try { File.Delete(file); }
                catch (IOException) { /* the temp file will be reaped anyway */ }
            }
        }
    }
}

public class LookupCascadeTests : IDisposable
{
    private readonly LookupFixture _fixture = new();
    private readonly LookupSection _settings = new();
    private readonly MetricsCollector _metrics = new();

    public void Dispose() => _fixture.Dispose();

    private SqliteLookupService CreateService(ITranslationFallback? fallback = null) =>
        new(_fixture.Database, _settings, fallback, _metrics);

    [Fact]
    public void Schema_creation_is_idempotent()
    {
        var exception = Record.Exception(() => _fixture.Database.EnsureCreated());
        Assert.Null(exception);
    }

    [Fact]
    public async Task Exact_stage_resolves_a_clean_read()
    {
        _fixture.Repository.Upsert("Blood Pact", "Кровавый пакт", key: "card.blood_pact");

        var hit = await CreateService().ResolveAsync("Blood Pact", confidence: 0.99);

        Assert.Equal(LookupSource.Exact, hit.Source);
        Assert.Equal("Кровавый пакт", hit.Russian);
        Assert.True(hit.IsTranslated);
    }

    [Fact]
    public async Task Exact_stage_survives_markup_and_case_differences()
    {
        _fixture.Repository.Upsert("<color=#ff0000>Blood Pact</color>", "Кровавый пакт", key: "card.blood_pact");

        var hit = await CreateService().ResolveAsync("BLOOD PACT", confidence: 0.99);

        Assert.Equal(LookupSource.Exact, hit.Source);
    }

    [Fact]
    public async Task Exact_stage_prefers_a_reviewed_row_over_a_machine_translated_one()
    {
        _fixture.Repository.Upsert("Blood Pact", "Пакт крови", key: "a", status: StringStatus.MachineTranslated);
        _fixture.Repository.Upsert("Blood Pact", "Кровавый пакт", key: "b", status: StringStatus.Reviewed);

        var hit = await CreateService().ResolveAsync("Blood Pact", confidence: 0.99);

        Assert.Equal("Кровавый пакт", hit.Russian);
    }

    [Fact]
    public async Task Exact_stage_prefers_a_translated_row_over_an_untranslated_one()
    {
        _fixture.Repository.Upsert("Blood Pact", null, key: "a", status: StringStatus.Locked);
        _fixture.Repository.Upsert("Blood Pact", "Кровавый пакт", key: "b", status: StringStatus.New);

        var hit = await CreateService().ResolveAsync("Blood Pact", confidence: 0.99);

        Assert.Equal("Кровавый пакт", hit.Russian);
    }

    [Fact]
    public async Task Fuzzy_stage_recovers_a_single_misread_character()
    {
        _fixture.Repository.Upsert(
            "Restore 10 damage to all enemies",
            "Наносит 10 урона всем врагам",
            key: "skill.damage_all");

        // 'q' instead of 'g' — the kind of miss a 48px rec model makes on a stylised font.
        // Note it has to be a confusion the normalizer does *not* already fold away: "allles"
        // for "allies" would normalize to the same key and come back as an exact hit instead.
        var hit = await CreateService().ResolveAsync("Restore 10 damaqe to all enemies", confidence: 0.9);

        Assert.Equal(LookupSource.Fuzzy, hit.Source);
        Assert.Equal("Наносит 10 урона всем врагам", hit.Russian);
        Assert.True(hit.Score >= _settings.FuzzyThreshold);
    }

    [Fact]
    public async Task Fuzzy_stage_writes_a_correction_so_the_next_hit_short_circuits()
    {
        _fixture.Repository.Upsert("Restore 10 damage to all enemies", "Наносит 10 урона всем врагам", key: "s1");

        var service = CreateService();
        var first = await service.ResolveAsync("Restore 10 damaqe to all enemies", confidence: 0.9);
        var second = await service.ResolveAsync("Restore 10 damaqe to all enemies", confidence: 0.9);

        Assert.Equal(LookupSource.Fuzzy, first.Source);
        Assert.Equal(LookupSource.Correction, second.Source);
        Assert.Equal(first.Russian, second.Russian);
    }

    [Fact]
    public async Task Fuzzy_stage_refuses_a_candidate_below_the_threshold()
    {
        _fixture.Repository.Upsert("Restore 10 health to all allies", "Восстанавливает 10 здоровья", key: "s1");

        var hit = await CreateService().ResolveAsync("Summon a skeletal warrior", confidence: 0.95);

        Assert.Equal(LookupSource.Miss, hit.Source);
    }

    [Fact]
    public async Task Low_confidence_relaxes_the_threshold()
    {
        _fixture.Repository.Upsert("Deal 12 damage to a random enemy", "Наносит 12 урона случайному врагу", key: "s1");

        // Four misreads across 33 normalized characters — distance 5, score ≈ 0.848:
        // below the strict 0.85 gate, above the relaxed 0.80 one.
        const string noisy = "Ceal 12 damaqe to a randorn enemv";

        var strict = await CreateService().ResolveAsync(noisy, confidence: 0.95);
        var relaxed = await CreateService().ResolveAsync(noisy, confidence: 0.5);

        Assert.Equal(LookupSource.Miss, strict.Source);
        Assert.Equal(LookupSource.Fuzzy, relaxed.Source);
    }

    [Fact]
    public async Task A_miss_shows_the_original_text()
    {
        var hit = await CreateService().ResolveAsync("Completely unknown string", confidence: 0.99);

        Assert.Equal(LookupSource.Miss, hit.Source);
        Assert.False(hit.IsTranslated);
        Assert.Equal("Completely unknown string", hit.Display);
    }

    [Fact]
    public async Task Empty_recognition_is_a_miss_without_touching_the_database()
    {
        var hit = await CreateService().ResolveAsync("   ", confidence: 0.99);
        Assert.Equal(LookupSource.Miss, hit.Source);
    }

    [Fact]
    public async Task Short_strings_skip_the_fuzzy_stage_instead_of_crashing()
    {
        // The trigram index cannot represent anything shorter than three characters.
        _fixture.Repository.Upsert("OK", "ОК", key: "ui.ok");

        var hit = await CreateService().ResolveAsync("QK", confidence: 0.9);
        Assert.Equal(LookupSource.Miss, hit.Source);
    }

    [Fact]
    public async Task Llm_fallback_is_written_back_as_mt_and_becomes_an_exact_hit()
    {
        var fallback = new StubFallback("Наносит 5 урона");
        var service = CreateService(fallback);

        var first = await service.ResolveAsync("Deal 5 damage", confidence: 0.99);
        var second = await service.ResolveAsync("Deal 5 damage", confidence: 0.99);

        Assert.Equal(LookupSource.Llm, first.Source);
        Assert.Equal(LookupSource.Exact, second.Source);
        Assert.Equal(1, fallback.Calls);

        var row = _fixture.Repository.GetById(first.StringId!.Value)!;
        Assert.Equal(StringStatus.MachineTranslated, row.Status);
        Assert.Equal(StringSource.Ocr, row.Source);
        Assert.Null(row.Key);
    }

    [Fact]
    public async Task An_unavailable_fallback_degrades_to_a_miss()
    {
        var hit = await CreateService(new StubFallback(null)).ResolveAsync("Deal 5 damage", confidence: 0.99);

        Assert.Equal(LookupSource.Miss, hit.Source);
        Assert.Equal("Deal 5 damage", hit.Display);
    }

    [Fact]
    public async Task A_disabled_fallback_is_never_called()
    {
        var fallback = new StubFallback("не должно вызываться") { IsEnabled = false };

        var hit = await CreateService(fallback).ResolveAsync("Deal 5 damage", confidence: 0.99);

        Assert.Equal(LookupSource.Miss, hit.Source);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Corrections_take_priority_over_everything_else()
    {
        var id = _fixture.Repository.Upsert("Blood Pact", "Кровавый пакт", key: "card.blood_pact");
        var service = CreateService();

        await service.RecordCorrectionAsync(TextNormalizer.Normalize("8lood Pacf"), id);

        var hit = await service.ResolveAsync("8lood Pacf", confidence: 0.4);

        Assert.Equal(LookupSource.Correction, hit.Source);
        Assert.Equal("Кровавый пакт", hit.Russian);
    }

    [Fact]
    public async Task Zone_cache_round_trips_a_whole_screen()
    {
        var service = CreateService();
        var original = new ZoneResult(
            "dialogue",
            0xDEAD_BEEF_CAFE_1234UL,
            [
                new TranslatedLine(
                    new PixelRect(10, 20, 300, 40),
                    new LookupHit(LookupSource.Exact, "Blood Pact", "Кровавый пакт", 7, 1.0),
                    0.97)
            ],
            FromCache: false);

        await service.StoreZoneAsync(original);
        var restored = await service.TryGetCachedZoneAsync("dialogue", original.ZoneHash);

        Assert.NotNull(restored);
        Assert.True(restored!.FromCache);
        var line = Assert.Single(restored.Lines);
        Assert.Equal("Кровавый пакт", line.Hit.Russian);
        Assert.Equal(new PixelRect(10, 20, 300, 40), line.Box);
        Assert.Equal(0.97, line.Confidence, precision: 6);
    }

    [Fact]
    public async Task Zone_cache_survives_a_hash_with_the_high_bit_set()
    {
        // zone_hash is a ulong going into a signed SQLite column — half of all hashes are negative.
        var service = CreateService();
        var hash = 0xFFFF_FFFF_FFFF_FFFFUL;

        await service.StoreZoneAsync(new ZoneResult("topbar", hash, [], FromCache: false));

        Assert.NotNull(await service.TryGetCachedZoneAsync("topbar", hash));
    }

    [Fact]
    public async Task Clearing_the_zone_cache_removes_every_screen()
    {
        var service = CreateService();
        await service.StoreZoneAsync(new ZoneResult("dialogue", 1UL, [], FromCache: false));
        await service.StoreZoneAsync(new ZoneResult("topbar", 2UL, [], FromCache: false));

        await service.ClearZoneCacheAsync();

        Assert.Null(await service.TryGetCachedZoneAsync("dialogue", 1UL));
        Assert.Null(await service.TryGetCachedZoneAsync("topbar", 2UL));
    }

    [Fact]
    public async Task Zone_cache_is_trimmed_to_its_capacity()
    {
        _settings.ZoneCacheCapacity = 5;
        var service = CreateService();

        for (var i = 0; i < 20; i++)
            await service.StoreZoneAsync(new ZoneResult("z", (ulong)i, [], FromCache: false));

        await using var connection = _fixture.Database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM zone_cache;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.True(count <= 5, $"zone_cache grew to {count} rows despite a capacity of 5.");
    }

    [Fact]
    public async Task Metrics_record_each_stage()
    {
        _fixture.Repository.Upsert("Blood Pact", "Кровавый пакт", key: "a");
        var service = CreateService();

        await service.ResolveAsync("Blood Pact", 0.99);
        await service.ResolveAsync("Nothing like this exists", 0.99);
        await service.StoreZoneAsync(new ZoneResult("z", 42UL, [], FromCache: false));
        await service.TryGetCachedZoneAsync("z", 42UL);

        var snapshot = _metrics.Snapshot();

        Assert.Equal(1, snapshot.ExactHits);
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(1, snapshot.CacheHits);
    }

    [Fact]
    public void Fts_index_is_populated_by_the_triggers()
    {
        // Without the sync triggers an external-content FTS5 table stays empty and the fuzzy
        // stage silently returns nothing at all.
        _fixture.Repository.Upsert("Summon a skeletal warrior", "Призывает скелета-воина", key: "s1");

        using var connection = _fixture.Database.OpenConnection();
        using var command = connection.CreateCommand();

        // The index holds the *normalized* text, where glyph folding has turned "skeletal" into
        // "5ke1eta1" — so the trigram to look for is "5ke", not "ske".
        command.CommandText = "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"5ke\"';";

        Assert.True(Convert.ToInt32(command.ExecuteScalar()) > 0);
    }

    [Fact]
    public void Updating_a_string_keeps_the_index_in_step()
    {
        var id = _fixture.Repository.Upsert("Summon a skeletal warrior", null, key: "s1");
        _fixture.Repository.Upsert("Summon a spectral archer", null, key: "s1");

        using var connection = _fixture.Database.OpenConnection();
        using var stale = connection.CreateCommand();
        stale.CommandText = "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"5ke\"';";
        Assert.Equal(0, Convert.ToInt32(stale.ExecuteScalar()));

        using var fresh = connection.CreateCommand();
        fresh.CommandText = "SELECT COUNT(*) FROM strings_fts WHERE strings_fts MATCH '\"arc\"';";
        Assert.True(Convert.ToInt32(fresh.ExecuteScalar()) > 0);

        Assert.Equal(1, _fixture.Repository.Count());
        Assert.NotNull(_fixture.Repository.GetById(id));
    }

    private sealed class StubFallback(string? translation) : ITranslationFallback
    {
        public bool IsEnabled { get; init; } = true;
        public int Calls { get; private set; }

        public Task<string?> TranslateAsync(string english, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(translation);
        }
    }
}
