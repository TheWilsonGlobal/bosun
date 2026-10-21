using System.Threading.Channels;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Application.Monitoring;

/// <summary>
/// Decides whether a failed store is worth retrying. Database specific knowledge (SQLite BUSY, a PostgreSQL serialization failure)
/// lives in the Infrastructure implementation; this default knows only the provider-neutral cases.
/// </summary>
public interface IWriteFailureClassifier
{
    /// <summary>True for a failure that can go away by itself (busy/locked database, I/O hiccup, timeout). False = retrying cannot help.</summary>
    bool IsTransient(Exception exception);
}

public sealed class DefaultWriteFailureClassifier : IWriteFailureClassifier
{
    public bool IsTransient(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is IOException or TimeoutException or OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The ONE writer of alarm rows (plan s5.1: SQLite has a single writer, so alarm events are written in batches by one background
/// writer). Producers call <see cref="Enqueue"/> (never touches the database); <see cref="RunAsync"/> drains the channel, waits a
/// short moment so a burst becomes one batch, then stores the batch in one transaction while holding the application-wide
/// <see cref="WriteGate"/> - the same gate change-set writes use - so it queues behind an apply instead of racing it.
/// <para>
/// A batch stores, per alarm id, the LAST snapshot (insert or update) plus every event of the batch, so the order of transitions
/// inside a batch cannot produce a stale row.
/// </para>
/// <para>
/// Failure policy. TRANSIENT failures (database busy/locked, I/O, timeout - see <see cref="IWriteFailureClassifier"/>) are retried
/// with a growing delay for as long as the process runs. PERMANENT failures (a constraint violation, a bad row) are not retried:
/// the batch is split in halves until the offending event is isolated, only that event is DEAD-LETTERED (logged, counted in
/// <see cref="DeadLetteredEvents"/> and appended to <see cref="MonitoringOptions.AlarmDeadLetterPath"/> as JSON lines) and every
/// other event is stored, so one poison event never blocks later alarms.
/// </para>
/// <para>
/// Back-pressure. The queue is bounded (<see cref="MonitoringOptions.AlarmQueueCapacity"/>, large enough that a healthy writer never
/// fills it). When it is full <see cref="Enqueue"/> does not drop at once: it counts the event in <see cref="QueueFullEvents"/>,
/// logs, and blocks for up to <see cref="MonitoringOptions.AlarmEnqueueBlockMs"/> for the writer to make room. Only after that
/// timeout is an event given up: it is dead-lettered to the file (never silently) and counted in <see cref="DroppedEvents"/>.
/// </para>
/// <para>
/// Shutdown. The batch being stored when the stop signal arrives stays in flight and is retried by the final drain, which also
/// flushes everything still queued (bounded by <see cref="MonitoringOptions.AlarmShutdownFlushSeconds"/>); whatever is still unwritten
/// after that is dead-lettered. Flush markers are always completed (cancelled when their events were not stored), so
/// <see cref="FlushAsync"/> cannot hang.
/// </para>
/// </summary>
public sealed class AlarmEventWriter(
    IServiceScopeFactory scopes,
    WriteGate gate,
    MonitoringOptions options,
    ILogger<AlarmEventWriter>? logger = null,
    IWriteFailureClassifier? classifier = null) : IAlarmWriteQueue
{
    private abstract record Item;

    private sealed record Transition(AlarmTransition Value) : Item;

    private sealed record Marker(TaskCompletionSource Done) : Item;

    private static readonly System.Text.Json.JsonSerializerOptions DeadLetterJson = new() { WriteIndented = false };

    private readonly IWriteFailureClassifier failures = classifier ?? new DefaultWriteFailureClassifier();

    private readonly System.Threading.Channels.Channel<Item> channel = System.Threading.Channels.Channel.CreateBounded<Item>(
        new BoundedChannelOptions(Math.Max(16, options.AlarmQueueCapacity)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    // The batch being stored. Only the writer loop touches them; they survive a cancellation so the final drain can finish the job.
    private readonly List<AlarmTransition> inFlight = [];
    private readonly List<Marker> inFlightMarkers = [];
    private long batches;
    private long events;
    private long queueFull;
    private long dropped;
    private long deadLettered;
    private int lastBatchSize;

    /// <summary>Test seam: replaces the database store (fault injection). Null in production.</summary>
    public Func<IReadOnlyList<AlarmTransition>, CancellationToken, Task>? StoreOverride { get; set; }

    /// <summary>Transactions committed so far.</summary>
    public long BatchesWritten => Interlocked.Read(ref batches);

    /// <summary>Alarm events stored so far.</summary>
    public long EventsWritten => Interlocked.Read(ref events);

    public int LastBatchSize => Volatile.Read(ref lastBatchSize);

    /// <summary>Times <see cref="Enqueue"/> found the queue full (each one waited for room).</summary>
    public long QueueFullEvents => Interlocked.Read(ref queueFull);

    /// <summary>Events given up after the enqueue wait timed out (they are in the dead-letter log).</summary>
    public long DroppedEvents => Interlocked.Read(ref dropped);

    /// <summary>Events that could not be stored and were written to the dead-letter log instead.</summary>
    public long DeadLetteredEvents => Interlocked.Read(ref deadLettered);

    public int QueueDepth => channel.Reader.Count;

    public int QueueCapacity => Math.Max(16, options.AlarmQueueCapacity);

    public void Enqueue(AlarmTransition transition)
    {
        var item = new Transition(transition);
        if (channel.Writer.TryWrite(item))
        {
            return;
        }

        Interlocked.Increment(ref queueFull);
        logger?.LogWarning("Alarm write queue is full ({Capacity} events); waiting up to {Ms} ms for the writer (queue-full count {Count})", QueueCapacity, options.AlarmEnqueueBlockMs, QueueFullEvents);
        try
        {
            using var wait = new CancellationTokenSource(Math.Max(1, options.AlarmEnqueueBlockMs));
            channel.Writer.WriteAsync(item, wait.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            Interlocked.Increment(ref dropped);
            logger?.LogError("Alarm event {Kind} of alarm {AlarmId} ({Tag}) was NOT queued ({Reason}); written to the dead-letter log", transition.Kind, transition.Alarm.Id, transition.Alarm.TagName, ex is ChannelClosedException ? "writer stopped" : "queue stayed full");
            DeadLetter([transition], ex is ChannelClosedException ? "writer stopped" : "queue full");
        }
    }

    /// <summary>Completes when everything enqueued before this call has been stored (used when shutting down and by tests).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await channel.Writer.WriteAsync(new Marker(done), ct);
        }
        catch (ChannelClosedException)
        {
            return; // already completed: the final drain wrote everything
        }

        await done.Task.WaitAsync(ct);
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        var reader = channel.Reader;
        var linger = TimeSpan.FromMilliseconds(Math.Max(0, options.AlarmFlushIntervalMs));
        var batchSize = Math.Max(1, options.AlarmBatchSize);
        try
        {
            try
            {
                while (await reader.WaitToReadAsync(stopping))
                {
                    if (linger > TimeSpan.Zero)
                    {
                        await Task.Delay(linger, stopping);
                    }

                    await DrainOnceAsync(batchSize, stopping);
                }
            }
            catch (OperationCanceledException)
            {
                // stopping: fall through to the final drain
            }

            channel.Writer.TryComplete();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, options.AlarmShutdownFlushSeconds)));
            try
            {
                while (inFlight.Count > 0 || inFlightMarkers.Count > 0 || reader.TryPeek(out _))
                {
                    await DrainOnceAsync(batchSize, deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
                logger?.LogWarning("Alarm writer stopped with events still queued (database busy for more than {Seconds} s)", options.AlarmShutdownFlushSeconds);
            }
        }
        finally
        {
            Abandon();
        }
    }

    private async Task DrainOnceAsync(int batchSize, CancellationToken ct)
    {
        while (inFlight.Count < batchSize && channel.Reader.TryRead(out var item))
        {
            if (item is Transition t)
            {
                inFlight.Add(t.Value);
            }
            else if (item is Marker m)
            {
                inFlightMarkers.Add(m);
            }
        }

        if (inFlight.Count > 0)
        {
            await StoreBatchAsync(inFlight, ct);
        }

        foreach (var marker in inFlightMarkers)
        {
            marker.Done.TrySetResult();
        }

        inFlightMarkers.Clear();
    }

    /// <summary>
    /// Stores <paramref name="batch"/> and leaves in it only what is NOT yet stored or dead-lettered (empty on success), so a
    /// cancellation in the middle never causes events to be stored twice.
    /// </summary>
    private async Task StoreBatchAsync(List<AlarmTransition> batch, CancellationToken ct)
    {
        var failure = await StoreWithRetryAsync(batch, ct);
        if (failure is null)
        {
            return;
        }

        if (batch.Count == 1)
        {
            logger?.LogError(failure, "Alarm event {Kind} of alarm {AlarmId} ({Tag}) cannot be stored and is dead-lettered", batch[0].Kind, batch[0].Alarm.Id, batch[0].Alarm.TagName);
            DeadLetter(batch, $"{failure.GetType().Name}: {Clip(failure.Message)}");
            batch.Clear();
            return;
        }

        logger?.LogWarning(failure, "A batch of {Count} alarm event(s) failed permanently; splitting it to isolate the bad event", batch.Count);
        var half = batch.Count / 2;
        var left = batch.GetRange(0, half);
        try
        {
            await StoreBatchAsync(left, ct);
        }
        finally
        {
            batch.RemoveRange(0, half - left.Count);
        }

        await StoreBatchAsync(batch, ct);
    }

    /// <summary>Null = stored (batch cleared). An exception = a PERMANENT failure. Transient failures are retried here until the token stops it.</summary>
    private async Task<Exception?> StoreWithRetryAsync(List<AlarmTransition> batch, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(1, options.AlarmRetryInitialMs));
        var maxDelay = TimeSpan.FromMilliseconds(Math.Max(options.AlarmRetryInitialMs, options.AlarmRetryMaxMs));
        var failing = false;
        while (true)
        {
            try
            {
                await (StoreOverride ?? StoreAsync)(batch, ct);
                if (failing)
                {
                    logger?.LogInformation("Alarm writer recovered; {Count} event(s) stored", batch.Count);
                }

                batch.Clear();
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!failures.IsTransient(ex))
                {
                    return ex;
                }

                if (!failing)
                {
                    logger?.LogWarning(ex, "Storing {Count} alarm event(s) failed (transient); keeping them and retrying", batch.Count);
                    failing = true;
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
            }
        }
    }

    /// <summary>The writer is finished: whatever is left is dead-lettered and every waiting flush is released.</summary>
    private void Abandon()
    {
        channel.Writer.TryComplete();
        var markers = new List<Marker>(inFlightMarkers);
        var leftover = new List<AlarmTransition>(inFlight);
        while (channel.Reader.TryRead(out var item))
        {
            if (item is Transition t)
            {
                leftover.Add(t.Value);
            }
            else if (item is Marker m)
            {
                markers.Add(m);
            }
        }

        inFlight.Clear();
        inFlightMarkers.Clear();
        if (leftover.Count > 0)
        {
            logger?.LogError("Alarm writer ended with {Count} alarm event(s) unstored; they are in the dead-letter log", leftover.Count);
            DeadLetter(leftover, "writer stopped before the events were stored");
        }

        foreach (var marker in markers)
        {
            if (leftover.Count > 0)
            {
                marker.Done.TrySetCanceled();
            }
            else
            {
                marker.Done.TrySetResult();
            }
        }
    }

    /// <summary>Counts the events and appends them as JSON lines to the dead-letter file when one is configured. Never throws.</summary>
    private void DeadLetter(IReadOnlyCollection<AlarmTransition> items, string reason)
    {
        Interlocked.Add(ref deadLettered, items.Count);
        var path = options.AlarmDeadLetterPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var lines = items.Select(t => System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    deadLetteredAt = DateTime.UtcNow,
                    reason,
                    alarmId = t.Alarm.Id,
                    projectId = t.Alarm.ProjectId,
                    tag = t.Alarm.TagName,
                    level = t.Alarm.Level.ToString(),
                    kind = t.Kind.ToString(),
                    at = t.At,
                    value = t.Value,
                    actor = t.Actor,
                },
                DeadLetterJson));
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not write {Count} alarm event(s) to the dead-letter file", items.Count);
        }
    }

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300];

    /// <summary>One transaction for the whole batch.</summary>
    internal async Task StoreAsync(IReadOnlyList<AlarmTransition> batch, CancellationToken ct)
    {
        var latest = batch.GroupBy(t => t.Alarm.Id).Select(g => g.Last().Alarm).ToList();
        var ids = latest.Select(a => a.Id).ToList();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        using var _ = await gate.EnterAsync(ct);

        // Autocommit read BEFORE the write transaction starts, so the write is the first statement of its transaction (a
        // read-then-write inside one deferred SQLite transaction can fail with SQLITE_BUSY_SNAPSHOT under WAL).
        var existing = (await db.Alarms.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        foreach (var alarm in latest)
        {
            if (existing.Contains(alarm.Id))
            {
                db.Alarms.Update(alarm);
            }
            else
            {
                db.Alarms.Add(alarm);
            }
        }

        foreach (var t in batch)
        {
            db.AlarmEvents.Add(new AlarmEvent { AlarmId = t.Alarm.Id, Kind = t.Kind, At = t.At, Value = t.Value, Actor = t.Actor, Note = t.Note });
        }

        await db.SaveChangesAsync(ct);
        Interlocked.Increment(ref batches);
        Interlocked.Add(ref events, batch.Count);
        Volatile.Write(ref lastBatchSize, batch.Count);
    }
}

