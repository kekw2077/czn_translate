using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using Serilog;

namespace CznTranslator.Sync;

/// <summary>
/// Runtime fallback against Ollama over Tailscale (TZ §7).
/// <para>
/// Bounded on both axes: a hard per-request timeout, and at most
/// <c>sync.maxParallelRequests</c> in flight. Anything that does not come back in time shows the
/// original English and counts as a miss — a translation that arrives after the screen changed is
/// worse than no translation.
/// </para>
/// </summary>
public sealed class OllamaFallback : ITranslationFallback, IDisposable
{
    private const string SystemPrompt =
        """
        Ты переводчик игровой локализации. Переводишь с английского на русский интерфейс и
        сюжетные тексты тёмного фэнтези-рогалика.

        Правила:
        - Сохраняй ВСЕ плейсхолдеры ({0}, {value}, %s) и теги (<color=...>, </color>,
          <sprite=...>) точно как в оригинале, включая порядок.
        - Интерфейсные строки — коротко, в стиле игровых кнопок, без точки в конце.
        - Не добавляй пояснений, не переводи то, что выглядит как техническое ID.
        - Если строка непереводима (код, число, пустая) — верни её без изменений.

        Ответь ТОЛЬКО переводом, одной строкой, без кавычек и пояснений.
        """;

    private readonly HttpClient _http;
    private readonly SyncSection _settings;
    private readonly SemaphoreSlim _concurrency;
    private readonly ILogger _log;
    private readonly bool _ownsHttpClient;

    public OllamaFallback(SyncSection settings, HttpClient? httpClient = null, ILogger? log = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log ?? Log.Logger;
        _ownsHttpClient = httpClient is null;

        _http = httpClient ?? new HttpClient();
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri(settings.OllamaEndpoint.TrimEnd('/') + "/");

        _concurrency = new SemaphoreSlim(settings.MaxParallelRequests, settings.MaxParallelRequests);
    }

    public bool IsEnabled => _settings.FallbackEnabled;

    public async Task<string?> TranslateAsync(string english, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(english))
            return null;

        // Non-blocking admission: when the server is already saturated, this request is dropped
        // rather than queued behind requests whose screens are long gone.
        if (!await _concurrency.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            _log.Debug("Ollama fallback is at its concurrency limit, skipping '{Text}'.", english);
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));

            var request = new GenerateRequest(
                _settings.Model,
                english,
                SystemPrompt,
                Stream: false,
                Options: new GenerateOptions(Temperature: 0.2));

            using var response = await _http
                .PostAsJsonAsync("api/generate", request, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _log.Warning("Ollama returned {Status} for the fallback request.", (int)response.StatusCode);
                return null;
            }

            var body = await response.Content
                .ReadFromJsonAsync<GenerateResponse>(timeout.Token)
                .ConfigureAwait(false);

            var translated = body?.Response?.Trim();
            return string.IsNullOrWhiteSpace(translated) ? null : Sanitize(translated);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Debug("Ollama fallback timed out after {Timeout}s.", _settings.TimeoutSeconds);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _log.Debug(ex, "Ollama fallback is unavailable.");
            return null;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Models sometimes wrap the answer in quotes or add a leading label despite the instruction.
    /// Stripping that here keeps the junk out of <c>strings</c>, where it would become permanent.
    /// </summary>
    internal static string Sanitize(string text)
    {
        var cleaned = text.Trim();

        var newline = cleaned.IndexOf('\n');
        if (newline >= 0)
            cleaned = cleaned[..newline].Trim();

        if (cleaned.Length >= 2 &&
            ((cleaned[0] == '"' && cleaned[^1] == '"') ||
             (cleaned[0] == '«' && cleaned[^1] == '»')))
        {
            cleaned = cleaned[1..^1].Trim();
        }

        return cleaned;
    }

    public void Dispose()
    {
        _concurrency.Dispose();
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] GenerateOptions Options);

    private sealed record GenerateOptions(
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record GenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
