using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CznTranslator.Core.Config;
using Serilog;

namespace CznTranslator.Capture;

public sealed record GameWindow(nint Handle, int ProcessId, string ClassName, string Title);

/// <summary>
/// Finds the game window by process name and window class (TZ §2).
/// <para>
/// Everything here is window-manager level: enumerate top-level windows, read their class and
/// their owning process id. No handle to the game process is ever opened — no <c>OpenProcess</c>,
/// no module enumeration — because that is precisely the contact §0 rules out.
/// </para>
/// </summary>
public sealed class WindowFinder(ILogger? log = null)
{
    private readonly ILogger _log = log ?? Log.Logger;

    public GameWindow? Find(CaptureSection settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var processIds = Process.GetProcessesByName(settings.ProcessName)
            .Select(process =>
            {
                try
                {
                    return process.Id;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .ToHashSet();

        if (processIds.Count == 0)
            return null;

        GameWindow? found = null;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (!processIds.Contains((int)processId))
                return true;

            var className = GetClassName(handle);
            if (!string.IsNullOrEmpty(settings.WindowClass) &&
                !string.Equals(className, settings.WindowClass, StringComparison.Ordinal))
            {
                return true;
            }

            // Unity spawns helper windows (splash, IME) in the same process; the real one has a
            // client area, so anything degenerate is skipped rather than captured as black.
            if (!GetClientRect(handle, out var client) || client.Right < 320 || client.Bottom < 240)
                return true;

            found = new GameWindow(handle, (int)processId, className, GetWindowTitle(handle));
            return false;
        }, nint.Zero);

        return found;
    }

    /// <summary>Polls until the window shows up. The interval comes from the config (2 s by default).</summary>
    public async Task<GameWindow> WaitForAsync(CaptureSection settings, CancellationToken cancellationToken)
    {
        var announced = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var window = Find(settings);
            if (window is not null)
            {
                _log.Information(
                    "Found the game window: {Class} '{Title}' (pid {Pid}).",
                    window.ClassName, window.Title, window.ProcessId);
                return window;
            }

            if (!announced)
            {
                _log.Information("Waiting for {Process} to start…", settings.ProcessName);
                announced = true;
            }

            await Task.Delay(settings.WindowSearchIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }

    private static string GetClassName(nint handle)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    private static string GetWindowTitle(nint handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetWindowText(handle, buffer, buffer.Capacity);
        return length > 0 ? buffer.ToString(0, length) : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(nint handle, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint handle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint handle, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(nint handle, ref System.Drawing.Point point);
}
