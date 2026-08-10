using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using CznTranslator.Lookup;

// UseWindowsForms is on for the tray, so the WinForms/Drawing twins of these are also in scope;
// pin every control type built in code to its WPF version.
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

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

        _pages = [PageDash, PageOverlay, PageKey, PageTranslate, PageStation, PageReview, PageUpdate];

        LoadOverlayFields();
        LoadDashboard();
        SetKeyProvider("anthropic");
        SetTransProvider("anthropic");
        RefreshKeyStatus();
        LoadStationFields();
        InitUpdateTab();
        Footer.Text = $"База: {Path.GetFileName(_repository?.Database.DatabasePath ?? "нет")}";
    }

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_pages.Length == 0)
            return;

        for (var i = 0; i < _pages.Length; i++)
            _pages[i].Visibility = i == Nav.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;

        // By page object, not a magic index, so inserting a tab does not silently rewire these.
        var page = Nav.SelectedIndex >= 0 && Nav.SelectedIndex < _pages.Length ? _pages[Nav.SelectedIndex] : null;
        if (page == PageDash)
            LoadDashboard();
        else if (page == PageKey)
            RefreshKeyStatus();
        else if (page == PageReview)
            LoadReview(0);
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
    private void ProvDeepseek_Click(object sender, RoutedEventArgs e) => SetKeyProvider("deepseek");

    private void SetKeyProvider(string provider)
    {
        _keyProvider = provider;
        var primary = (Style)FindResource("Btn");
        var ghost = (Style)FindResource("Ghost");
        ProvAnthropic.Style = provider == "anthropic" ? primary : ghost;
        ProvOpenai.Style = provider == "openai" ? primary : ghost;
        ProvDeepseek.Style = provider == "deepseek" ? primary : ghost;
    }

    private void RefreshKeyStatus()
    {
        var anthropic = _secrets.Has(SecretStore.AnthropicKey);
        var openai = _secrets.Has(SecretStore.OpenAiKey);
        var deepseek = _secrets.Has(SecretStore.DeepSeekKey);
        KeyStatus.Text = $"Anthropic — {(anthropic ? "ключ есть ✓" : "нет")}   ·   "
                       + $"OpenAI — {(openai ? "ключ есть ✓" : "нет")}   ·   "
                       + $"DeepSeek — {(deepseek ? "ключ есть ✓" : "нет")}";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            KeyHint.Text = "Введите ключ.";
            return;
        }

        var name = SecretStore.NameFor(_keyProvider);
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

    // ------------------------------------------------------------------- review

    private const int ReviewPageSize = 25;
    private int _reviewOffset;

    private void ReviewReload_Click(object sender, RoutedEventArgs e) => LoadReview(0);

    private void LoadReview(int offset)
    {
        ReviewList.Children.Clear();
        ReviewPager.Children.Clear();

        if (_repository is null)
        {
            ReviewTitle.Text = "База не подключена.";
            return;
        }

        int total;
        IReadOnlyList<StringRow> rows;
        try
        {
            total = _repository.CountByStatus(StringStatus.MachineTranslated);
            rows = _repository.Page(StringStatus.MachineTranslated, ReviewPageSize, offset);
        }
        catch (Exception ex)
        {
            ReviewTitle.Text = $"Ошибка чтения базы: {ex.Message}";
            return;
        }

        _reviewOffset = offset;
        ReviewTitle.Text = $"Очередь — {total:N0} строк";

        if (rows.Count == 0)
        {
            ReviewList.Children.Add(new TextBlock
            {
                Text = total == 0 ? "Очередь пуста 🎉" : "На этой странице пусто.",
                Foreground = (Brush)FindResource("Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 24, 0, 8),
            });
            return;
        }

        foreach (var row in rows)
            ReviewList.Children.Add(BuildReviewCard(row));

        BuildReviewPager(total, offset);
    }

    private Border BuildReviewCard(StringRow row)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"#{row.Id}  {row.Key ?? "(без ключа)"}",
            Foreground = (Brush)FindResource("Muted"),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 11.5,
        });
        stack.Children.Add(new TextBlock
        {
            Text = row.English,
            Foreground = (Brush)FindResource("Text1"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 8),
        });

        var ruBox = new TextBox
        {
            Text = row.Russian ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 34,
        };
        stack.Children.Add(ruBox);

        foreach (var warning in TranslationValidator.Validate(row.English, row.Russian))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "⚠ " + warning,
                Foreground = (Brush)FindResource("Accent"),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var card = new Border
        {
            Background = (Brush)FindResource("Panel2"),
            BorderBrush = (Brush)FindResource("Border1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(SmallButton("Принять", "Btn", () => SaveReview(row.Id, ruBox.Text, StringStatus.Reviewed, card)));
        buttons.Children.Add(SmallButton("Принять и закрепить", "Ghost", () => SaveReview(row.Id, ruBox.Text, StringStatus.Locked, card)));
        stack.Children.Add(buttons);

        card.Child = stack;
        return card;
    }

    private Button SmallButton(string content, string styleKey, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource(styleKey),
            Padding = new Thickness(12, 6, 12, 6),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 8, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void BuildReviewPager(int total, int offset)
    {
        if (offset > 0)
            ReviewPager.Children.Add(SmallButton("← назад", "Ghost", () => LoadReview(Math.Max(0, offset - ReviewPageSize))));

        ReviewPager.Children.Add(new TextBlock
        {
            Text = $"{offset + 1}–{Math.Min(offset + ReviewPageSize, total)} из {total:N0}",
            Foreground = (Brush)FindResource("Muted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0),
        });

        if (offset + ReviewPageSize < total)
            ReviewPager.Children.Add(SmallButton("вперёд →", "Ghost", () => LoadReview(offset + ReviewPageSize)));
    }

    private void SaveReview(long id, string russian, StringStatus status, Border card)
    {
        if (_repository is null)
            return;

        try
        {
            _repository.SetTranslation(id, russian, status);
            ReviewList.Children.Remove(card);
            if (ReviewList.Children.Count == 0)
                LoadReview(_reviewOffset);
        }
        catch (Exception ex)
        {
            ReviewTitle.Text = $"Ошибка сохранения: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- translate

    private string _transProvider = "anthropic";
    private CancellationTokenSource? _translateCts;
    private readonly List<string> _translateLog = [];

    private void TransProvAnthropic_Click(object sender, RoutedEventArgs e) => SetTransProvider("anthropic");
    private void TransProvOpenai_Click(object sender, RoutedEventArgs e) => SetTransProvider("openai");
    private void TransProvDeepseek_Click(object sender, RoutedEventArgs e) => SetTransProvider("deepseek");

    private void SetTransProvider(string provider)
    {
        _transProvider = provider;
        var primary = (Style)FindResource("Btn");
        var ghost = (Style)FindResource("Ghost");
        TransProvAnthropic.Style = provider == "anthropic" ? primary : ghost;
        TransProvOpenai.Style = provider == "openai" ? primary : ghost;
        TransProvDeepseek.Style = provider == "deepseek" ? primary : ghost;
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null)
        {
            TransHint.Text = "База не подключена.";
            return;
        }

        var secretName = SecretStore.NameFor(_transProvider);
        var apiKey = _secrets.Get(secretName);
        if (string.IsNullOrEmpty(apiKey))
        {
            TransHint.Text = "Нет ключа — задайте его во вкладке «Ключ API».";
            return;
        }

        int? limit = int.TryParse(TransLimit.Text?.Trim(), out var n) && n > 0 ? n : null;
        var model = string.IsNullOrWhiteSpace(TransModel.Text) ? null : TransModel.Text.Trim();
        var baseUrl = string.IsNullOrWhiteSpace(TransBaseUrl.Text) ? null : TransBaseUrl.Text.Trim();

        _translateLog.Clear();
        TransLog.Text = string.Empty;
        TransProgress.Visibility = Visibility.Visible;
        TransLogWrap.Visibility = Visibility.Visible;
        TransHint.Text = string.Empty;
        TransStop.IsEnabled = true;
        _translateCts = new CancellationTokenSource();

        var client = new ApiTranslationClient(_transProvider, apiKey, model, baseUrl);
        var translator = new BatchTranslator(_repository, client);
        var progress = new Progress<TranslationProgress>(OnTransProgress);
        AppendTransLog($"Провайдер {_transProvider}, модель {client.Model}.");

        try
        {
            await Task.Run(() => translator.RunAsync(limit, progress, _translateCts.Token), _translateCts.Token);
        }
        catch (OperationCanceledException)
        {
            AppendTransLog("Остановлено.");
            TransHint.Text = "Остановлено.";
        }
        catch (Exception ex)
        {
            AppendTransLog($"Ошибка: {ex.Message}");
            TransHint.Text = "Ошибка — см. лог.";
        }
        finally
        {
            TransStop.IsEnabled = false;
            _translateCts?.Dispose();
            _translateCts = null;
            LoadDashboard();
        }
    }

    private void TransStop_Click(object sender, RoutedEventArgs e) => _translateCts?.Cancel();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null)
        {
            ImportHint.Text = "База не подключена.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Файл перевода: JSON вида {\"english\": \"русский\"}",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(dialog.FileName));
            if (map is null || map.Count == 0)
            {
                ImportHint.Text = "Файл пуст или не в формате {english: русский}.";
                return;
            }

            var applied = _repository.ApplyTranslationsByEnglish(map, StringStatus.MachineTranslated);
            ImportHint.Text = $"Загружено: {applied:N0} строк (статус mt). Проверьте во вкладке «Ревью».";
            LoadDashboard();
        }
        catch (Exception ex)
        {
            ImportHint.Text = $"Ошибка: {ex.Message}";
        }
    }

    private void OnTransProgress(TranslationProgress p)
    {
        var fraction = p.Total > 0 ? Math.Clamp((double)p.Done / p.Total, 0, 1) : (p.Finished ? 1.0 : 0.0);
        TransFill.Width = new GridLength(fraction, GridUnitType.Star);
        TransRest.Width = new GridLength(1 - fraction, GridUnitType.Star);
        TransPct.Text = $"{fraction:P0}";
        TransStat.Text = p.Total > 0 ? $"{p.Done:N0} / {p.Total:N0}" : "—";

        var tokens = p.InputTokens + p.OutputTokens > 0
            ? $"  ·  токены: {p.InputTokens:N0}+{p.OutputTokens:N0}"
            : string.Empty;
        AppendTransLog(p.Message + tokens);

        if (p.Finished)
            TransHint.Text = p.Message;
    }

    private void AppendTransLog(string line)
    {
        _translateLog.Add(line);
        if (_translateLog.Count > 200)
            _translateLog.RemoveAt(0);
        TransLog.Text = string.Join("\n", _translateLog);
        TransLogScroll.ScrollToEnd();
    }

    // ------------------------------------------------------------------- update

    private const string DefaultPackPath =
        @"C:\ProgramData\Smilegate\Games\ChaosZeroNightmare\bin\appdata\cznlive\data.pack";

    private CancellationTokenSource? _updateCts;
    private readonly List<string> _updateLog = [];

    private void InitUpdateTab()
    {
        UpdatePack.Text = DefaultPackPath;
        if (!PackExtractor.TryLoadDefault(out _, out var error))
        {
            UpdateCheckBtn.IsEnabled = false;
            UpdateApplyBtn.IsEnabled = false;
            UpdateHint.Text = error;
        }
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => _ = RunUpdate(apply: false);

    private void UpdateApply_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "Извлечь из data.pack и применить изменения к базе?",
                "CZN Translator", System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK)
            _ = RunUpdate(apply: true);
    }

    private async Task RunUpdate(bool apply)
    {
        if (_repository is null)
        {
            UpdateHint.Text = "База не подключена.";
            return;
        }

        var packPath = UpdatePack.Text?.Trim() ?? string.Empty;
        if (!System.IO.File.Exists(packPath))
        {
            UpdateHint.Text = "data.pack не найден по этому пути.";
            return;
        }

        if (!PackExtractor.TryLoadDefault(out var extractor, out var error))
        {
            UpdateHint.Text = error;
            return;
        }

        _updateLog.Clear();
        UpdateLog.Text = string.Empty;
        UpdateLogWrap.Visibility = Visibility.Visible;
        UpdateTiles.Items.Clear();
        UpdateCheckBtn.IsEnabled = false;
        UpdateApplyBtn.IsEnabled = false;
        UpdateHint.Text = apply ? "Извлечение и применение…" : "Извлечение и сравнение…";
        _updateCts = new CancellationTokenSource();

        var updater = new PackUpdater(_repository, extractor!);
        var progress = new Progress<string>(AppendUpdateLog);

        try
        {
            var diff = await Task.Run(() => updater.RunAsync(packPath, apply, progress, _updateCts.Token), _updateCts.Token);
            ShowUpdateTiles(diff);
            UpdateHint.Text = apply ? "Применено." : "Проверка завершена.";
            if (apply)
                LoadDashboard();
        }
        catch (OperationCanceledException)
        {
            UpdateHint.Text = "Остановлено.";
        }
        catch (Exception ex)
        {
            AppendUpdateLog($"Ошибка: {ex.Message}");
            UpdateHint.Text = "Ошибка — см. лог.";
        }
        finally
        {
            UpdateCheckBtn.IsEnabled = true;
            UpdateApplyBtn.IsEnabled = true;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private void ShowUpdateTiles(PackDiff diff)
    {
        UpdateTiles.Items.Clear();
        AddUpdateTile("Новые", diff.New, (Brush)FindResource("Accent"));
        AddUpdateTile("Изменены", diff.Changed, (Brush)FindResource("Accent"));
        AddUpdateTile("Удалены", diff.Removed, null);
        AddUpdateTile("Без изменений", diff.Unchanged, null);
    }

    private void AddUpdateTile(string label, int value, Brush? accent)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = value.ToString("N0", CultureInfo.CurrentCulture),
            FontSize = 22,
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
        UpdateTiles.Items.Add(new Border
        {
            Background = (Brush)FindResource("Tile"),
            BorderBrush = (Brush)FindResource("Border1"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 22, 12),
            Margin = new Thickness(0, 0, 12, 0),
            MinWidth = 120,
            Child = stack,
        });
    }

    private void AppendUpdateLog(string line)
    {
        _updateLog.Add(line);
        if (_updateLog.Count > 200)
            _updateLog.RemoveAt(0);
        UpdateLog.Text = string.Join("\n", _updateLog);
        UpdateLogScroll.ScrollToEnd();
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

    // ------------------------------------------------------------------ station

    private const string StationDefaultEndpoint = "http://127.0.0.1:11434";
    private const string StationDefaultModel = "qwen3:4b";

    private CancellationTokenSource? _stationCts;
    private readonly List<string> _stationLog = [];

    private sealed record StationSettings(string Endpoint, string Model, int Batch, int Timeout, int Retries, int NumThread);

    private void LoadStationFields()
    {
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_configPath))?.AsObject();
            var st = root?["station"] as JsonObject;
            var sync = root?["sync"] as JsonObject;
            StationEndpoint.Text = StStr(st, "endpoint") ?? StStr(sync, "ollamaEndpoint") ?? StationDefaultEndpoint;
            StationModel.Text = StStr(st, "model") ?? StationDefaultModel;
            StationBatch.Text = (StInt(st, "batch") ?? 25).ToString(CultureInfo.InvariantCulture);
            StationTimeout.Text = (StInt(st, "timeoutSeconds") ?? 300).ToString(CultureInfo.InvariantCulture);
            StationRetries.Text = (StInt(st, "retries") ?? 2).ToString(CultureInfo.InvariantCulture);
            StationThreads.Text = (StInt(st, "numThread") ?? 0).ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            StationEndpoint.Text = StationDefaultEndpoint;
            StationModel.Text = StationDefaultModel;
            StationBatch.Text = "25";
            StationTimeout.Text = "300";
            StationRetries.Text = "2";
            StationThreads.Text = "0";
        }
    }

    private static string? StStr(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : null;

    private static int? StInt(JsonObject? o, string key)
    {
        if (o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var si)) return si;
        }
        return null;
    }

    private bool TryReadStationFields(out StationSettings settings, out string error)
    {
        settings = new StationSettings(StationDefaultEndpoint, StationDefaultModel, 25, 300, 2, 0);
        var endpoint = StationEndpoint.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(endpoint) || !(endpoint.StartsWith("http://") || endpoint.StartsWith("https://")))
        {
            error = "Адрес станции должен начинаться с http:// или https://";
            return false;
        }
        var model = string.IsNullOrWhiteSpace(StationModel.Text) ? StationDefaultModel : StationModel.Text.Trim();
        int Parse(string? t, int fallback) => int.TryParse(t?.Trim(), out var v) && v > 0 ? v : fallback;
        // 0 (or blank) means "let Ollama pick", so it is allowed through rather than forced positive.
        var threads = int.TryParse(StationThreads.Text?.Trim(), out var t) && t > 0 ? t : 0;
        settings = new StationSettings(
            endpoint.TrimEnd('/'), model,
            Parse(StationBatch.Text, 25), Parse(StationTimeout.Text, 300), Parse(StationRetries.Text, 2), threads);
        error = string.Empty;
        return true;
    }

    private JsonObject StationJson(StationSettings s) => new()
    {
        ["kind"] = "ollama",
        ["endpoint"] = s.Endpoint,
        ["model"] = s.Model,
        ["batch"] = s.Batch,
        ["timeoutSeconds"] = s.Timeout,
        ["retries"] = s.Retries,
        ["numThread"] = s.NumThread,
        ["chunk"] = 100,
    };

    private void StationSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadStationFields(out var s, out var error))
        {
            StationHint.Text = error;
            return;
        }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_configPath))?.AsObject()
                       ?? throw new InvalidDataException("config.json is not an object.");
            var st = Section(root, "station");
            st["kind"] = "ollama";
            st["endpoint"] = s.Endpoint;
            st["model"] = s.Model;
            st["batch"] = s.Batch;
            st["timeoutSeconds"] = s.Timeout;
            st["retries"] = s.Retries;
            st["numThread"] = s.NumThread;
            if (st["chunk"] is null) st["chunk"] = 100;
            File.WriteAllText(_configPath, root.ToJsonString(WriteOptions));
            StationHint.Text = "Настройки станции сохранены.";
        }
        catch (Exception ex)
        {
            StationHint.Text = $"Ошибка сохранения: {ex.Message}";
        }
    }

    private bool TryResolveRuntime(out string python, out string toolsDir, out string workDir, out string error)
    {
        var baseDir = AppContext.BaseDirectory;
        toolsDir = Path.Combine(baseDir, "tools");
        workDir = Path.Combine(baseDir, "station_work");
        var bundled = Path.Combine(baseDir, "runtime", "python", "python.exe");
        python = File.Exists(bundled) ? bundled : "python";

        if (!File.Exists(Path.Combine(toolsDir, "station_fill.py")))
        {
            error = "Не найден tools\\station_fill.py рядом с программой — переустановите/обновите сборку.";
            return false;
        }
        Directory.CreateDirectory(workDir);
        error = string.Empty;
        return true;
    }

    private async Task RunStation(bool checkOnly)
    {
        if (!checkOnly && _repository is null)
        {
            StationHint.Text = "База не подключена.";
            return;
        }
        if (!TryReadStationFields(out var settings, out var fieldError))
        {
            StationHint.Text = fieldError;
            return;
        }
        if (!TryResolveRuntime(out var python, out var toolsDir, out var workDir, out var runtimeError))
        {
            StationHint.Text = runtimeError;
            return;
        }

        StationSave_Click(this, new RoutedEventArgs());
        var stationJson = Path.Combine(workDir, "station.json");
        File.WriteAllText(stationJson, StationJson(settings).ToJsonString(WriteOptions));

        var argv = new List<string> { "station_fill.py" };
        if (checkOnly)
        {
            argv.AddRange(["--check", "--station", stationJson]);
        }
        else
        {
            var dbPath = _repository!.Database.DatabasePath;
            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(AppContext.BaseDirectory, dbPath);
            argv.AddRange(["--db", dbPath, "--station", stationJson, "--work", workDir]);
            if (int.TryParse(StationLimit.Text?.Trim(), out var limit) && limit > 0)
                argv.AddRange(["--limit", limit.ToString(CultureInfo.InvariantCulture)]);
        }

        var env = new Dictionary<string, string>
        {
            ["PYTHONPATH"] = toolsDir,
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
        };

        _stationLog.Clear();
        StationLog.Text = string.Empty;
        StationLogWrap.Visibility = Visibility.Visible;
        StationProgress.Visibility = checkOnly ? Visibility.Collapsed : Visibility.Visible;
        StationHint.Text = checkOnly ? "Проверка соединения…" : "Перевод через станцию…";
        SetStationBusy(true);
        _stationCts = new CancellationTokenSource();

        try
        {
            var code = await StationProcess.RunAsync(
                python, argv, toolsDir, env,
                line => Dispatcher.Invoke(() => OnStationLine(line)),
                line => Dispatcher.Invoke(() => AppendStationLog(line)),
                _stationCts.Token);

            // Stop kills the child, which exits non-zero — that is a stop, not a failure. Rows are
            // written continuously, so whatever finished before the stop is already in the base.
            var stopped = _stationCts.IsCancellationRequested;
            if (checkOnly)
                StationHint.Text = code == 0 ? "Станция доступна ✓" : "Станция или модель недоступна — см. лог.";
            else if (stopped)
                StationHint.Text = "Остановлено. Переведённое сохранено — можно продолжить позже.";
            else
                StationHint.Text = code == 0 ? "Готово. Проверьте во вкладке «Ревью»." : "Завершено с ошибкой — см. лог.";
        }
        catch (OperationCanceledException)
        {
            StationHint.Text = "Остановлено.";
        }
        catch (Exception ex)
        {
            AppendStationLog($"Ошибка запуска: {ex.Message}");
            StationHint.Text = "Не удалось запустить — см. лог.";
        }
        finally
        {
            SetStationBusy(false);
            _stationCts?.Dispose();
            _stationCts = null;
            if (!checkOnly && _repository is not null)
                LoadDashboard();
        }
    }

    private void StationCheck_Click(object sender, RoutedEventArgs e) => _ = RunStation(checkOnly: true);
    private void StationRun_Click(object sender, RoutedEventArgs e) => _ = RunStation(checkOnly: false);
    private void StationStop_Click(object sender, RoutedEventArgs e) => _stationCts?.Cancel();

    private void SetStationBusy(bool busy)
    {
        StationRunBtn.IsEnabled = !busy;
        StationCheckBtn.IsEnabled = !busy;
        StationStop.IsEnabled = busy;
    }

    private void OnStationLine(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0] == "PROGRESS"
            && int.TryParse(parts[1], out var done) && int.TryParse(parts[2], out var total))
        {
            var fraction = total > 0 ? Math.Clamp((double)done / total, 0, 1) : 0.0;
            StationFill.Width = new GridLength(fraction, GridUnitType.Star);
            StationRest.Width = new GridLength(1 - fraction, GridUnitType.Star);
            StationPct.Text = fraction.ToString("P0", CultureInfo.CurrentCulture);
            StationStat.Text = $"{done:N0} / {total:N0}";
            return;
        }
        AppendStationLog(line);
    }

    private void AppendStationLog(string line)
    {
        _stationLog.Add(line);
        if (_stationLog.Count > 200)
            _stationLog.RemoveAt(0);
        StationLog.Text = string.Join("\n", _stationLog);
        StationLogScroll.ScrollToEnd();
    }
}
