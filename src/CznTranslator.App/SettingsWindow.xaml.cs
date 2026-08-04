using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CznTranslator.Core.Config;
using CznTranslator.Lookup;

// UseWindowsForms is on for the tray, so System.Drawing.Brush is also in scope.
using Brush = System.Windows.Media.Brush;

namespace CznTranslator.App;

/// <summary>
/// The native settings window (replaces the web panel). Drives the same czn.db and config.json as
/// the running overlay. Saving writes config.json, which the running app's ConfigService watcher
/// picks up and applies live — so there is one code path for "settings changed", file or window.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep '#' in colours and any Cyrillic readable rather than \uXXXX-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConfigService _config;
    private readonly StringRepository? _repository;
    private readonly string _configPath;
    private readonly SecretStore _secrets = new();
    private StackPanel[] _pages = [];
    private string _keyProvider = "anthropic";

    public SettingsWindow(ConfigService config, StringRepository? repository, string configPath)
    {
        _config = config;
        _repository = repository;
        _configPath = configPath;
        InitializeComponent();

        _pages = [PageDash, PageOverlay, PageKey, PageTranslate, PageReview, PageUpdate];

        LoadOverlayFields();
        LoadDashboard();
        SetKeyProvider("anthropic");
        RefreshKeyStatus();
        Footer.Text = $"База: {Path.GetFileName(_repository?.Database.DatabasePath ?? "нет")}";
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0)
            return;

        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;

        if (Nav.SelectedIndex == 0)
            LoadDashboard();
        else if (Nav.SelectedIndex == 2)
            RefreshKeyStatus();
    }

    // ---------------------------------------------------------------- dashboard

    private void RefreshDash_Click(object sender, RoutedEventArgs e) => LoadDashboard();

    private void LoadDashboard()
    {
        Tiles.Items.Clear();

        if (_repository is null)
        {
            CovLabel.Text = "База не подключена.";
            CovFill.Width = new GridLength(0, GridUnitType.Star);
            CovRest.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        IReadOnlyDictionary<string, int> counts;
        try
        {
            counts = _repository.StatusCounts();
        }
        catch (Exception ex)
        {
            CovLabel.Text = $"Не удалось прочитать базу: {ex.Message}";
            return;
        }

        int C(string s) => counts.TryGetValue(s, out var v) ? v : 0;
        var total = counts.Values.Sum();
        var translated = C("mt") + C("reviewed") + C("locked");
        var pending = C("new") + C("stale");
        var accepted = C("reviewed") + C("locked");

        AddTile("Всего строк", total, null);
        AddTile("Переведено", translated, (Brush)FindResource("Good"));
        AddTile("В очереди", pending, (Brush)FindResource("Accent"));
        AddTile("На ревью", C("mt"), null);
        AddTile("Принято", accepted, (Brush)FindResource("Good"));

        var coverage = total > 0 ? (double)translated / total : 0.0;
        CovLabel.Text = $"Покрытие переводом — {coverage:P0}";
        CovFill.Width = new GridLength(coverage, GridUnitType.Star);
        CovRest.Width = new GridLength(1 - coverage, GridUnitType.Star);
    }

    private void AddTile(string label, int value, Brush? accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = value.ToString("N0", CultureInfo.CurrentCulture),
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = accent ?? (Brush)FindResource("Text1"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)FindResource("Muted"),
        });

        Tiles.Items.Add(new Border
        {
            Background = (Brush)FindResource("Tile"),
            BorderBrush = (Brush)FindResource("Border1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 22, 14),
            Margin = new Thickness(0, 0, 12, 12),
            MinWidth = 130,
            Child = stack,
        });
    }

    // ------------------------------------------------------------------ overlay

    private void LoadOverlayFields()
    {
        var c = _config.Current;
        FontFamilyBox.Text = c.Overlay.FontFamily;
        FontSizeBox.Text = c.Overlay.FontSize.ToString(CultureInfo.InvariantCulture);
        TextColor.Text = c.Overlay.TextColor;
        BackdropColor.Text = c.Overlay.BackdropColor;
        BackdropOpacity.Text = c.Overlay.BackdropOpacity.ToString(CultureInfo.InvariantCulture);
        ProcessName.Text = c.Capture.ProcessName;
        WindowClass.Text = c.Capture.WindowClass;
    }

    private void SaveOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseDouble(FontSizeBox.Text, out var fontSize) || fontSize is < 6 or > 96)
        {
            Warn("Размер шрифта: число 6–96.");
            return;
        }
        if (!TryParseDouble(BackdropOpacity.Text, out var opacity) || opacity is < 0 or > 1)
        {
            Warn("Прозрачность: число 0–1.");
            return;
        }
        if (!IsHexColor(TextColor.Text) || !IsHexColor(BackdropColor.Text))
        {
            Warn("Цвет должен быть в формате #RRGGBB.");
            return;
        }

        try
        {
            // Targeted JSON edit: only the touched fields change, so zones and every other section
            // keep their exact formatting and camelCase keys.
            var root = JsonNode.Parse(File.ReadAllText(_configPath))?.AsObject()
                       ?? throw new InvalidDataException("config.json is not an object.");

            var overlay = Section(root, "overlay");
            overlay["fontFamily"] = FontFamilyBox.Text.Trim();
            overlay["fontSize"] = fontSize;
            overlay["textColor"] = TextColor.Text.Trim();
            overlay["backdropColor"] = BackdropColor.Text.Trim();
            overlay["backdropOpacity"] = opacity;

            var capture = Section(root, "capture");
            capture["processName"] = ProcessName.Text.Trim();
            capture["windowClass"] = WindowClass.Text.Trim();

            File.WriteAllText(_configPath, root.ToJsonString(WriteOptions));
            Warn("Сохранено. Применяется на лету.");
        }
        catch (Exception ex)
        {
            Warn($"Ошибка сохранения: {ex.Message}");
        }
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }

    private void Warn(string message) => OverlayHint.Text = message;

    // ---------------------------------------------------------------------- key

    private void ProvAnthropic_Click(object sender, RoutedEventArgs e) => SetKeyProvider("anthropic");
    private void ProvOpenai_Click(object sender, RoutedEventArgs e) => SetKeyProvider("openai");

    private void SetKeyProvider(string provider)
    {
        _keyProvider = provider;
        var primary = (Style)FindResource("Btn");
        var ghost = (Style)FindResource("Ghost");
        ProvAnthropic.Style = provider == "anthropic" ? primary : ghost;
        ProvOpenai.Style = provider == "openai" ? primary : ghost;
    }

    private void RefreshKeyStatus()
    {
        var anthropic = _secrets.Has(SecretStore.AnthropicKey);
        var openai = _secrets.Has(SecretStore.OpenAiKey);
        KeyStatus.Text = $"Anthropic — {(anthropic ? "ключ есть ✓" : "нет")}   ·   OpenAI — {(openai ? "ключ есть ✓" : "нет")}";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            KeyHint.Text = "Введите ключ.";
            return;
        }

        var name = _keyProvider == "anthropic" ? SecretStore.AnthropicKey : SecretStore.OpenAiKey;
        try
        {
            _secrets.Set(name, key);
            KeyBox.Clear();
            RefreshKeyStatus();
            KeyHint.Text = "Ключ сохранён (зашифрован DPAPI).";
        }
        catch (Exception ex)
        {
            KeyHint.Text = $"Ошибка: {ex.Message}";
        }
    }

    private static bool TryParseDouble(string text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool IsHexColor(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '#' || (text.Length != 7 && text.Length != 9))
            return false;
        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i]))
                return false;
        return true;
    }
}
