using CznTranslator.Core.Models;

namespace CznTranslator.Core.Abstractions;

/// <summary>
/// A captured frame. <see cref="GpuTexture"/> is the live path: the frame stays on the GPU and
/// only cropped ROIs are read back (TZ §2). <see cref="Cpu"/> is populated by the folder-based
/// offline stand, where there is no GPU surface to begin with.
/// </summary>
public sealed record CapturedFrame(
    int Width,
    int Height,
    object? GpuTexture,
    GrayImage? Cpu,
    long TimestampTicks)
{
    public bool IsOnGpu => GpuTexture is not null;
}

/// <summary>
/// Frame producer. Two implementations per TZ §12: <c>WgcFrameSource</c> (live, Windows-only)
/// and <c>FolderFrameSource</c> (PNGs on a timer, runs anywhere).
/// </summary>
public interface IFrameSource : IAsyncDisposable
{
    /// <summary>Size of the source surface in pixels; zero until the first frame arrives.</summary>
    (int Width, int Height) SurfaceSize { get; }

    /// <summary>True once frames are actually flowing.</summary>
    bool IsRunning { get; }

    event EventHandler<CapturedFrame>? FrameArrived;

    /// <summary>
    /// Raised when capture cannot proceed for a reason the user has to fix — most commonly
    /// exclusive fullscreen, where WGC hands back black frames (TZ §2, §11).
    /// </summary>
    event EventHandler<CaptureFault>? Faulted;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}

public enum CaptureFaultKind
{
    GameNotRunning,
    ExclusiveFullscreen,
    DeviceLost,
    AccessDenied,
    SourceUnavailable
}

public sealed record CaptureFault(CaptureFaultKind Kind, string Message, Exception? Exception = null);
