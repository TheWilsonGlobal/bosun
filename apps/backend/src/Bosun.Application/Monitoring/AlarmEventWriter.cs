using System.Threading.Channels;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bosun.Application.Monitoring;

/// <summary>
/// The ONE writer of alarm rows (plan s5.1: SQLite has a single writer, so alarm events are written in batches by one background
/// writer). Producers call <see cref="Enqueue"/> (never blocks, never touches the database); <see cref="RunAsync"/> drains the
/// channel, waits a short moment so a burst becomes one batch, then stores the batch in one transaction while holding the
/// application-wide <see cref="WriteGate"/> - the same gate change-set writes use - so it queues behind an apply instead of
/// racing it. A failed batch is kept and retried; nothing is dropped while the process is up.
/// <para>
/// A batch stores, per alarm id, the LAST snapshot (insert or update) plus every event of the batch, so the order of transitions
/// inside a batch cannot produce a stale row.
/// </para>
/// </summary>
public sealed class AlarmEventWriter(IServiceScopeFactory scopes, WriteGate gate, MonitoringOptions options, ILogger<AlarmEventWriter>? logger = null) : IAlarmWriteQueue
{
    private abstract record Item;

    private sealed record Transition(AlarmTransition Value) : Item;

    private sealed record Marker(TaskCompletionSource Done) : Item;

    private readonly System.Threading.Channels.Channel<Item> channel = System.Threading.Channels.Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private long batches;
    private long events;
    private int lastBatchSize;

    /// <summary>Transactions committed so far.</summary>
    public long BatchesWritten => Interlocked.Read(ref batches);

    /// <summary>Alarm events stored so far.</summary>
    public long EventsWritten => Interlocked.Read(ref events);

    public int LastBatchSize => Volatile.Read(ref lastBatchSize);

    public void Enqueue(AlarmTransition transition) => channel.Writer.TryWrite(new Transition(transition));

    /// <summary>Completes when everything enqueued before this call has been stored (used when shutting down and by tests).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!channel.Writer.TryWrite(new Marker(done)))
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
            while (await reader.WaitToReadAsync(stopping))
            {
                if (linger > TimeSpan.Zero)
                {
                    await Task.Delay(linger, stopping);
                }

                await DrainOnceAsync(batchSize, stopping, retryUntilStored: true);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping: fall through to the final drain
        }

        channel.Writer.TryComplete();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (reader.TryPeek(out _))
            {
                await DrainOnceAsync(batchSize, deadline.Token, retryUntilStored: true);
            }
        }
        catch (OperationCanceledException)
        {
            logger?.LogWarning("Alarm writer stopped with events still queued (database busy for more than 5 s)");
        }
    }

    private async Task DrainOnceAsync(int batchSize, CancellationToken ct, bool retryUntilStored)
    {
        var batch = new List<AlarmTransition>();
        var markers = new List<Marker>();
        while (batch.Count < batchSize && channel.Reader.TryRead(out var item))
        {
            if (item is Transition t)
            {
                batch.Add(t.Value);
            }
            else if (item is Marker m)
            {
                markers.Add(m);
            }
        }

        if (batch.Count > 0)
        {
            await StoreWithRetryAsync(batch, ct, retryUntilStored);
        }

        foreach (var marker in markers)
        {
            marker.Done.TrySetResult();
        }
    }

    private async Task StoreWithRetryAsync(IReadOnlyList<AlarmTransition> batch, CancellationToken ct, bool retryUntilStored)
    {
        var delay = TimeSpan.FromMilliseconds(250);
        var failing = false;
        while (true)
        {
            try
            {
                await StoreAsync(batch, ct);
                if (failing)
                {
                    logger?.LogInformation("Alarm writer recovered; {Count} event(s) stored", batch.Count);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && retryUntilStored)
            {
                if (!failing)
                {
                    logger?.LogWarning(ex, "Storing {Count} alarm event(s) failed; keeping them and retrying", batch.Count);
                    failing = true;
                }

                await Task.Delay(delay, ct);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5000));
            }
        }
    }

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
