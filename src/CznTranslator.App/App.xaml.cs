using System.IO;
using System.Windows;
using System.Windows.Threading;
using CznTranslator.Capture;
using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Config;
using CznTranslator.Core.Metrics;
using CznTranslator.Lookup;
using CznTranslator.Ocr;
using CznTranslator.Overlay;
using CznTranslator.Sync;
using Serilog;
using Serilog.Events;

namespace CznTranslator.App;

/// <summary>
/// Startup, teardown, and the tray. The order here follows §4 and §11: models are warmed before
/// the overlay appears, so the first translation of a session is not mistaken for a hang.
/// </summary>
public partial class App : Application
{
    private ConfigService? _configService;
    private TranslationPipeline? _pipeline;
    private OverlayWindow? _overlayWindow;
    private OverlayRenderer? _renderer;
    private TargetWindowTracker? _tracker;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private MetricsCollector? _metrics;
    private IMetricsStore? _metricsStore;
    private DispatcherTimer? _housekeeping;
    private CancellationTokenSource? _shutdown;

    private bool _overlayEnabled = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _shutdown = new CancellationTokenSource();

        try
        {
            await StartAsync(_shutdown.Token);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed.");
            MessageBox.Show(
                $"Не удалось запустить переводчик:\n\n{ex.Message}",
                "CZN Translator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);
        var config = _configService.Current;

        ConfigureLogging(config.Logging);
        Log.Information("CZN Translator starting with profile '{Profile}'.", config.Profile);

        var databasePath = Path.Combine(AppContext.BaseDirectory, config.Lookup.DatabasePath);
        var database = new TranslationDatabase(databasePath);
        database.EnsureCreated();

        _metrics = new MetricsCollector();
        _metricsStore = new SqliteMetricsStore(database);
        _metrics.DayRolled += async (_, snapshot) => await _metricsStore.UpsertAsync(snapshot);

        var fallback = new OllamaFallback(config.Sync);
        var lookup = new SqliteLookupService(database, config.Lookup, fallback, _metrics);

        await CheckPackVersionAsync(config, database, cancellationToken);

        // Frame source first: on the WGC path its D3D device is what the ROI extractor binds to.
        var (frameSource, roiExtractor, targetWindow) = await CreateFrameSourceAsync(config, cancellationToken);

        var adapterProvider = OperatingSystem.IsWindows()
            ? new DxgiAdapterProvider()
            : (IGraphicsAdapterProvider)new EmptyAdapterProvider();

        var ocr = new OcrBackendFactory(adapterProvider).Create(config);

        // Warm-up before anything is shown. The first DirectML inference compiles shaders and
        // takes 1–3 s; paying that here rather than on the first real screen is the whole point.
        await ocr.WarmUpAsync(cancellationToken);

        _pipeline = new TranslationPipeline(config, frameSource, roiExtractor, ocr, lookup, _metrics);

        _overlayWindow = new OverlayWindow();
        _renderer = new OverlayRenderer(_overlayWindow.Handle, config.Overlay);
        _pipeline.ResultsChanged += (_, _) => Dispatcher.BeginInvoke(Redraw);

        if (targetWindow is not null)
        {
            _tracker = new TargetWindowTracker(targetWindow.Handle, targetWindow.ProcessId);
            _tracker.Changed += (_, state) => Dispatcher.BeginInvoke(() => OnTargetWindowChanged(state));
            OnTargetWindowChanged(_tracker.Current);
        }
        else
        {
            // Offline stand: there is no game window to follow, so the overlay sits on the
            // primary display and draws over the replayed frame.
            _overlayWindow.SetBounds(new Core.Models.PixelRect(
                0, 0,
                Math.Max(1, frameSource.SurfaceSize.Width),
                Math.Max(1, frameSource.SurfaceSize.Height)));
            _overlayWindow.Show();
        }

        _hotkeys = new HotkeyManager();
        _hotkeys.Pressed += async (_, action) => await OnHotkeyAsync(action);

        _tray = new TrayIcon(ocr.Info.ToString());
        _tray.ExitRequested += (_, _) => Shutdown();
        _tray.ToggleRequested += (_, _) => ToggleOverlay();

        _configService.Changed += (_, args) => Dispatcher.BeginInvoke(() => OnConfigChanged(args));

        // WinEvents can be dropped under load and a monitor DPI change raises none at all, so a
        // slow poll backs up the event-driven path rather than replacing it.
        _housekeeping = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _housekeeping.Tick += (_, _) => Housekeeping();
        _housekeeping.Start();

        await _pipeline.StartAsync(cancellationToken);
        Log.Information("Pipeline running on {Backend}.", ocr.Info);
    }

    private async Task<(IFrameSource Source, GpuRoiExtractor? Extractor, GameWindow? Window)> CreateFrameSourceAsync(
        AppConfig config,
        CancellationToken cancellationToken)
    {
        if (string.Equals(config.Capture.Source, "folder", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information("Using the offline frame folder {Folder}.", config.Capture.Folder);
            return (new FolderFrameSource(config.Capture), null, null);
        }

        var window = await new WindowFinder().WaitForAsync(config.Capture, cancellationToken);
        var source = new WgcFrameSource(window.Handle);

        source.Faulted += (_, fault) => Dispatcher.BeginInvoke(() => OnCaptureFault(fault));
        await source.StartAsync(cancellationToken);

        return (source, new GpuRoiExtractor(source.Device), window);
    }

    private async Task CheckPackVersionAsync(AppConfig config, TranslationDatabase database, CancellationToken cancellationToken)
    {
        var watcher = new PackWatcher(new SqlitePackVersionStore(database));
        var result = await watcher.CheckAsync(config.Sync.PackPath, config.Capture.ProcessName, cancellationToken);

        Log.Information("Pack check: {Outcome} — {Message}", result.Outcome, result.Message);

        // Detected, reported, and left alone: §7 is explicit that nothing rebuilds by itself.
        if (result.Outcome == PackCheckOutcome.PatchDetected)
        {
            MessageBox.Show(
                result.Message + "\n\nЗапустите tools/diff_pack.py при закрытой игре, чтобы обновить базу.",
                "CZN Translator — вышел патч",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void OnCaptureFault(CaptureFault fault)
    {
        Log.Warning("Capture fault: {Kind} — {Message}", fault.Kind, fault.Message);
        _tray?.ShowMessage("CZN Translator", fault.Message);

        if (fault.Kind == CaptureFaultKind.ExclusiveFullscreen)
            _overlayWindow?.Hide();
    }

    private void OnTargetWindowChanged(TrackedWindowState state)
    {
        if (_overlayWindow is null)
            return;

        _overlayWindow.SetBounds(state.ClientBoundsOnScreen);

        // The overlay follows the game's visibility: it must not float over the desktop or over
        // whatever the player alt-tabbed to.
        if (_overlayEnabled && state.IsForeground && !state.IsMinimized)
        {
            _overlayWindow.Show();
            Redraw();
        }
        else
        {
            _overlayWindow.Hide();
        }
    }

    private void Housekeeping()
    {
        _tracker?.Poll();

        if (_metrics is null || _tray is null)
            return;

        var snapshot = _metrics.Snapshot();
        _tray.UpdateStatus(
            snapshot.Coverage,
            _metrics.Stage(PipelineStage.ChangeToDraw).Percentile(0.95));
    }

    private void Redraw()
    {
        if (_renderer is null || _overlayWindow is null || _pipeline is null || !_overlayWindow.IsVisible)
            return;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _renderer.Draw(
            _overlayWindow.Bounds.Width,
            _overlayWindow.Bounds.Height,
            _pipeline.Results,
            _pipeline.ZoneOrigins);

        _metrics?.RecordStage(PipelineStage.Draw, stopwatch.Elapsed.TotalMilliseconds);
    }

    private void OnConfigChanged(ConfigChangedEventArgs args)
    {
        _pipeline?.ApplyConfig(args.Config);
        _renderer?.ApplySettings(args.Config.Overlay);
        Redraw();

        if (args.RestartRequired.Count > 0)
        {
            _tray?.ShowMessage(
                "CZN Translator",
                "Применено. Требуют перезапуска: " + string.Join(", ", args.RestartRequired));
        }
    }

    private async Task OnHotkeyAsync(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleOverlay:
                ToggleOverlay();
                break;

            case HotkeyAction.ClearZoneCache:
                if (_pipeline is not null)
                    await _pipeline.ClearCachesAsync();
                _tray?.ShowMessage("CZN Translator", "Кэш экранов сброшен.");
                break;

            case HotkeyAction.ToggleDebug:
                if (_configService is not null)
                {
                    var overlay = _configService.Current.Overlay;
                    overlay.Debug = !overlay.Debug;
                    _renderer?.ApplySettings(overlay);
                    Dispatcher.BeginInvoke(Redraw);
                }
                break;

            case HotkeyAction.TranslateSelection:
                // Zones in manual mode are the scrolling lists §11 keeps out of the poll loop.
                foreach (var zone in _configService?.Current.Zones ?? [])
                    _pipeline?.RequestManualZone(zone.Id);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unhandled hotkey.");
        }
    }

    private void ToggleOverlay()
    {
        _overlayEnabled = !_overlayEnabled;

        if (_overlayEnabled)
        {
            _overlayWindow?.Show();
            Redraw();
        }
        else
        {
            _overlayWindow?.Hide();
        }

        _tray?.SetEnabled(_overlayEnabled);
    }

    private static void ConfigureLogging(LoggingSection settings)
    {
        var level = Enum.TryParse<LogEventLevel>(settings.Level, ignoreCase: true, out var parsed)
            ? parsed
            : LogEventLevel.Information;

        var directory = Path.IsPathRooted(settings.Directory)
            ? settings.Directory
            : Path.Combine(AppContext.BaseDirectory, settings.Directory);

        Directory.CreateDirectory(directory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(directory, "czn-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _housekeeping?.Stop();
        _shutdown?.Cancel();

        if (_metrics is not null && _metricsStore is not null)
            await _metricsStore.UpsertAsync(_metrics.Snapshot());

        _hotkeys?.Dispose();
        _tray?.Dispose();
        _tracker?.Dispose();
        _renderer?.Dispose();
        _overlayWindow?.Dispose();

        if (_pipeline is not null)
            await _pipeline.DisposeAsync();

        _configService?.Dispose();

        Log.Information("CZN Translator stopped.");
        await Log.CloseAndFlushAsync();

        base.OnExit(e);
    }
}
