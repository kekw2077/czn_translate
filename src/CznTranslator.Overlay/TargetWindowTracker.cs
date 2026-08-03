using System.Runtime.InteropServices;
using CznTranslator.Core.Models;
using Serilog;

namespace CznTranslator.Overlay;

public sealed record TrackedWindowState(PixelRect ClientBoundsOnScreen, bool IsForeground, bool IsMinimized);

/// <summary>
/// Keeps the overlay glued to the game window (TZ §6).
/// <para>
/// <c>SetWinEventHook</c> is scoped to the game's own process and thread and runs out-of-context,
/// so this is a subscription to events about one window — not a <c>SetWindowsHookEx</c> global
/// hook, nothing is injected anywhere, and nothing about it resembles a keylogger. That
/// distinction is the reason §0 forbids one API and §6 permits the other.
/// </para>
/// </summary>
public sealed class TargetWindowTracker : IDisposable
{
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;

    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;

    private readonly nint _targetWindow;
    private readonly ILogger _log;
    private readonly WinEventDelegate _callback;

    private nint _locationHook;
    private nint _foregroundHook;

    public TargetWindowTracker(nint targetWindow, int targetProcessId, ILogger? log = null)
    {
        _targetWindow = targetWindow;
        _log = log ?? Log.Logger;
        _callback = OnWinEvent;

        // Scoped to the game's process. A zero process id here would subscribe system-wide, which
        // is both wasteful and exactly the kind of breadth worth avoiding next to an anti-cheat.
        _locationHook = SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange,
            nint.Zero, _callback,
            (uint)targetProcessId, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        _foregroundHook = SetWinEventHook(
            EventSystemForeground, EventSystemMinimizeEnd,
            nint.Zero, _callback,
            0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);

        if (_locationHook == nint.Zero)
            _log.Warning("SetWinEventHook for location changes failed; the overlay will only follow on the poll.");

        Current = Read();
    }

    public TrackedWindowState Current { get; private set; }

    public event EventHandler<TrackedWindowState>? Changed;

    /// <summary>
    /// Re-reads the window state. WinEvents can be missed under load, and a DPI change on the
    /// monitor does not raise a location event at all, so the app also calls this on a slow timer.
    /// </summary>
    public TrackedWindowState Poll()
    {
        var state = Read();
        if (state == Current)
            return state;

        Current = state;
        Changed?.Invoke(this, state);
        return state;
    }

    private TrackedWindowState Read()
    {
        var minimized = IsIconic(_targetWindow);
        var foreground = GetForegroundWindow() == _targetWindow;

        if (!GetClientRect(_targetWindow, out var client))
            return new TrackedWindowState(new PixelRect(0, 0, 1, 1), foreground, minimized);

        // Client rect is window-relative; the overlay needs screen coordinates, and the client
        // area is what matters — the title bar is not part of the game's picture.
        var origin = new Point { X = 0, Y = 0 };
        ClientToScreen(_targetWindow, ref origin);

        var bounds = new PixelRect(
            origin.X,
            origin.Y,
            Math.Max(1, client.Right - client.Left),
            Math.Max(1, client.Bottom - client.Top));

        return new TrackedWindowState(bounds, foreground, minimized);
    }

    private void OnWinEvent(
        nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint threadId, uint timestamp)
    {
        // OBJID_WINDOW only: the game raises location events for child objects too, and reacting
        // to those would re-measure the overlay dozens of times per second for nothing.
        if (eventType == EventObjectLocationChange && (hwnd != _targetWindow || objectId != 0))
            return;

        Poll();
    }

    public void Dispose()
    {
        if (_locationHook != nint.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = nint.Zero;
        }

        if (_foregroundHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }
    }

    private delegate void WinEventDelegate(
        nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint threadId, uint timestamp);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint moduleHandle,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hwnd, ref Point point);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