/// <summary>Database side of <see cref="IAlarmRepository"/>. Singleton; every call uses its own scope.</summary>
public sealed class DbAlarmRepository(IServiceScopeFactory scopes) : IAlarmRepository
{
    public async Task<IReadOnlyList<TagAlarmConfig>> LoadLimitsAsync(int projectId, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var names = tags.ToList();
        var rows = await db.Tags.AsNoTracking()
            .Where(t => t.ProjectId == projectId && names.Contains(t.TagName) && t.AlarmLimits.Any())
            .Select(t => new
            {
                t.TagName,
                t.Unit,
                t.Description,
                Limits = t.AlarmLimits.Select(l => new { l.Level, l.Value, l.DelayS, l.Hysteresis }).ToList(),
            })
            .ToListAsync(ct);
        return rows
            .Select(r => new TagAlarmConfig(
                r.TagName,
                r.Unit,
                r.Description,
                r.Limits.OrderBy(l => l.Level).Select(l => new AlarmLimitSpec(l.Level, l.Value, l.DelayS, l.Hysteresis)).ToList()))
            .ToList();
    }

    public async Task<AlarmHistory> LoadHistoryAsync(int projectId, int recentClosed, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var open = await db.Alarms.AsNoTracking().Where(a => a.ProjectId == projectId && a.State != AlarmState.Cleared).ToListAsync(ct);
        var closed = await db.Alarms.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.State == AlarmState.Cleared)
            .OrderByDescending(a => a.Id)
            .Take(Math.Max(0, recentClosed))
            .ToListAsync(ct);
        var maxId = await db.Alarms.MaxAsync(a => (int?)a.Id, ct) ?? 0;
        return new AlarmHistory(open, closed, maxId);
    }
}
