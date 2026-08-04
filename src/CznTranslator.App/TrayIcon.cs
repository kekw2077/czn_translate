using System.Drawing;
using System.Windows.Forms;

namespace CznTranslator.App;

/// <summary>
/// Tray presence and the live status readout (TZ §4, §9): which OCR backend was selected, and the
/// two numbers that say whether the thing is working — coverage and p95 latency.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly string _backend;

    public TrayIcon(string backendDescription)
    {
        _backend = backendDescription;

        _statusItem = new ToolStripMenuItem("Ожидание данных…") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Оверлей включён");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new ToolStripMenuItem("Настройки…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var backendItem = new ToolStripMenuItem(backendDescription) { Enabled = false };
        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(backendItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = $"CZN Translator — {backendDescription}",
            ContextMenuStrip = menu
        };
    }

    public event EventHandler? ExitRequested;
    public event EventHandler? ToggleRequested;
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Coverage is the number that matters (§9): a drop below 90% after a patch means the base
    /// went stale, which is otherwise invisible — translations simply stop appearing.
    /// </summary>
    public void UpdateStatus(double coverage, double p95LatencyMs)
    {
        var latency = double.IsNaN(p95LatencyMs) ? "—" : $"{p95LatencyMs:F0} мс";
        _statusItem.Text = $"Покрытие {coverage:P0} · p95 {latency}";

        // The tooltip is capped at 63 characters by the shell; anything longer is silently dropped.
        var tooltip = $"CZN · {coverage:P0} · {latency} · {_backend}";
        _icon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    public void SetEnabled(bool enabled) =>
        _toggleItem.Text = enabled ? "Оверлей включён" : "Оверлей выключен";

    public void ShowMessage(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
