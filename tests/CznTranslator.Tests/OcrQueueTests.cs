using CznTranslator.Core.Abstractions;
using CznTranslator.Core.Models;
using CznTranslator.Detect;
using Xunit;

namespace CznTranslator.Tests;

public class OcrJobQueueTests
{
    private static OcrJob Job(string zone, int priority, long enqueuedAt) =>
        new(zone, priority, 0UL, GrayImage.Allocate(4, 4), OcrRequestOptions.Default, enqueuedAt);

    [Fact]
    public void Empty_queue_returns_null()
    {
        Assert.Null(new OcrJobQueue().Dequeue());
        Assert.Null(new OcrJobQueue().Peek());
    }

    [Fact]
    public void Lowest_priority_number_wins()
    {
        var queue = new OcrJobQueue();
        queue.Enqueue(Job("topbar", 2, 0));
        queue.Enqueue(Job("tooltip", 1, 1));
        queue.Enqueue(Job("dialogue", 0, 2));

        Assert.Equal("dialogue", queue.Dequeue()!.ZoneId);
        Assert.Equal("tooltip", queue.Dequeue()!.ZoneId);
        Assert.Equal("topbar", queue.Dequeue()!.ZoneId);
    }

    [Fact]
    public void Equal_priorities_are_first_in_first_out()
    {
        var queue = new OcrJobQueue();
        queue.Enqueue(Job("a", 1, 10));
        queue.Enqueue(Job("b", 1, 20));
        queue.Enqueue(Job("c", 1, 5));

        Assert.Equal("c", queue.Dequeue()!.ZoneId);
        Assert.Equal("a", queue.Dequeue()!.ZoneId);
        Assert.Equal("b", queue.Dequeue()!.ZoneId);
    }

    [Fact]
    public void A_zone_that_changes_twice_is_queued_once_with_the_newer_content()
    {
        var queue = new OcrJobQueue();
        queue.Enqueue(Job("dialogue", 0, 1) with { ZoneHash = 111UL });

        var displaced = queue.Enqueue(Job("dialogue", 0, 2) with { ZoneHash = 222UL });

        Assert.True(displaced);
        Assert.Equal(1, queue.Count);
        Assert.Equal(222UL, queue.Dequeue()!.ZoneHash);
    }

    [Fact]
    public void Peek_does_not_consume()
    {
        var queue = new OcrJobQueue();
        queue.Enqueue(Job("dialogue", 0, 1));

        Assert.Equal("dialogue", queue.Peek()!.ZoneId);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Remove_and_clear_work()
    {
        var queue = new OcrJobQueue();
        queue.Enqueue(Job("a", 0, 1));
        queue.Enqueue(Job("b", 0, 2));

        Assert.True(queue.Remove("a"));
        Assert.False(queue.Remove("a"));
        Assert.Equal(1, queue.Count);

        queue.Clear();
        Assert.Equal(0, queue.Count);
    }
}

public class OcrSchedulerTests
{
    private static OcrJob Job(string zone, int priority) =>
        new(zone, priority, 0UL, GrayImage.Allocate(4, 4), OcrRequestOptions.Default, Environment.TickCount64);

    [Fact]
    public async Task Runs_one_job_at_a_time()
    {
        var concurrent = 0;
        var peak = 0;
        var completed = new CountdownEvent(4);

        await using var scheduler = new OcrScheduler(async (_, _) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            InterlockedMax(ref peak, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
            completed.Signal();
        });

        scheduler.Start();
        for (var i = 0; i < 4; i++)
            scheduler.Submit(Job($"zone{i}", 1));

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)), "Jobs did not finish in time.");
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Priority_zero_preempts_a_running_background_job()
    {
        var backgroundStarted = new ManualResetEventSlim();
        var dialogueDone = new ManualResetEventSlim();
        var backgroundCancelled = false;

        await using var scheduler = new OcrScheduler(async (job, token) =>
        {
            if (job.ZoneId == "tooltip")
            {
                backgroundStarted.Set();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
                catch (OperationCanceledException)
                {
                    backgroundCancelled = true;
                    throw;
                }

                return;
            }

            dialogueDone.Set();
        });

        scheduler.Start();
        scheduler.Submit(Job("tooltip", 1));
        Assert.True(backgroundStarted.Wait(TimeSpan.FromSeconds(5)), "Background job never started.");

        scheduler.Submit(Job("dialogue", 0));

        Assert.True(dialogueDone.Wait(TimeSpan.FromSeconds(5)), "The priority-0 job did not run.");
        Assert.True(backgroundCancelled);
    }

    [Fact]
    public async Task A_preempted_job_is_retried_rather_than_dropped()
    {
        // The change detector reports a settled zone once, so dropping the job would leave that
        // screen untranslated until it happened to change again.
        var tooltipAttempts = 0;
        var tooltipFinished = new ManualResetEventSlim();
        var firstAttemptRunning = new ManualResetEventSlim();

        await using var scheduler = new OcrScheduler(async (job, token) =>
        {
            if (job.ZoneId != "tooltip")
                return;

            var attempt = Interlocked.Increment(ref tooltipAttempts);
            if (attempt == 1)
            {
                firstAttemptRunning.Set();
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                return;
            }

            tooltipFinished.Set();
        });

        scheduler.Start();
        scheduler.Submit(Job("tooltip", 1));
        Assert.True(firstAttemptRunning.Wait(TimeSpan.FromSeconds(5)));

        scheduler.Submit(Job("dialogue", 0));

        Assert.True(tooltipFinished.Wait(TimeSpan.FromSeconds(5)), "The preempted job was never retried.");
        Assert.Equal(2, tooltipAttempts);
    }

    [Fact]
    public async Task Repeated_preemption_eventually_drops_the_job()
    {
        var attempts = 0;

        await using var scheduler = new OcrScheduler(
            async (job, token) =>
            {
                if (job.ZoneId != "tooltip")
                    return;

                Interlocked.Increment(ref attempts);
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            },
            maxPreemptions: 1);

        scheduler.Start();
        scheduler.Submit(Job("tooltip", 1));

        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(60);
            scheduler.Submit(Job("dialogue", 0));
        }

        await Task.Delay(300);

        // One original run plus one retry, then the job is abandoned so the queue keeps moving.
        Assert.True(attempts <= 2, $"Job was retried {attempts} times, expected at most 2.");
    }

    [Fact]
    public async Task An_exception_in_one_job_does_not_stop_the_worker()
    {
        var secondRan = new ManualResetEventSlim();

        await using var scheduler = new OcrScheduler((job, _) =>
        {
            if (job.ZoneId == "bad")
                throw new InvalidOperationException("model blew up");

            secondRan.Set();
            return Task.CompletedTask;
        });

        scheduler.Start();
        scheduler.Submit(Job("bad", 1));
        scheduler.Submit(Job("good", 1));

        Assert.True(secondRan.Wait(TimeSpan.FromSeconds(5)), "The worker died with the failing job.");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
