using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Bosun.Application.Monitoring;

/// <summary>Raised by <see cref="ChangeSetService"/> after a change set was committed, so the live side can reload alarm limits.</summary>
public sealed class ConfigChangeNotifier
{
    public event Action<int>? Applied;

    public void NotifyApplied(int projectId)
    {
        foreach (var handler in Applied?.GetInvocationList().Cast<Action<int>>() ?? [])
        {
            try
            {
                handler(projectId);
            }
            catch (Exception)
            {
                // A listener must never turn a committed apply into an error response.
            }
        }
    }
}

/// <summary>Alarm persistence used by the manager (database in production, anything in unit tests).</summary>
public interface IAlarmRepository
{
    /// <summary>Alarm limits of the given tags; tags without a limit are left out.</summary>
    Task<IReadOnlyList<TagAlarmConfig>> LoadLimitsAsync(int projectId, IReadOnlyCollection<string> tags, CancellationToken ct);

    /// <summary>Open alarms, the most recent closed ones, and the highest alarm id used so far.</summary>
    Task<AlarmHistory> LoadHistoryAsync(int projectId, int recentClosed, CancellationToken ct);
}

public sealed record AlarmHistory(IReadOnlyList<Alarm> Open, IReadOnlyList<Alarm> RecentClosed, int MaxId);

/// <summary>Receives alarm transitions to store. The implementation batches them on ONE background writer.</summary>
public interface IAlarmWriteQueue
{
    void Enqueue(AlarmTransition transition);
}

public readonly record struct Sample(string Tag, double Value, DateTime At);

/// <summary>
/// The live alarm engine of the backend: the <see cref="AlarmEvaluator"/> state machine plus everything around it. Samples come in
/// from the Modbus poller; limits are read from the database (and re-read when a change set is applied); every transition is
/// (1) kept in memory for the alarm list, (2) queued for the batch writer and (3) pushed to clients, in that order and
/// strictly in the order they happened. Alarm ids are allocated here, so a client can acknowledge an alarm it was told about
/// before the writer has stored it.
/// </summary>
public sealed class AlarmManager
{
    private readonly MonitoringOptions options;
    private readonly IReadOnlyCollection<string> tags;
    private readonly IAlarmRepository repository;
    private readonly IAlarmWriteQueue writer;
    private readonly IMonitoringNotifier notifier;
    private readonly TimeProvider time;
    private readonly ILogger? logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AlarmEvaluator evaluator;
    private readonly LinkedList<Alarm> recentClosed = new();
    private int nextAlarmId = 1;
    private bool initialised;
    private volatile bool limitsStale = true;
    private DateTime limitsLoadedAt = DateTime.MinValue;
    private int limitsGeneration;

    public AlarmManager(
        MonitoringOptions options,
        IReadOnlyCollection<string> tags,
        IAlarmRepository repository,
        IAlarmWriteQueue writer,
        IMonitoringNotifier notifier,
        ConfigChangeNotifier changes,
        TimeProvider time,
        ILogger<AlarmManager>? logger = null)
    {
        this.options = options;
        this.tags = tags;
        this.repository = repository;
        this.writer = writer;
        this.notifier = notifier;
        this.time = time;
        this.logger = logger;
        evaluator = new AlarmEvaluator(options.ProjectId, () => nextAlarmId++);
        changes.Applied += projectId =>
        {
            if (projectId == options.ProjectId)
            {
                Invalidate();
            }
        };
    }

    /// <summary>How many times the limits were (re)loaded; lets tests wait for a reload.</summary>
    public int LimitsGeneration => Volatile.Read(ref limitsGeneration);

    /// <summary>Marks the limits stale: the next sample (or <see cref="RefreshLimitsAsync"/>) reads them again.</summary>
    public void Invalidate() => limitsStale = true;

    /// <summary>Reads the database now: restores open alarms after a restart (once) and reloads the limits.</summary>
    public async Task RefreshLimitsAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await ReloadAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Evaluates a poll cycle's worth of samples.</summary>
    public async Task ProcessAsync(IReadOnlyList<Sample> samples, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!initialised || limitsStale || (time.GetUtcNow().UtcDateTime - limitsLoadedAt).TotalSeconds >= options.LimitsMaxAgeSeconds)
            {
                await ReloadAsync(ct);
            }

            var transitions = new List<AlarmTransition>();
            foreach (var sample in samples)
            {
                transitions.AddRange(evaluator.Evaluate(sample.Tag, sample.Value, sample.At));
            }

