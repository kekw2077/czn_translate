using System.Runtime.InteropServices;
using CznTranslator.Core.Models;
using Serilog;

namespace CznTranslator.Overlay;

/// <summary>
/// The bare native window the overlay draws into (TZ §6).
/// <para>
/// The extended style set is the whole design in one line:
/// <c>WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
/// WS_EX_NOREDIRECTIONBITMAP</c>. TRANSPARENT and NOACTIVATE together make it completely
/// click-through and unfocusable, so it intercepts no input at all; NOREDIRECTIONBITMAP is what
/// lets DirectComposition present without a redirection surface, which is the difference between
/// this and the UpdateLayeredWindow approach the TZ rules out.
/// </para>
/// </summary>
public sealed class OverlayWindow : IDisposable
{
    private const string ClassName = "CznTranslatorOverlay";

    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;
    private const int WsExTopMost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExNoRedirectionBitmap = 0x00200000;

    private const int WsPopup = unchecked((int)0x80000000);

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    private static readonly nint HwndTopMost = -1;

    private readonly WndProcDelegate _wndProc;
    private readonly ILogger _log;
    private ushort _classAtom;
    private bool _visible;

    public OverlayWindow(ILogger? log = null)
    {
        _log = log ?? Log.Logger;
        _wndProc = StaticWndProc;

        var wndClass = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName
        };

        _classAtom = RegisterClassEx(ref wndClass);
        if (_classAtom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            // 1410 is "class already exists", which happens on a restart within one process.
            if (error != 1410)
                throw new InvalidOperationException($"RegisterClassEx failed with {error}.");
        }

        Handle = CreateWindowEx(
            WsExLayered | WsExTransparent | WsExTopMost | WsExToolWindow | WsExNoActivate | WsExNoRedirectionBitmap,
            ClassName,
            "CZN Translator overlay",
            WsPopup,
            0, 0, 16, 16,
            nint.Zero, nint.Zero, GetModuleHandle(null), nint.Zero);

        if (Handle == nint.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed with {Marshal.GetLastWin32Error()}.");

        _log.Debug("Overlay window created (hwnd {Handle}).", Handle);
    }

    public nint Handle { get; private set; }

    public PixelRect Bounds { get; private set; }

    /// <summary>Moves the overlay onto the tracked window's client area, in screen coordinates.</summary>
    public void SetBounds(PixelRect bounds)
    {
        if (bounds == Bounds || Handle == nint.Zero)
            return;

        Bounds = bounds;

        SetWindowPos(
            Handle, HwndTopMost,
            bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SwpNoActivate | (_visible ? SwpShowWindow : 0));
    }

    public void Show()
    {
        if (_visible || Handle == nint.Zero)
            return;

        _visible = true;

        // SW_SHOWNOACTIVATE, never SW_SHOW: showing the overlay must not pull focus off the game.
        ShowWindow(Handle, SwShowNoActivate);
        SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0, SwpNoActivate | SwpNoZOrder | 0x0001 | 0x0002);
    }

    public void Hide()
    {
        if (!_visible || Handle == nint.Zero)
            return;

        _visible = false;
        ShowWindow(Handle, SwHide);
    }

    public bool IsVisible => _visible;

    private static nint StaticWndProc(nint hwnd, uint message, nint wParam, nint lParam) =>
        DefWindowProc(hwnd, message, wParam, lParam);

    public void Dispose()
    {
        if (Handle != nint.Zero)
        {
            DestroyWindow(Handle);
            Handle = nint.Zero;
        }

        if (_classAtom != 0)
        {
            UnregisterClass(ClassName, GetModuleHandle(null));
            _classAtom = 0;
        }
    }

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
