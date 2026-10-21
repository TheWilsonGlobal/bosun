using System.Collections.Concurrent;

namespace Bosun.Application.Monitoring;

/// <summary>Latest value of one tag. <see cref="Version"/> grows with every change so the broadcaster can ask "what changed since".</summary>
public sealed record LiveValue(string Tag, double Value, string? Unit, string Quality, DateTime Timestamp, long Version)
{
    public LiveValueDto ToDto() => new(Tag, Value, Unit, Quality, Timestamp);
}

/// <summary>The latest value per tag, written by the Modbus poller and read by REST and the hub broadcaster. Thread-safe.</summary>
public sealed class LiveValueStore
{
    private readonly ConcurrentDictionary<string, LiveValue> values = new(StringComparer.Ordinal);
    private long version;

    public int Count => values.Count;

    public DateTime? LastUpdate { get; private set; }

    public void Set(string tag, double value, string? unit, DateTime timestamp)
    {
        values[tag] = new LiveValue(tag, value, unit, ValueQuality.Good, timestamp, Interlocked.Increment(ref version));
        LastUpdate = timestamp;
    }

    /// <summary>The source went away: keep the last values but flag them, so the mimic can grey them out instead of showing a lie.</summary>
    public void MarkAllStale()
    {
        foreach (var (tag, current) in values)
        {
            if (current.Quality != ValueQuality.Stale)
            {
                values[tag] = current with { Quality = ValueQuality.Stale, Version = Interlocked.Increment(ref version) };
            }
        }
    }

    public IReadOnlyList<LiveValue> All() => [.. values.Values.OrderBy(v => v.Tag, StringComparer.Ordinal)];

    /// <summary>Values changed after <paramref name="sinceVersion"/>; <paramref name="newest"/> is the version to pass next time.</summary>
    public IReadOnlyList<LiveValue> ChangedSince(long sinceVersion, out long newest)
    {
        var upTo = Interlocked.Read(ref version);
        newest = upTo;
        return [.. values.Values.Where(v => v.Version > sinceVersion && v.Version <= upTo).OrderBy(v => v.Tag, StringComparer.Ordinal)];
    }
}

/// <summary>Where connection changes, alarm changes and navigation updates go to be pushed to clients. The API replaces the no-op default with SignalR.</summary>
public interface IMonitoringNotifier
{
    Task AlarmAsync(AlarmNotification notification);

    Task ConnectionAsync(ConnectionStatusDto status);

    Task NavigationAsync(NavigationDto navigation);
}

public sealed class NullMonitoringNotifier : IMonitoringNotifier
{
    public Task AlarmAsync(AlarmNotification notification) => Task.CompletedTask;

    public Task ConnectionAsync(ConnectionStatusDto status) => Task.CompletedTask;

    public Task NavigationAsync(NavigationDto navigation) => Task.CompletedTask;
}

/// <summary>Everything the live side knows besides alarms: values, the two link states and the ship's position and heading.</summary>
public sealed class MonitoringState(IMonitoringNotifier notifier, TimeProvider time)
{
    public const string ModbusName = "modbus";
    public const string NmeaName = "nmea";

    private readonly object gate = new();
    private readonly Dictionary<string, ConnectionStatusDto> connections = new()
    {
        [ModbusName] = new(ModbusName, ConnectionState.Disconnected, DateTime.UtcNow, null, 0, null),
        [NmeaName] = new(NmeaName, ConnectionState.Disconnected, DateTime.UtcNow, null, 0, null),
    };

    private NavigationDto? navigation;

    public LiveValueStore Values { get; } = new();

    public NavigationDto? Navigation
    {
        get
        {
            lock (gate)
            {
                return navigation;
            }
        }
    }

    public ConnectionStatusDto Connection(string name)
    {
        lock (gate)
        {
            return connections[name];
        }
    }

    /// <summary>
    /// Records the state of a link. Clients are told only when the STATE changes (a retry that fails again is not news), so a dead
    /// simulator produces one "disconnected" event, not one per reconnect attempt.
    /// </summary>
    public async Task SetConnectionAsync(string name, ConnectionState state, string? error = null, int attempts = 0, string? endpoint = null)
    {
        ConnectionStatusDto next;
        bool changed;
        lock (gate)
        {
            var current = connections[name];
            changed = current.State != state;
            next = new ConnectionStatusDto(
                name,
                state,
                changed ? time.GetUtcNow().UtcDateTime : current.Since,
                error ?? (state == ConnectionState.Connected ? null : current.LastError),
                attempts,
                endpoint ?? current.Endpoint);
            connections[name] = next;
        }

        if (changed)
        {
            await notifier.ConnectionAsync(next);
        }
    }

    /// <summary>Merges new RMC and/or HDT data into the navigation record and pushes it.</summary>
    public async Task UpdateNavigationAsync(Func<NavigationDto?, NavigationDto> update)
    {
        NavigationDto next;
        lock (gate)
        {
            next = update(navigation);
            navigation = next;
        }

        await notifier.NavigationAsync(next);
    }

    public void ClearNavigation()
    {
        lock (gate)
        {
            navigation = null;
        }
    }
}
