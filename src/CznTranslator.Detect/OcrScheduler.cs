using Serilog;

namespace CznTranslator.Detect;

/// <summary>
/// Drives the single OCR queue: exactly one inference in flight, priority-0 zones preempt
/// background work (TZ §3).
/// <para>
/// A preempted job is pushed back rather than dropped. The change detector reports a settled
/// zone once, so a dropped job would leave that screen untranslated until it changed again.
/// <c>maxPreemptions</c> bounds the push-back so a busy priority-0 zone cannot starve the
/// background queue indefinitely.
/// </para>
/// </summary>
public sealed class OcrScheduler : IAsyncDisposable
{
    private readonly OcrJobQueue _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<OcrJob, CancellationToken, Task> _execute;
    private readonly ILogger _log;
    private readonly int _maxPreemptions;

    private Task? _worker;
    private CancellationTokenSource? _runningCts;
    private int _runningPriority = int.MaxValue;
    private bool _disposed;

    public OcrScheduler(
        Func<OcrJob, CancellationToken, Task> execute,
        ILogger? log = null,
        int maxPreemptions = 2)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _log = log ?? Log.Logger;
        _maxPreemptions = maxPreemptions;
    }

    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(() => WorkerLoopAsync(_shutdown.Token));
        }
    }

    public void Submit(OcrJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        CancellationTokenSource? toCancel = null;

        lock (_gate)
        {
            if (_disposed)
                return;

            if (_queue.Enqueue(job))
                _log.Debug("Zone {Zone} changed again before recognition, superseding the pending job.", job.ZoneId);

            if (job.Priority < _runningPriority && _runningCts is { IsCancellationRequested: false })
            {
                _log.Debug(
                    "Preempting the running priority-{Running} job for zone {Zone} (priority {Priority}).",
                    _runningPriority, job.ZoneId, job.Priority);
                toCancel = _runningCts;
            }
        }

        toCancel?.Cancel();
        _signal.Release();
    }

    /// <summary>Drops everything pending — used when the game window disappears or on Ctrl+Alt+R.</summary>
    public void Clear()
    {
        lock (_gate) _queue.Clear();
    }

    private async Task WorkerLoopAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!shutdown.IsCancellationRequested)
            {
                OcrJob? job;
                CancellationTokenSource cts;

                lock (_gate)
                {
                    job = _queue.Dequeue();
                    if (job is null)
                        break;

                    cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
                    _runningCts = cts;
                    _runningPriority = job.Priority;
                }

                try
                {
                    await _execute(job, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
                {
                    Requeue(job);
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "OCR job for zone {Zone} failed.", job.ZoneId);
                }
                finally
                {
                    lock (_gate)
                    {
                        _runningCts = null;
                        _runningPriority = int.MaxValue;
                    }

                    cts.Dispose();
                }
            }
        }
    }

    private void Requeue(OcrJob job)
    {
        if (job.PreemptionCount >= _maxPreemptions)
        {
            _log.Debug(
                "Dropping zone {Zone} after {Count} preemptions to keep the background queue moving.",
                job.ZoneId, job.PreemptionCount);
            return;
        }

        lock (_gate)
        {
            if (_disposed)
                return;
            _queue.Enqueue(job with { PreemptionCount = job.PreemptionCount + 1 });
        }

        _signal.Release();
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;

        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            worker = _worker;
            _queue.Clear();
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }
}
