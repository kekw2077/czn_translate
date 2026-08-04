using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
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

// Both UseWPF and UseWindowsForms are on (the tray needs NotifyIcon), so the unqualified name is
// ambiguous. Every dialog here is WPF's — MessageBoxButton/MessageBoxImage are WPF-only types.
using MessageBox = System.Windows.MessageBox;

namespace CznTranslator.App;

/// <summary>
/// Startup, teardown, and the tray. The order here follows §4 and §11: models are warmed before
/// the overlay appears, so the first translation of a session is not mistaken for a hang.
/// </summary>
public partial class App : System.Windows.Application
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

    private TranslationDatabase? _database;
    private string? _configPath;
    private SettingsWindow? _settingsWindow;

    private bool _overlayEnabled = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _shutdown = new CancellationTokenSource();

        // M1 diagnostic mode (§10): overlay + window tracking + a rectangle, without OCR/DB/pipeline.
        // Lets the milestone be exercised on a real machine before the models and database exist.
        var m1 = e.Args.Any(arg => string.Equals(arg, "--m1", StringComparison.OrdinalIgnoreCase));
        var m2 = e.Args.Any(arg => string.Equals(arg, "--m2", StringComparison.OrdinalIgnoreCase));
        var framesTest = e.Args.Any(arg => string.Equals(arg, "--frames-test", StringComparison.OrdinalIgnoreCase));
        var settingsOnly = e.Args.Any(arg => string.Equals(arg, "--settings", StringComparison.OrdinalIgnoreCase));
        var extractTest = e.Args.Any(arg => string.Equals(arg, "--extract-test", StringComparison.OrdinalIgnoreCase));

        // Non-interactive runs (the benchmarks, the frame regression, or an M1 run with a frame
        // budget) exit on their own; a modal error box would hang them, so on failure we log and
        // exit instead of blocking on a dialog nobody can dismiss.
        var headless = m2 || framesTest || extractTest || (m1 && e.Args.Any(arg => string.Equals(arg, "--frames", StringComparison.OrdinalIgnoreCase)));

        try
        {
            if (m2)
                await RunM2BenchmarkAsync(e.Args, _shutdown.Token);
            else if (framesTest)
                await RunFramesTestAsync(e.Args, _shutdown.Token);
            else if (m1)
                await RunM1DiagnosticAsync(e.Args, _shutdown.Token);
            else if (settingsOnly)
                RunSettingsOnly();
            else if (extractTest)
                RunExtractTest(e.Args);
            else
                await StartAsync(_shutdown.Token);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed.");
            if (!headless)
            {
                MessageBox.Show(
                    $"Не удалось запустить переводчик:\n\n{ex.Message}",
                    "CZN Translator",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(1);
        }
    }

    /// <summary>
    /// M1: put a click-through rectangle over the game window and keep it glued there (§6, §10).
    /// Deliberately skips the database, the models and the OCR pipeline — it exists to prove the
    /// overlay, the DirectComposition device stack and the window tracker work on real hardware,
    /// which is the whole content of the M1 milestone. Pass <c>--frames N</c> to run headless for a
    /// fixed number of frames and exit, so it can be checked without a human watching the screen.
    /// </summary>
    private async Task RunM1DiagnosticAsync(string[] args, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);
        var config = _configService.Current;

        ConfigureLogging(config.Logging);
        Log.Information("M1 diagnostic: overlay + window tracking + rectangle, no OCR/DB/pipeline.");

        int? frameBudget = null;
        var framesFlag = Array.FindIndex(args, a => string.Equals(a, "--frames", StringComparison.OrdinalIgnoreCase));
        if (framesFlag >= 0 && framesFlag + 1 < args.Length && int.TryParse(args[framesFlag + 1], out var parsed))
            frameBudget = parsed;

        // Follow the game if it is up; otherwise sit on whatever window is in front, so the whole
        // path still runs and can be seen even with the game closed.
        var target = new WindowFinder().Find(config.Capture);
        nint targetHandle;
        int targetProcessId;

        if (target is not null)
        {
            targetHandle = target.Handle;
            targetProcessId = target.ProcessId;
            Log.Information("Following the game window: {Class} '{Title}' (pid {Pid}).",
                target.ClassName, target.Title, target.ProcessId);
        }
        else
        {
            targetHandle = GetForegroundWindow();
            _ = GetWindowThreadProcessId(targetHandle, out var pid);
            targetProcessId = (int)pid;
            Log.Warning(
                "Game window ({Process}/{Class}) not found; following the foreground window for the diagnostic.",
                config.Capture.ProcessName, config.Capture.WindowClass);
        }

        _overlayWindow = new OverlayWindow();
        _renderer = new OverlayRenderer(_overlayWindow.Handle, config.Overlay);
        Log.Information("Overlay window and DirectComposition device stack are up (hwnd {Handle:X}).", _overlayWindow.Handle);

        VerifyOverlayExStyle(_overlayWindow.Handle);

        if (targetHandle != nint.Zero)
        {
            _tracker = new TargetWindowTracker(targetHandle, targetProcessId);
            _tracker.Changed += (_, state) => Dispatcher.BeginInvoke(() => OnTargetWindowChanged(state));
            OnTargetWindowChanged(_tracker.Current);
        }
        else
        {
            _overlayWindow.SetBounds(new Core.Models.PixelRect(80, 80, 900, 240));
            _overlayWindow.Show();
            Log.Warning("No target window handle; showing the diagnostic rectangle at a fixed position.");
        }

        // A tray icon is the only way to quit an overlay launched from a shortcut: the overlay
        // window is click-through and cannot take focus, so there is nothing to Alt+F4. Skipped on
        // headless (--frames) runs, which exit on their own.
        if (frameBudget is null)
        {
            _tray = new TrayIcon("M1 диагностика · оверлей");
            _tray.ExitRequested += (_, _) => Shutdown();
            _tray.ToggleRequested += (_, _) => ToggleOverlay();
        }

        var frames = 0;
        var totalFrames = 0L;
        var fpsWindow = System.Diagnostics.Stopwatch.StartNew();

        // Drives the redraw. The surface is updated even while hidden — harmless, and it keeps the
        // fps measurement honest rather than reading zero whenever the target is not in front.
        _housekeeping = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(8) };
        _housekeeping.Tick += (_, _) =>
        {
            _tracker?.Poll();

            var bounds = _overlayWindow!.Bounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                _renderer!.DrawDiagnosticFrame(
                    bounds.Width, bounds.Height,
                    $"CZN M1 · {bounds.Width}×{bounds.Height} · click-through");
            }

            frames++;
            totalFrames++;

            if (fpsWindow.Elapsed.TotalSeconds >= 1)
            {
                Log.Information("M1 draw {Fps} fps (visible={Visible}, bounds={Bounds}).",
                    frames, _overlayWindow.IsVisible, _overlayWindow.Bounds);
                frames = 0;
                fpsWindow.Restart();
            }

            if (frameBudget is int budget && totalFrames >= budget)
            {
                Log.Information("M1 diagnostic reached the {Budget}-frame budget; exiting.", budget);
                Shutdown(0);
            }
        };
        _housekeeping.Start();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Confirms the overlay actually carries the click-through / topmost extended styles §6 asks
    /// for. CreateWindowEx can silently drop styles, and "click-through" is an M1 acceptance
    /// criterion, so it is worth asserting rather than assuming.
    /// </summary>
    private static void VerifyOverlayExStyle(nint hwnd)
    {
        const int GwlExStyle = -20;
        const long WsExLayered = 0x00080000;
        const long WsExTransparent = 0x00000020;
        const long WsExTopMost = 0x00000008;
        const long WsExNoActivate = 0x08000000;

        var exStyle = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        var required = WsExLayered | WsExTransparent | WsExTopMost | WsExNoActivate;
        var missing = required & ~exStyle;

        if (missing == 0)
        {
            Log.Information(
                "Overlay ex-style OK (0x{Ex:X}): layered + transparent (click-through) + topmost + no-activate.",
                exStyle);
        }
        else
        {
            Log.Warning(
                "Overlay ex-style is missing bits 0x{Missing:X} (have 0x{Ex:X}); click-through/topmost may not hold.",
                missing, exStyle);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    /// <summary>
    /// M2 (§10): run the real det + rec ONNX pipeline on a 400×80 zone and measure the latency,
    /// the milestone's acceptance number (det+rec ≤ 30 ms). Real game frames come later (§12);
    /// this uses a synthetic zone with known English text so the pipeline can be timed before any
    /// screenshots exist. Flags: <c>--models &lt;dir&gt;</c>, <c>--iters N</c>, <c>--cpu</c>.
    /// </summary>
    private async Task RunM2BenchmarkAsync(string[] args, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);
        var config = _configService.Current;
        ConfigureLogging(config.Logging);

        var modelsFlag = Array.FindIndex(args, a => string.Equals(a, "--models", StringComparison.OrdinalIgnoreCase));
        if (modelsFlag >= 0 && modelsFlag + 1 < args.Length)
            config.Ocr.ModelsDirectory = args[modelsFlag + 1];
        config.Ocr.ModelsDirectory = Path.GetFullPath(config.Ocr.ModelsDirectory);

        if (args.Any(a => string.Equals(a, "--cpu", StringComparison.OrdinalIgnoreCase)))
            config.Ocr.Provider = Core.Models.OcrProviderKind.Cpu;

        if (args.Any(a => string.Equals(a, "--quant", StringComparison.OrdinalIgnoreCase)))
            config.Ocr.Quantized = true; // picks the *_quant.onnx int8 pair (laptop profile).

        var iterations = 50;
        var itersFlag = Array.FindIndex(args, a => string.Equals(a, "--iters", StringComparison.OrdinalIgnoreCase));
        if (itersFlag >= 0 && itersFlag + 1 < args.Length && int.TryParse(args[itersFlag + 1], out var parsedIters))
            iterations = Math.Max(1, parsedIters);

        Log.Information("M2 benchmark: det+rec on a 400x80 zone, {Iters} iterations, models in {Dir}.",
            iterations, config.Ocr.ModelsDirectory);

        var adapterProvider = OperatingSystem.IsWindows()
            ? (IGraphicsAdapterProvider)new DxgiAdapterProvider()
            : new EmptyAdapterProvider();

        using var backend = new OcrBackendFactory(adapterProvider).Create(config);
        Log.Information("OCR backend: {Backend}", backend.Info);

        var warmup = System.Diagnostics.Stopwatch.StartNew();
        await backend.WarmUpAsync(cancellationToken);
        Log.Information("Warm-up finished in {Ms} ms.", warmup.ElapsedMilliseconds);

        var roi = M2TestImage.Render(400, 80, "Attack Power +15%");

        // One untimed read to confirm the pipeline actually recognizes the zone.
        var probe = await backend.RecognizeAsync(roi, OcrRequestOptions.Default, cancellationToken);
        Log.Information("Recognized {Count} line(s): {Text}",
            probe.Lines.Count,
            string.Join(" | ", probe.Lines.Select(l => $"'{l.Text}' ({l.Confidence:F2})")));

        var det = new List<double>(iterations);
        var rec = new List<double>(iterations);
        var total = new List<double>(iterations);

        for (var i = 0; i < iterations; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await backend.RecognizeAsync(roi, OcrRequestOptions.Default, cancellationToken);
            det.Add(result.DetectMs);
            rec.Add(result.RecognizeMs);
            total.Add(result.TotalMs);
        }

        static double Percentile(List<double> values, double p)
        {
            var sorted = values.OrderBy(x => x).ToList();
            var index = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[index];
        }

        var medianTotal = Percentile(total, 0.5);
        Log.Information("M2 det   ms: median {Med:F2}  p95 {P95:F2}", Percentile(det, 0.5), Percentile(det, 0.95));
        Log.Information("M2 rec   ms: median {Med:F2}  p95 {P95:F2}", Percentile(rec, 0.5), Percentile(rec, 0.95));
        Log.Information("M2 total ms: median {Med:F2}  p95 {P95:F2}  (target ≤ 30 ms)",
            medianTotal, Percentile(total, 0.95));
        Log.Information("M2 verdict: median det+rec {Med:F2} ms {Verdict} the 30 ms budget on {Backend}.",
            medianTotal, medianTotal <= 30 ? "MEETS" : "EXCEEDS", backend.Info);

        Shutdown(0);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Replays a folder of PNG frames through det + rec and, where a <c>&lt;frame&gt;.expected.json</c>
    /// sits next to one, checks the recognized lines against its <c>en</c> values (§12). This is the
    /// OCR half of the regression set — it needs no database, so it runs before the pack is imported;
    /// the <c>ru</c>/<c>source</c> fields become checkable once the full pipeline runs on a real DB.
    /// Flags: <c>--frames-test &lt;dir&gt;</c> (default: config's capture folder), <c>--models</c>, <c>--cpu</c>.
    /// </summary>
    private async Task RunFramesTestAsync(string[] args, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(configPath);
        var config = _configService.Current;
        ConfigureLogging(config.Logging);

        var modelsFlag = Array.FindIndex(args, a => string.Equals(a, "--models", StringComparison.OrdinalIgnoreCase));
        if (modelsFlag >= 0 && modelsFlag + 1 < args.Length)
            config.Ocr.ModelsDirectory = args[modelsFlag + 1];
        config.Ocr.ModelsDirectory = Path.GetFullPath(config.Ocr.ModelsDirectory);

        if (args.Any(a => string.Equals(a, "--cpu", StringComparison.OrdinalIgnoreCase)))
            config.Ocr.Provider = Core.Models.OcrProviderKind.Cpu;
        if (args.Any(a => string.Equals(a, "--quant", StringComparison.OrdinalIgnoreCase)))
            config.Ocr.Quantized = true;

        var dirFlag = Array.FindIndex(args, a => string.Equals(a, "--frames-test", StringComparison.OrdinalIgnoreCase));
        var dir = dirFlag >= 0 && dirFlag + 1 < args.Length && !args[dirFlag + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[dirFlag + 1]
            : config.Capture.Folder;
        dir = Path.GetFullPath(dir);

        if (!Directory.Exists(dir))
        {
            Log.Error("Frames directory {Dir} does not exist.", dir);
            Shutdown(2);
            return;
        }

        var frames = Directory.GetFiles(dir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Log.Information("Frames regression: {Count} PNG(s) in {Dir}.", frames.Length, dir);

        var adapterProvider = OperatingSystem.IsWindows()
            ? (IGraphicsAdapterProvider)new DxgiAdapterProvider()
            : new EmptyAdapterProvider();

        using var backend = new OcrBackendFactory(adapterProvider).Create(config);
        Log.Information("OCR backend: {Backend}", backend.Info);
        await backend.WarmUpAsync(cancellationToken);

        static string Normalize(string s) =>
            string.Join(' ', s.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var withExpectations = 0;
        var passed = 0;

        foreach (var frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var roi = FolderFrameSource.LoadGrayscale(frame);
            var result = await backend.RecognizeAsync(roi, OcrRequestOptions.Default, cancellationToken);
            var recognized = result.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var name = Path.GetFileName(frame);

            var expectedPath = Path.ChangeExtension(frame, ".expected.json");
            if (!File.Exists(expectedPath))
            {
                Log.Information("{Name}: no expectations · det+rec {Ms:F1} ms · read [{Read}]",
                    name, result.TotalMs, string.Join(" | ", recognized));
                continue;
            }

            withExpectations++;
            var expected = ReadExpectedEn(expectedPath);
            var recSet = recognized.Select(Normalize).OrderBy(x => x).ToList();
            var expSet = expected.Select(Normalize).OrderBy(x => x).ToList();
            var match = recSet.SequenceEqual(expSet);
            if (match)
                passed++;

            var missing = expSet.Except(recSet).ToList();
            var extra = recSet.Except(expSet).ToList();
            Log.Information("{Name}: {Verdict} · det+rec {Ms:F1} ms · read [{Read}]{Missing}{Extra}",
                name, match ? "PASS" : "FAIL", result.TotalMs, string.Join(" | ", recognized),
                missing.Count > 0 ? " · missing [" + string.Join(" | ", missing) + "]" : string.Empty,
                extra.Count > 0 ? " · extra [" + string.Join(" | ", extra) + "]" : string.Empty);
        }

        Log.Information("Frames regression: {Passed}/{WithExpectations} matched · {Total} frame(s) total.",
            passed, withExpectations, frames.Length);

        Shutdown(withExpectations == 0 || passed == withExpectations ? 0 : 2);
    }

    /// <summary>Flattens a <c>.expected.json</c> to the list of <c>en</c> strings across all zones.</summary>
    private static List<string> ReadExpectedEn(string path)
    {
        var result = new List<string>();
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (!document.RootElement.TryGetProperty("zones", out var zones))
            return result;

        foreach (var zone in zones.EnumerateObject())
        {
            if (!zone.Value.TryGetProperty("lines", out var lines))
                continue;

            foreach (var line in lines.EnumerateArray())
            {
                if (line.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.String)
                    result.Add(en.GetString()!);
            }
        }

        return result;
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configPath = configPath;
        _configService = new ConfigService(configPath);
        var config = _configService.Current;

        ConfigureLogging(config.Logging);
        Log.Information("CZN Translator starting with profile '{Profile}'.", config.Profile);

        var databasePath = Path.Combine(AppContext.BaseDirectory, config.Lookup.DatabasePath);
        var database = new TranslationDatabase(databasePath);
        database.EnsureCreated();
        _database = database;

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
        _tray.SettingsRequested += (_, _) => Dispatcher.BeginInvoke(() => OpenSettings());

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

    /// <summary>
    /// <c>--settings</c>: open the native settings window on its own (config + database, no overlay
    /// or OCR pipeline), so it can be built and used without the game running. Closing it exits.
    /// </summary>
    private void RunSettingsOnly()
    {
        _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        _configService = new ConfigService(_configPath);
        ConfigureLogging(_configService.Current.Logging);
        Log.Information("Settings-only mode: native settings window, no overlay/pipeline.");

        OpenDatabase(_configService.Current);

        var window = OpenSettings();
        window.Closed += (_, _) => Shutdown(0);
    }

    /// <summary><c>--extract-test &lt;pack&gt;</c>: decode the pack in C# and print the pair count, to
    /// check the native extractor against the Python one without the game or the UI.</summary>
    private void RunExtractTest(string[] args)
    {
        ConfigureLogging(new LoggingSection());

        var flag = Array.FindIndex(args, a => string.Equals(a, "--extract-test", StringComparison.OrdinalIgnoreCase));
        var packPath = flag >= 0 && flag + 1 < args.Length ? args[flag + 1] : null;
        if (packPath is null || !File.Exists(packPath))
        {
            Log.Error("--extract-test needs an existing data.pack path.");
            Shutdown(2);
            return;
        }

        if (!PackExtractor.TryLoadDefault(out var extractor, out var error))
        {
            Log.Error("Keys unavailable: {Error}", error);
            Shutdown(2);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pairs = extractor!.Extract(packPath, "en", new Progress<string>(m => Log.Information("{Msg}", m)), CancellationToken.None);
        Log.Information("Extracted {Count} key→text pairs in {Sec:F1}s.", pairs.Count, stopwatch.Elapsed.TotalSeconds);
        foreach (var (key, value) in pairs.Take(5))
            Log.Information("  {Key} = {Value}", key, value);
        Shutdown(0);
    }

    /// <summary>Opens the settings window, or brings the existing one to the front.</summary>
    private SettingsWindow OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return _settingsWindow;
        }

        var repository = _database is not null ? new StringRepository(_database) : null;
        _settingsWindow = new SettingsWindow(_configService!, repository, _configPath!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        return _settingsWindow;
    }

    private void OpenDatabase(AppConfig config)
    {
        try
        {
            var dbPath = Path.Combine(AppContext.BaseDirectory, config.Lookup.DatabasePath);
            var database = new TranslationDatabase(dbPath);
            database.EnsureCreated();
            _database = database;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database unavailable; the settings dashboard will be empty.");
            _database = null;
        }
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
