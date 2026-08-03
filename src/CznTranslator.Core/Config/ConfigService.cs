using Serilog;

namespace CznTranslator.Core.Config;

public sealed record ConfigChangedEventArgs(AppConfig Config, IReadOnlyList<string> RestartRequired);

/// <summary>
/// Holds the current <see cref="AppConfig"/> and re-reads it when the file changes.
/// Editors write config files in several steps (truncate, write, rename), so the watcher
/// debounces and tolerates a partially written file instead of tearing down the pipeline.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly FileSystemWatcher? _watcher;
    private readonly object _gate = new();
    private readonly TimeSpan _debounce;

    private AppConfig _current;
    private Timer? _debounceTimer;
    private bool _disposed;

    public ConfigService(string path, ILogger? log = null, bool watch = true, TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path);
        _log = log ?? Log.Logger;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);

        var result = ConfigLoader.LoadFromFile(_path);
        _current = result.Config;
        foreach (var warning in result.Warnings)
            _log.Warning("Config: {Warning}", warning);

        if (!watch)
            return;

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            _log.Warning("Config directory {Directory} does not exist, live reload is off.", directory);
            return;
        }

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    public AppConfig Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    /// <summary>Re-reads the file immediately. Returns false when the file is unreadable or invalid.</summary>
    public bool Reload()
    {
        AppConfig previous;
        lock (_gate) previous = _current;

        ConfigLoadResult result;
        try
        {
            result = ConfigLoader.LoadFromFile(_path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _log.Warning(ex, "Config reload failed, keeping the previous configuration.");
            return false;
        }

        foreach (var warning in result.Warnings)
            _log.Warning("Config: {Warning}", warning);

        var restartRequired = LiveSettings.Compare(previous, result.Config);

        lock (_gate) _current = result.Config;

        if (restartRequired.Count > 0)
        {
            _log.Information(
                "Config reloaded. These take effect after a restart: {Keys}",
                string.Join(", ", restartRequired));
        }
        else
        {
            _log.Information("Config reloaded and applied live.");
        }

        Changed?.Invoke(this, new ConfigChangedEventArgs(result.Config, restartRequired));
        return true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => Reload(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
        }

        _debounceTimer?.Dispose();
    }
}
