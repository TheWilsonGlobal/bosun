using Bosun.Application.Monitoring;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Bosun.Api;

/// <summary>
/// The server-to-client contract of <c>/hubs/monitoring</c> (SignalR, JSON protocol, cookie session - signed-in users only).
/// The wire method names are exactly these method names; SignalR clients match them case-insensitively.
/// </summary>
public interface IMonitoringClient
{
    /// <summary>Tags whose value changed since the previous batch, at most every <c>BroadcastIntervalMs</c> (500 ms = 2 Hz per tag).</summary>
    Task Values(ValuesBatchDto batch);

    /// <summary>An alarm was raised / acknowledged / returned to normal / cleared. Sent immediately, not throttled.</summary>
    Task AlarmEvent(AlarmNotification notification);

    /// <summary>The Modbus or NMEA link changed state (connected, connecting, disconnected, disabled). Only state CHANGES are sent.</summary>
    Task Connection(ConnectionStatusDto status);

    /// <summary>Position, speed, course and heading from the NMEA feed (about 1 Hz while it runs).</summary>
    Task Navigation(NavigationDto navigation);

    /// <summary>Sent to the caller right after it connects (catch-up): status, all latest values, navigation, open alarms.</summary>
    Task Snapshot(MonitoringSnapshotDto snapshot);
}

[Authorize]
public sealed class MonitoringHub(MonitoringState state, AlarmManager alarms, MonitoringOptions options, SnapshotThrottle throttle) : Hub<IMonitoringClient>
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        await Clients.Caller.Snapshot(await BuildSnapshotAsync(state, alarms, options, Context.ConnectionAborted));
    }

    /// <summary>
    /// The same catch-up message on demand (for example after the client re-subscribed its handlers). It copies every value and takes
    /// the alarm lock, so one build is shared by all callers for <see cref="SnapshotThrottle.Ttl"/> (1 s): calling it in a loop
    /// costs one build per second, and the answer is at most that old.
    /// </summary>
    public Task<MonitoringSnapshotDto> GetSnapshot() =>
        throttle.GetAsync(() => BuildSnapshotAsync(state, alarms, options, CancellationToken.None), Context.ConnectionAborted);

    public static async Task<MonitoringSnapshotDto> BuildSnapshotAsync(MonitoringState state, AlarmManager alarms, MonitoringOptions options, CancellationToken ct) =>
        new(
            await BuildStatusAsync(state, alarms, options, ct),
            [.. state.Values.All().Select(v => v.ToDto())],
            state.Navigation,
            (await alarms.ListAsync("active", 1000, ct)).Items);

    public static async Task<MonitoringStatusDto> BuildStatusAsync(MonitoringState state, AlarmManager alarms, MonitoringOptions options, CancellationToken ct)
    {
        var (active, unacknowledged) = await alarms.CountsAsync(ct);
        return new MonitoringStatusDto(
            options.Enabled,
            options.ProjectId,
            state.Connection(MonitoringState.ModbusName),
            state.Connection(MonitoringState.NmeaName),
            state.Values.Count,
            active,
            unacknowledged,
            state.Values.LastUpdate);
    }
}

/// <summary>Shares one snapshot build between all <c>GetSnapshot</c> callers within <see cref="Ttl"/>.</summary>
public sealed class SnapshotThrottle(TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private MonitoringSnapshotDto? cached;
    private long builtAt;
    private int builds;

    /// <summary>How many snapshots were really built (tests).</summary>
    public int Builds => Volatile.Read(ref builds);

    public async Task<MonitoringSnapshotDto> GetAsync(Func<Task<MonitoringSnapshotDto>> build, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is null || time.GetElapsedTime(builtAt) >= Ttl)
            {
                cached = await build();
                builtAt = time.GetTimestamp();
                Interlocked.Increment(ref builds);
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <summary>Pushes monitoring events to every connected client.</summary>
public sealed class SignalRMonitoringNotifier(IHubContext<MonitoringHub, IMonitoringClient> hub) : IMonitoringNotifier
{
    public Task AlarmAsync(AlarmNotification notification) => hub.Clients.All.AlarmEvent(notification);

    public Task ConnectionAsync(ConnectionStatusDto status) => hub.Clients.All.Connection(status);

    public Task NavigationAsync(NavigationDto navigation) => hub.Clients.All.Navigation(navigation);
}

/// <summary>Sends the batched <c>Values</c> event: one message per interval holding every tag that changed (so a tag goes out at most once per interval).</summary>
public sealed class ValuesBroadcaster(
    MonitoringState state,
    MonitoringOptions options,
    IHubContext<MonitoringHub, IMonitoringClient> hub,
    TimeProvider time,
    ILogger<ValuesBroadcaster> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(50, options.BroadcastIntervalMs)), time);
        long sent = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changed = state.Values.ChangedSince(sent, out var newest);
                if (changed.Count == 0)
                {
                    continue;
                }

                try
                {
                    await hub.Clients.All.Values(new ValuesBatchDto(time.GetUtcNow().UtcDateTime, [.. changed.Select(v => v.ToDto())]));
                    sent = newest;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Pushing a values batch failed; the next tick resends it");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
