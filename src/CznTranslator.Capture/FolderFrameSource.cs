using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Models;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CznTranslator.Capture;

/// <summary>
/// Replays PNGs from a folder as if they were captured frames (TZ §12).
/// <para>
/// This is what makes everything except capture itself developable on a machine that does not
/// have the game — and, more usefully day to day, it turns <c>testdata/frames</c> into a
/// regression set: the same frames through the same pipeline, compared against their
/// <c>.expected.json</c> files, so a change to normalization or a search threshold shows
/// immediately what it broke.
/// </para>
/// </summary>
public sealed class FolderFrameSource : IFrameSource
{
    private readonly string _folder;
    private readonly double _fps;
    private readonly bool _loop;
    private readonly ILogger _log;
    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _pump;
    private string[] _files = [];
    private int _index;

    public FolderFrameSource(CaptureSection settings, ILogger? log = null, bool loop = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _folder = settings.Folder;
        _fps = settings.Fps > 0 ? settings.Fps : 2;
        _loop = loop;
        _log = log ?? Log.Logger;
    }

    public (int Width, int Height) SurfaceSize { get; private set; }

    public bool IsRunning { get; private set; }

    public event EventHandler<CapturedFrame>? FrameArrived;
    public event EventHandler<CaptureFault>? Faulted;

    /// <summary>Frame files in play order — the harness uses this to line results up with expectations.</summary>
    public IReadOnlyList<string> Files => _files;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (IsRunning)
                return Task.CompletedTask;

            if (!Directory.Exists(_folder))
            {
                Faulted?.Invoke(this, new CaptureFault(
                    CaptureFaultKind.SourceUnavailable,
                    $"Frame folder '{_folder}' does not exist."));
                return Task.CompletedTask;
            }

            _files = Directory.GetFiles(_folder, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (_files.Length == 0)
            {
                Faulted?.Invoke(this, new CaptureFault(
                    CaptureFaultKind.SourceUnavailable,
                    $"Frame folder '{_folder}' holds no PNG files."));
                return Task.CompletedTask;
            }

            _index = 0;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            IsRunning = true;
            _pump = Task.Run(() => PumpAsync(_cancellation.Token), _cancellation.Token);
        }

        _log.Information("Folder capture started: {Count} frame(s) from {Folder} at {Fps} fps.", _files.Length, _folder, _fps);
        return Task.CompletedTask;
    }

    /// <summary>Pushes exactly one frame, for the hotkey-driven manual runs the laptop profile uses.</summary>
    public bool PushNext()
    {
        if (_files.Length == 0)
            _files = Directory.GetFiles(_folder, "*.png").OrderBy(path => path, StringComparer.Ordinal).ToArray();

        if (_files.Length == 0)
            return false;

        if (_index >= _files.Length)
        {
            if (!_loop)
                return false;
            _index = 0;
        }

        Emit(_files[_index++]);
        return true;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(1.0 / _fps);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!PushNext())
                    break;

                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            IsRunning = false;
        }
    }

    private void Emit(string path)
    {
        try
        {
            var image = LoadGrayscale(path);
            SurfaceSize = (image.Width, image.Height);

            // No GPU texture here: the offline stand has no capture device, so the frame arrives
            // already on the CPU and the ROI crop falls back to GrayImage.Crop.
            FrameArrived?.Invoke(this, new CapturedFrame(
                image.Width,
                image.Height,
                GpuTexture: null,
                Cpu: image,
                DateTimeOffset.UtcNow.Ticks));
        }
        catch (Exception ex) when (ex is IOException or UnknownImageFormatException or InvalidImageContentException)
        {
            _log.Warning(ex, "Skipping unreadable frame {Path}.", path);
        }
    }

    /// <summary>
    /// Converts with the same Rec. 601 weighting the GPU shader uses, so a frame replayed here
    /// produces the same pHash and the same OCR input it would during a live capture.
    /// </summary>
    public static GrayImage LoadGrayscale(string path)
    {
        using var image = Image.Load<Rgba32>(path);

        var pixels = new byte[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    pixels[y * accessor.Width + x] =
                        (byte)(0.299 * pixel.R + 0.587 * pixel.G + 0.114 * pixel.B + 0.5);
                }
            }
        });

        return new GrayImage(image.Width, image.Height, pixels);
    }

    public Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            IsRunning = false;
            cancellation = _cancellation;
            _cancellation = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
    }
}
