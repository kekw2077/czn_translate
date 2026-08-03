using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace CznTranslator.App;

public enum HotkeyAction
{
    ToggleOverlay,
    TranslateSelection,
    ClearZoneCache,
    ToggleDebug
}

/// <summary>
/// Global hotkeys through <c>RegisterHotKey</c> (TZ §6).
/// <para>
/// This is the sanctioned way to claim a key combination system-wide: the shell delivers
/// <c>WM_HOTKEY</c> to a specific window and the application sees nothing else. It is deliberately
/// not <c>SetWindowsHookEx</c>, which would observe every keystroke on the machine and read to an
/// anti-cheat exactly like a keylogger (§0).
/// </para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private static readonly (HotkeyAction Action, uint Modifiers, uint Key, string Label)[] Bindings =
    [
        (HotkeyAction.ToggleOverlay, ModControl | ModAlt, 'T', "Ctrl+Alt+T"),
        (HotkeyAction.TranslateSelection, ModControl | ModAlt, 'A', "Ctrl+Alt+A"),
        (HotkeyAction.ClearZoneCache, ModControl | ModAlt, 'R', "Ctrl+Alt+R"),
        (HotkeyAction.ToggleDebug, ModControl | ModAlt, 'D', "Ctrl+Alt+D")
    ];

    private readonly HwndSource _source;
    private readonly ILogger _log;
    private readonly List<int> _registered = [];

    public HotkeyManager(ILogger? log = null)
    {
        _log = log ?? Log.Logger;

        // A message-only window: it never displays, it only receives WM_HOTKEY.
        _source = new HwndSource(new HwndSourceParameters("CznTranslatorHotkeys")
        {
            ParentWindow = -3, // HWND_MESSAGE
            Width = 0,
            Height = 0
        });

        _source.AddHook(WndProc);

        foreach (var (action, modifiers, key, label) in Bindings)
        {
            var id = (int)action;

            // MOD_NOREPEAT: holding the combination must fire once, not sixty times a second.
            if (RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, key))
            {
                _registered.Add(id);
            }
            else
            {
                _log.Warning(
                    "Could not register {Label} for {Action} — another application already owns it.",
                    label, action);
            }
        }
    }

    public event EventHandler<HotkeyAction>? Pressed;

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmHotkey)
            return nint.Zero;

        var id = wParam.ToInt32();
        if (!_registered.Contains(id))
            return nint.Zero;

        handled = true;
        Pressed?.Invoke(this, (HotkeyAction)id);
        return nint.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered)
            UnregisterHotKey(_source.Handle, id);

        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
