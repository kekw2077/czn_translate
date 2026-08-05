using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CznTranslator.App;

public sealed record TranslationItem(int Id, string En);

/// <summary>Thrown when a batch reply cannot be turned into id → translation; the batch is retried.</summary>
public sealed class BatchTranslationException(string message) : Exception(message);

/// <summary>
/// The C# port of tools/czn/apiclient.py: batch EN→RU through a hosted LLM (Anthropic Messages, or
/// any OpenAI-compatible chat endpoint), same strict JSON protocol. Batches of ids in, a JSON array
/// of {id, ru} out; a malformed or partial reply retries the whole batch rather than dropping
/// strings silently. Token usage is accumulated so the run can report what it cost.
/// </summary>
public sealed partial class ApiTranslationClient
{
    private const double Temperature = 0.2;
    private const int MaxOutputTokens = 8192;
    private static readonly int[] RetryableStatuses = [429, 500, 502, 503, 529];

    private static readonly Dictionary<string, string> DefaultModels = new()
    {
        ["anthropic"] = "claude-haiku-4-5-20251001",
        ["openai"] = "gpt-4o-mini",
        ["deepseek"] = "deepseek-chat",
    };
    private static readonly Dictionary<string, string> DefaultBaseUrls = new()
    {
        ["anthropic"] = "https://api.anthropic.com",
        ["openai"] = "https://api.openai.com/v1",
        ["deepseek"] = "https://api.deepseek.com",
    };

    [GeneratedRegex(@"^\s*```(?:json)?\s*(.*?)\s*```\s*$", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly HttpClient _http;

    public string Provider { get; }
    public string Model { get; }
    public long InputTokens { get; private set; }
    public long OutputTokens { get; private set; }
    public int Calls { get; private set; }

    public ApiTranslationClient(string provider, string apiKey, string? model = null, string? baseUrl = null, HttpClient? http = null)
    {
        if (!DefaultModels.ContainsKey(provider))
            throw new ArgumentException($"unknown provider '{provider}'", nameof(provider));
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is empty", nameof(apiKey));

        Provider = provider;
        _apiKey = apiKey;
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModels[provider] : model;
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrls[provider] : baseUrl).TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<Dictionary<int, string>> TranslateBatchAsync(
        IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string, string> glossary,
        CancellationToken ct,
        int attempts = 2)
    {
        if (items.Count == 0)
            return [];

        var system = BuildSystemPrompt(glossary);
        var prompt = BuildUserPrompt(items);
        var expected = items.Select(i => i.Id).ToHashSet();

        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var text = await GenerateAsync(system, prompt, ct);
            try
            {
                return ParseResponse(text, expected);
            }
            catch (BatchTranslationException ex)
            {
                last = ex;
            }
        }
        throw new BatchTranslationException($"batch failed after {attempts} attempts: {last?.Message}");
    }

    // --------------------------------------------------------------- transport

    private async Task<string> GenerateAsync(string system, string prompt, CancellationToken ct)
    {
        var url = Provider == "anthropic" ? $"{_baseUrl}/v1/messages" : $"{_baseUrl}/chat/completions";
        var payload = Provider == "anthropic"
            ? (object)new
            {
                model = Model,
                max_tokens = MaxOutputTokens,
                temperature = Temperature,
                system,
                messages = new[] { new { role = "user", content = prompt } },
            }
            : new
            {
                model = Model,
                temperature = Temperature,
                max_tokens = MaxOutputTokens,
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = prompt },
                },
            };

        var json = JsonSerializer.Serialize(payload);
        var delay = TimeSpan.FromSeconds(1);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (Provider == "anthropic")
            {
                request.Headers.Add("x-api-key", _apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Add("Authorization", $"Bearer {_apiKey}");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < 4)
            {
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return ExtractText(body);

            if (!RetryableStatuses.Contains((int)response.StatusCode) || attempt == 4)
                throw new BatchTranslationException($"HTTP {(int)response.StatusCode} from {Provider}: {Truncate(body, 400)}");

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
        }

        throw new BatchTranslationException($"{Provider} unreachable after retries");
    }

