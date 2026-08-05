using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CznTranslator.App;

/// <summary>
/// The API key at rest. Each value is encrypted with Windows DPAPI (CurrentUser scope) and stored
/// as base64 in a small JSON file under %LocalAppData%, so the key is never in git, never on a
/// command line, and only readable by this Windows user on this machine.
/// </summary>
public sealed class SecretStore
{
    public const string AnthropicKey = "ANTHROPIC_API_KEY";
    public const string OpenAiKey = "OPENAI_API_KEY";
    public const string DeepSeekKey = "DEEPSEEK_API_KEY";

    /// <summary>Maps a provider id (anthropic / openai / deepseek) to its stored secret name.</summary>
    public static string NameFor(string provider) => provider switch
    {
        "anthropic" => AnthropicKey,
        "deepseek" => DeepSeekKey,
        _ => OpenAiKey,
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CznTranslator.SecretStore.v1");

    private readonly string _path;

    public SecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CZN Translator",
            "secrets.json");
    }

    public bool Has(string name) => !string.IsNullOrEmpty(Get(name));

    public string? Get(string name)
    {
        var all = Load();
        if (!all.TryGetValue(name, out var encoded) || string.IsNullOrEmpty(encoded))
            return null;

        try
        {
            var plain = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Copied from another machine or user, or corrupted — treat as absent rather than crash.
            return null;
        }
    }

    public void Set(string name, string value)
    {
        var all = Load();
        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        all[name] = Convert.ToBase64String(cipher);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