            await PublishAsync(transitions);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Operator acknowledgement. Throws <see cref="AppException"/>: 404 not_found, 409 already_acknowledged / alarm_closed.</summary>
    public async Task<AlarmDto> AcknowledgeAsync(int id, string actor, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var result = evaluator.Acknowledge(id, actor, time.GetUtcNow().UtcDateTime);
            switch (result.Outcome)
            {
                case AckOutcome.AlreadyAcknowledged:
                    throw new AppException(ErrorKind.Conflict, "already_acknowledged", $"Alarm {id} is already acknowledged.");
                case AckOutcome.NotOpen:
                    throw recentClosed.Any(a => a.Id == id)
                        ? new AppException(ErrorKind.Conflict, "alarm_closed", $"Alarm {id} is already cleared.")
                        : AppException.NotFound("Alarm", id);
            }

            await PublishAsync(result.Transitions);
            return AlarmDto.From(result.Alarm!);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The alarm list. <paramref name="state"/>: null or "all" = open alarms plus recent history; "active" = every open alarm
    /// (unacknowledged, acknowledged and returned_unacknowledged); or one state name. Newest first.
    /// </summary>
    public async Task<AlarmListDto> ListAsync(string? state, int limit, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var open = evaluator.OpenAlarms;
            IEnumerable<Alarm> selected = (state?.Trim().ToLowerInvariant()) switch
            {
                null or "" or "all" => open.Concat(recentClosed),
                "active" => open,
                "unacknowledged" => open.Where(a => a.State == AlarmState.Unacknowledged),
                "acknowledged" => open.Where(a => a.State == AlarmState.Acknowledged),
                "returned_unacknowledged" => open.Where(a => a.State == AlarmState.ReturnedUnacknowledged),
                "cleared" => recentClosed,
                _ => throw AppException.Invalid(
                    "state must be one of: all, active, unacknowledged, acknowledged, returned_unacknowledged, cleared.",
                    new Dictionary<string, object?> { ["state"] = state }),
            };

            var items = selected.OrderByDescending(a => a.RaisedAt).ThenByDescending(a => a.Id).Take(Math.Clamp(limit, 1, 1000)).Select(AlarmDto.From).ToList();
            return new AlarmListDto(items, open.Count, open.Count(a => a.State is AlarmState.Unacknowledged or AlarmState.ReturnedUnacknowledged));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(int Active, int Unacknowledged)> CountsAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var open = evaluator.OpenAlarms;
            return (open.Count, open.Count(a => a.State is AlarmState.Unacknowledged or AlarmState.ReturnedUnacknowledged));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Caller holds the gate.</summary>
    private async Task ReloadAsync(CancellationToken ct)
    {
        if (!initialised)
        {
            var history = await repository.LoadHistoryAsync(options.ProjectId, options.RecentAlarmHistory, ct);
            evaluator.Restore(history.Open);
            foreach (var closed in history.RecentClosed.OrderByDescending(a => a.ClearedAt ?? a.RaisedAt))
            {
                recentClosed.AddLast(closed);
            }

            nextAlarmId = Math.Max(nextAlarmId, history.MaxId + 1);
            initialised = true;
            if (history.Open.Count > 0)
            {
                logger?.LogInformation("Restored {Count} open alarm(s) from the database", history.Open.Count);
            }
        }

        // Cleared before the read: an apply that commits while we read marks it stale again and is picked up next time.
        limitsStale = false;
        try
        {
            var configs = await repository.LoadLimitsAsync(options.ProjectId, tags, ct);
            var now = time.GetUtcNow().UtcDateTime;
            await PublishAsync(evaluator.SetLimits(configs, now));
            limitsLoadedAt = now;
            Interlocked.Increment(ref limitsGeneration);
        }
        catch
        {
            limitsStale = true;
            throw;
        }
    }

    /// <summary>Caller holds the gate. Order matters: memory first (list is right immediately), then the writer, then the clients.</summary>
    private async Task PublishAsync(IReadOnlyList<AlarmTransition> transitions)
    {
        foreach (var t in transitions)
        {
            if (t.Kind == AlarmEventKind.Cleared)
            {
                recentClosed.AddFirst(t.Alarm);
                while (recentClosed.Count > options.RecentAlarmHistory)
                {
                    recentClosed.RemoveLast();
                }
            }

            writer.Enqueue(t);
            try
            {
                await notifier.AlarmAsync(new AlarmNotification(t.Kind, AlarmDto.From(t.Alarm), t.At, t.Value));
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Pushing an alarm event to clients failed; the alarm itself is unaffected");
            }
        }
    }
}