    private string ExtractText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (Provider == "anthropic")
        {
            var sb = new StringBuilder();
            foreach (var part in root.GetProperty("content").EnumerateArray())
            {
                if (part.TryGetProperty("type", out var t) && t.GetString() == "text")
                    sb.Append(part.GetProperty("text").GetString());
            }
            if (root.TryGetProperty("usage", out var u))
            {
                InputTokens += u.TryGetProperty("input_tokens", out var it) ? it.GetInt64() : 0;
                OutputTokens += u.TryGetProperty("output_tokens", out var ot) ? ot.GetInt64() : 0;
            }
            Calls++;
            return sb.ToString();
        }

        var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        if (root.TryGetProperty("usage", out var usage))
        {
            InputTokens += usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt64() : 0;
            OutputTokens += usage.TryGetProperty("completion_tokens", out var ctk) ? ctk.GetInt64() : 0;
        }
        Calls++;
        return text;
    }

    // ---------------------------------------------------------------- protocol

    private static Dictionary<int, string> ParseResponse(string text, HashSet<int> expected)
    {
        var stripped = text.Trim();
        var fence = FenceRegex().Match(stripped);
        if (fence.Success)
            stripped = fence.Groups[1].Value;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stripped);
        }
        catch (JsonException ex)
        {
            throw new BatchTranslationException($"reply is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new BatchTranslationException($"expected a JSON array, got {doc.RootElement.ValueKind}");

            var result = new Dictionary<int, string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var idEl)
                    || !entry.TryGetProperty("ru", out var ruEl))
                    throw new BatchTranslationException($"malformed entry: {entry}");
                if (!idEl.TryGetInt32(out var id))
                    throw new BatchTranslationException($"non-integer id: {idEl}");
                if (ruEl.ValueKind != JsonValueKind.String)
                    throw new BatchTranslationException($"translation for {id} is not a string");
                if (!expected.Contains(id))
                    throw new BatchTranslationException($"reply contains unrequested id {id}");
                result[id] = ruEl.GetString()!;
            }

            var missing = expected.Where(id => !result.ContainsKey(id)).ToList();
            if (missing.Count > 0)
                throw new BatchTranslationException($"reply is missing {missing.Count} id(s)");

            return result;
        }
    }

    private static string BuildUserPrompt(IReadOnlyList<TranslationItem> items) =>
        JsonSerializer.Serialize(items.Select(i => new { id = i.Id, en = i.En }));

    private static string BuildSystemPrompt(IReadOnlyDictionary<string, string> glossary)
    {
        var rendered = glossary.Count == 0
            ? "(пусто)"
            : string.Join("\n", glossary.OrderBy(kv => kv.Key).Select(kv => $"- {kv.Key} = {kv.Value}"));

        // Literal braces below are safe: this is a plain interpolation of {rendered}, no String.Format.
        return $$"""
            Ты переводчик игровой локализации. Переводишь с английского на русский
            интерфейс и сюжетные тексты тёмного фэнтези-рогалика.

            Правила:
            - Сохраняй ВСЕ плейсхолдеры ({0}, {value}, %s) и теги (<color=...>, </color>,
              <sprite=...>) точно как в оригинале, включая порядок.
            - Соблюдай глоссарий дословно, без склонений имён собственных в базовой форме.
            - Интерфейсные строки — коротко, в стиле игровых кнопок, без точки в конце.
            - Не добавляй пояснений, не переводи то, что выглядит как техническое ID.
            - Если строка непереводима (код, число, пустая) — верни её без изменений.

            Глоссарий:
            {{rendered}}

            Отвечай ТОЛЬКО валидным JSON-массивом вида [{"id": <int>, "ru": "<перевод>"}].
            Без markdown, без пояснений.
            """;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
