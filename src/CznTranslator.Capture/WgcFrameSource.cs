using System.Runtime.InteropServices;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using Serilog;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace CznTranslator.Capture;

/// <summary>
/// Windows Graphics Capture over the game window (TZ §2).
/// <para>
/// This is the same public API OBS and Discord use to share a window. It reads the desktop
/// composition, not the game: no process handle, no swapchain hook, nothing injected. That is
/// what makes it acceptable next to the anti-cheat, and it is why the capture path must never be
/// "optimised" into anything that touches the game directly.
/// </para>
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    private readonly nint _windowHandle;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private ID3D11Device? _device;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private int _blackFrameStreak;
    private bool _exclusiveFullscreenReported;

    /// <summary>Consecutive fully-black frames before exclusive fullscreen is called out.</summary>
    private const int BlackFrameThreshold = 30;

    public WgcFrameSource(nint windowHandle, ILogger? log = null)
    {
        if (windowHandle == nint.Zero)
            throw new ArgumentException("Window handle must be non-zero.", nameof(windowHandle));

        _windowHandle = windowHandle;
        _log = log ?? Log.Logger;
    }

    public (int Width, int Height) SurfaceSize { get; private set; }

    public bool IsRunning { get; private set; }

    public event EventHandler<CapturedFrame>? FrameArrived;
    public event EventHandler<CaptureFault>? Faulted;

    /// <summary>The D3D11 device the frames live on — <see cref="GpuRoiExtractor"/> needs it.</summary>
    public ID3D11Device Device => _device ?? throw new InvalidOperationException("Capture has not been started.");

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (IsRunning)
                return Task.CompletedTask;

            if (!GraphicsCaptureSession.IsSupported())
            {
                Faulted?.Invoke(this, new CaptureFault(
                    CaptureFaultKind.SourceUnavailable,
                    "Windows Graphics Capture is not available on this system (Windows 10 1903+ required)."));
                return Task.CompletedTask;
            }

            _device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0]);

            _winrtDevice = CreateWinRtDevice(_device);

            // Programmatic item creation: the system picker would ask the user to choose a window
            // on every start, which is unusable for something that runs alongside a game.
            _item = CreateItemForWindow(_windowHandle);
            _item.Closed += OnItemClosed;

            SurfaceSize = (_item.Size.Width, _item.Size.Height);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                _item.Size);

            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            TryDisableCaptureBorder(_session);

            _session.StartCapture();
            IsRunning = true;
        }

        _log.Information("WGC capture started at {Width}×{Height}.", SurfaceSize.Width, SurfaceSize.Height);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The yellow capture border can only be turned off on Windows 11 22H2 and later, so the
    /// property has to be probed rather than assumed — setting it blind throws on older builds.
    /// </summary>
    private void TryDisableCaptureBorder(GraphicsCaptureSession session)
    {
        const string typeName = "Windows.Graphics.Capture.GraphicsCaptureSession";

        if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(typeName, "IsBorderRequired"))
        {
            session.IsBorderRequired = false;
        }
        else
        {
            _log.Information(
                "IsBorderRequired is unavailable on this Windows build, so the yellow capture " +
                "border will stay visible. It does not affect recognition.");
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
            return;

        if (frame.ContentSize.Width != SurfaceSize.Width || frame.ContentSize.Height != SurfaceSize.Height)
        {
            // The window was resized; the pool has to be rebuilt at the new size or every
            // subsequent frame is letterboxed against the old one.
            SurfaceSize = (frame.ContentSize.Width, frame.ContentSize.Height);
            sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
            return;
        }

        using var texture = GetTexture(frame.Surface);
        DetectExclusiveFullscreen(texture);

        FrameArrived?.Invoke(this, new CapturedFrame(
            SurfaceSize.Width,
            SurfaceSize.Height,
            texture,
            Cpu: null,
            frame.SystemRelativeTime.Ticks));
    }

    /// <summary>
    /// In exclusive fullscreen WGC hands back black frames — documented behaviour, not a bug in
    /// the app. Reporting it explicitly matters because the symptom otherwise looks like OCR
    /// finding no text, and the user would go hunting in the wrong place (TZ §2, §11).
    /// </summary>
    private void DetectExclusiveFullscreen(ID3D11Texture2D texture)
    {
        if (_exclusiveFullscreenReported)
            return;

        if (IsProbablyBlack(texture))
        {
            if (++_blackFrameStreak < BlackFrameThreshold)
                return;

            _exclusiveFullscreenReported = true;
            Faulted?.Invoke(this, new CaptureFault(
                CaptureFaultKind.ExclusiveFullscreen,
                "The capture is returning black frames, which is what Windows Graphics Capture " +
                "does for a game in exclusive fullscreen. Switch the game to borderless windowed."));
        }
        else
        {
            _blackFrameStreak = 0;
        }
    }

    /// <summary>
    /// Samples a handful of texels rather than the whole frame — this runs on every frame, and a
    /// full readback here would undo the entire point of keeping frames on the GPU.
    /// </summary>
    private bool IsProbablyBlack(ID3D11Texture2D texture)
    {
        var description = texture.Description;
        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        description.Width = 8;
        description.Height = 8;

        using var probe = _device!.CreateTexture2D(description);

        _device.ImmediateContext.CopySubresourceRegion(
            probe, 0, 0, 0, 0,
            texture, 0,
            new Box(0, 0, 0, 8, 8, 1));

        var mapped = _device.ImmediateContext.Map(probe, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            unsafe
            {
                var row = (byte*)mapped.DataPointer;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8 * 4; x++)
                    {
                        if (row[x] > 8)
                            return false;
                    }

                    row += mapped.RowPitch;
                }
            }
        }
        finally
        {
            _device.ImmediateContext.Unmap(probe, 0);
        }

        return true;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        IsRunning = false;
        Faulted?.Invoke(this, new CaptureFault(
            CaptureFaultKind.GameNotRunning,
            "The captured window was closed."));
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            IsRunning = false;

            if (_framePool is not null)
                _framePool.FrameArrived -= OnFrameArrived;
            if (_item is not null)
                _item.Closed -= OnItemClosed;

            _session?.Dispose();
            _framePool?.Dispose();
            _session = null;
            _framePool = null;
            _item = null;
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        _winrtDevice?.Dispose();
        _device?.Dispose();
        _device = null;
        _winrtDevice = null;
    }

    // ------------------------------------------------------------------ interop

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    // The default interface of GraphicsCaptureItem — CreateForWindow QIs the new item for it.
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint activatableClassId, [In] ref Guid iid, out nint factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        // Ask WinRT for the GraphicsCaptureItem activation factory as the classic interop interface
        // directly. Going through CsWinRT's As<>() on a [ComImport] interface builds a wrapper whose
        // vtable does not line up, and the first call throws InvalidCastException — which is exactly
        // what killed the pipeline before it drew anything.
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out var classId));
        try
        {
            var interopIid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interopIid, out var factoryPtr));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var iid = GraphicsCaptureItemIid;
                var pointer = interop.CreateForWindow(hwnd, ref iid);
                try
                {
                    return GraphicsCaptureItem.FromAbi(pointer);
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    private static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();

        Marshal.ThrowExceptionForHR(
            CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var abi));

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var pointer = access.GetInterface(ref iid);

        return new ID3D11Texture2D(pointer);
    }
}
