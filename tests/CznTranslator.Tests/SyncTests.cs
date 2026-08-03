using System.Net;
using System.Text;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Sync;
using Xunit;

namespace CznTranslator.Tests;

public class PackWatcherTests : IDisposable
{
    private readonly string _packPath = Path.Combine(Path.GetTempPath(), $"czn-pack-{Guid.NewGuid():N}.pack");

    public void Dispose()
    {
        if (File.Exists(_packPath))
            File.Delete(_packPath);
    }

    private void WritePack(string content) => File.WriteAllText(_packPath, content);

    /// <summary>A name no process can plausibly have, so the "game closed" branch is the one under test.</summary>
    private const string AbsentProcess = "czn-not-a-real-process-name";

    [Fact]
    public async Task Md5_matches_the_reference_value()
    {
        WritePack("abc");
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", await PackWatcher.ComputeMd5Async(_packPath));
    }

    [Fact]
    public async Task An_unimported_install_is_reported_as_such()
    {
        WritePack("pack v1");
        var watcher = new PackWatcher(new FakeVersionStore());

        var result = await watcher.CheckAsync(_packPath, AbsentProcess);

        Assert.Equal(PackCheckOutcome.NeverImported, result.Outcome);
        Assert.NotNull(result.Md5);
    }

    [Fact]
    public async Task A_matching_hash_is_up_to_date()
    {
        WritePack("pack v1");
        var md5 = await PackWatcher.ComputeMd5Async(_packPath);
        var store = new FakeVersionStore(new PackVersion(3, md5, DateTimeOffset.UtcNow, null));

        var result = await new PackWatcher(store).CheckAsync(_packPath, AbsentProcess);

        Assert.Equal(PackCheckOutcome.UpToDate, result.Outcome);
        Assert.Equal(3, result.Known!.Version);
    }

    [Fact]
    public async Task Hash_comparison_ignores_case()
    {
        WritePack("pack v1");
        var md5 = (await PackWatcher.ComputeMd5Async(_packPath)).ToUpperInvariant();
        var store = new FakeVersionStore(new PackVersion(1, md5, null, null));

        var result = await new PackWatcher(store).CheckAsync(_packPath, AbsentProcess);

        Assert.Equal(PackCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task A_changed_pack_is_reported_as_a_patch_without_rebuilding_anything()
    {
        WritePack("pack v2");
        var store = new FakeVersionStore(new PackVersion(1, "0123456789abcdef0123456789abcdef", null, null));

        var result = await new PackWatcher(store).CheckAsync(_packPath, AbsentProcess);

        Assert.Equal(PackCheckOutcome.PatchDetected, result.Outcome);
        Assert.Contains("patch", result.Message, StringComparison.OrdinalIgnoreCase);

        // §7 is explicit that nothing is rebuilt automatically.
        Assert.Equal(0, store.RecordCalls);
    }

    [Fact]
    public async Task A_missing_pack_does_not_throw()
    {
        var result = await new PackWatcher(new FakeVersionStore())
            .CheckAsync(Path.Combine(Path.GetTempPath(), "definitely-not-here.pack"), AbsentProcess);

        Assert.Equal(PackCheckOutcome.PackMissing, result.Outcome);
    }

    [Fact]
    public async Task An_unconfigured_path_disables_the_check_quietly()
    {
        var result = await new PackWatcher(new FakeVersionStore()).CheckAsync("", AbsentProcess);

        Assert.Equal(PackCheckOutcome.PackMissing, result.Outcome);
        Assert.Contains("packPath", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pack_is_never_read_while_the_game_is_running()
    {
        WritePack("pack v1");

        // The current test host stands in for the game process.
        var runningProcess = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        var result = await new PackWatcher(new FakeVersionStore()).CheckAsync(_packPath, runningProcess);

        Assert.Equal(PackCheckOutcome.GameRunning, result.Outcome);
        Assert.Null(result.Md5);
    }

    private sealed class FakeVersionStore(PackVersion? latest = null) : IPackVersionStore
    {
        public int RecordCalls { get; private set; }

        public Task<PackVersion?> GetLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(latest);

        public Task<int> RecordAsync(string packMd5, string? note, CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            return Task.FromResult(1);
        }
    }
}

public class OllamaFallbackTests
{
    private static SyncSection Settings(int timeoutSeconds = 3, int maxParallel = 2) => new()
    {
        OllamaEndpoint = "http://127.0.0.1:11434",
        Model = "qwen3-loc",
        TimeoutSeconds = timeoutSeconds,
        MaxParallelRequests = maxParallel
    };

    [Theory]
    [InlineData("Кровавый пакт", "Кровавый пакт")]
    [InlineData("\"Кровавый пакт\"", "Кровавый пакт")]
    [InlineData("«Кровавый пакт»", "Кровавый пакт")]
    [InlineData("  Кровавый пакт  ", "Кровавый пакт")]
    [InlineData("Кровавый пакт\nПояснение: это карта", "Кровавый пакт")]
    public void Sanitize_strips_the_wrappers_models_add_anyway(string raw, string expected)
    {
        Assert.Equal(expected, OllamaFallback.Sanitize(raw));
    }

    [Fact]
    public async Task Returns_the_translated_string()
    {
        using var handler = new StubHandler(_ => Task.FromResult(Json("""{"response":"Кровавый пакт"}""")));
        using var fallback = new OllamaFallback(Settings(), new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Equal("Кровавый пакт", await fallback.TranslateAsync("Blood Pact"));
    }

    [Fact]
    public async Task A_disabled_fallback_makes_no_request()
    {
        using var handler = new StubHandler(_ => Task.FromResult(Json("""{"response":"нет"}""")));
        var settings = Settings();
        settings.FallbackEnabled = false;

        using var fallback = new OllamaFallback(settings, new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Null(await fallback.TranslateAsync("Blood Pact"));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task An_unreachable_server_degrades_to_null_rather_than_throwing()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("no route to host"));
        using var fallback = new OllamaFallback(Settings(), new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Null(await fallback.TranslateAsync("Blood Pact"));
    }

    [Fact]
    public async Task An_error_status_degrades_to_null()
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var fallback = new OllamaFallback(Settings(), new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Null(await fallback.TranslateAsync("Blood Pact"));
    }

    [Fact]
    public async Task A_slow_server_is_abandoned_at_the_timeout()
    {
        // A translation that lands after the screen has changed is worse than none.
        using var handler = new StubHandler(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json("""{"response":"поздно"}""");
        });
        using var fallback = new OllamaFallback(
            Settings(timeoutSeconds: 1),
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var started = DateTimeOffset.UtcNow;
        var result = await fallback.TranslateAsync("Blood Pact");

        Assert.Null(result);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Requests_beyond_the_concurrency_limit_are_dropped_not_queued()
    {
        var release = new TaskCompletionSource();
        using var handler = new StubHandler(async _ =>
        {
            await release.Task;
            return Json("""{"response":"ок"}""");
        });

        using var fallback = new OllamaFallback(
            Settings(maxParallel: 1),
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var inFlight = fallback.TranslateAsync("first");
        await Task.Delay(100);

        // The second call must come back immediately with null instead of waiting in line.
        var rejected = await fallback.TranslateAsync("second");
        Assert.Null(rejected);
        Assert.Equal(1, handler.Calls);

        release.SetResult();
        Assert.Equal("ок", await inFlight);
    }

    [Fact]
    public async Task An_empty_response_counts_as_a_miss()
    {
        using var handler = new StubHandler(_ => Task.FromResult(Json("""{"response":"   "}""")));
        using var fallback = new OllamaFallback(Settings(), new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Null(await fallback.TranslateAsync("Blood Pact"));
    }

    [Fact]
    public async Task Blank_input_never_reaches_the_server()
    {
        using var handler = new StubHandler(_ => Task.FromResult(Json("""{"response":"что-то"}""")));
        using var fallback = new OllamaFallback(Settings(), new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        Assert.Null(await fallback.TranslateAsync("   "));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _respond = respond;
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return _respond(cancellationToken);
        }
    }
}
