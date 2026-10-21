using System.Collections.Concurrent;
using Bosun.Application.Monitoring;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Bosun.Monitoring.Tests;

public class NotifierFailureTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private sealed class ThrowingNotifier : IMonitoringNotifier
    {
        public int Calls;

        public Task AlarmAsync(AlarmNotification notification) => Fail();

        public Task ConnectionAsync(ConnectionStatusDto status) => Fail();

        public Task NavigationAsync(NavigationDto navigation) => Fail();

        private Task Fail()
        {
            Interlocked.Increment(ref Calls);
            throw new InvalidOperationException("hub is broken");
        }
    }

    private sealed class HangingNotifier : IMonitoringNotifier
    {
        public int Started;

        public Task AlarmAsync(AlarmNotification notification)
        {
            Interlocked.Increment(ref Started);
            return new TaskCompletionSource().Task; // never completes: a client that stopped reading
        }

        public Task ConnectionAsync(ConnectionStatusDto status) => Task.CompletedTask;

        public Task NavigationAsync(NavigationDto navigation) => Task.CompletedTask;
    }

    private sealed class RecordingNotifier : IMonitoringNotifier
    {
        public ConcurrentQueue<AlarmNotification> Alarms { get; } = new();

        public Task AlarmAsync(AlarmNotification notification)
        {
            Alarms.Enqueue(notification);
            return Task.CompletedTask;
        }

        public Task ConnectionAsync(ConnectionStatusDto status) => Task.CompletedTask;

        public Task NavigationAsync(NavigationDto navigation) => Task.CompletedTask;
    }

    private sealed class CountingLogger<T> : ILogger<T>
    {
        public int Warnings;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Interlocked.Increment(ref Warnings);
            }
        }
    }

    private sealed class FakeRepository(params TagAlarmConfig[] configs) : IAlarmRepository
    {
        public Task<IReadOnlyList<TagAlarmConfig>> LoadLimitsAsync(int projectId, IReadOnlyCollection<string> tags, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagAlarmConfig>>(configs);

        public Task<AlarmHistory> LoadHistoryAsync(int projectId, int recentClosed, CancellationToken ct) =>
            Task.FromResult(new AlarmHistory([], [], 0));
    }

    private sealed class ListQueue : IAlarmWriteQueue
    {
        public ConcurrentQueue<AlarmTransition> Items { get; } = new();

        public void Enqueue(AlarmTransition transition) => Items.Enqueue(transition);
    }

    private static AlarmManager Manager(IMonitoringNotifier notifier, ListQueue queue, ILogger<AlarmManager>? logger = null) =>
        new(
            new MonitoringOptions { ProjectId = 1 },
            ["T-1"],
            new FakeRepository(new TagAlarmConfig("T-1", "C", "t", [new AlarmLimitSpec(AlarmLevel.H, 95, 0, 2)])),
            queue,
            notifier,
            new ConfigChangeNotifier(),
            TimeProvider.System,
            logger);

    [Fact]
    public async Task A_throwing_hub_does_not_break_connection_or_navigation_updates_and_logs_once()
    {
        var notifier = new ThrowingNotifier();
        var logger = new CountingLogger<MonitoringState>();
        var state = new MonitoringState(notifier, TimeProvider.System, logger);

        await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Connected);
        await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Disconnected, "x");
        for (var i = 0; i < 20; i++)
        {
            await state.UpdateNavigationAsync(_ => new NavigationDto(true, 59.0 + i, 10.0, 5, 90, 90, T0));
        }

        state.Connection(MonitoringState.ModbusName).State.Should().Be(ConnectionState.Disconnected);
        state.Navigation!.Latitude.Should().Be(78.0, "the state is updated even though the push failed");
        notifier.Calls.Should().Be(22, "every push was attempted");
        logger.Warnings.Should().Be(1, "a broken hub is reported once per interval, not per update");
    }

    [Fact]
    public async Task A_throwing_hub_does_not_stop_alarms_being_raised_stored_and_listed()
    {
        var queue = new ListQueue();
        using var manager = Manager(new ThrowingNotifier(), queue, new CountingLogger<AlarmManager>());

        await manager.ProcessAsync([new Sample("T-1", 99, T0)], CancellationToken.None);
        await manager.DrainNotificationsAsync(TimeSpan.FromSeconds(5));

        queue.Items.Should().ContainSingle().Which.Kind.Should().Be(AlarmEventKind.Raised);
        (await manager.ListAsync("active", 10, CancellationToken.None)).Items.Should().ContainSingle();
        var ack = await manager.AcknowledgeAsync(1, "op", CancellationToken.None);
        ack.State.Should().Be(AlarmState.Acknowledged);
    }

    [Fact]
    public async Task A_hub_that_never_answers_does_not_hold_the_alarm_gate()
    {
        var notifier = new HangingNotifier();
        var queue = new ListQueue();
        using var manager = Manager(notifier, queue);

        await manager.ProcessAsync([new Sample("T-1", 99, T0)], CancellationToken.None);
        await manager.ProcessAsync([new Sample("T-1", 80, T0.AddSeconds(1))], CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var list = await manager.ListAsync("all", 10, timeout.Token);
        var counts = await manager.CountsAsync(timeout.Token);
        list.Items.Should().NotBeEmpty();
        counts.Active.Should().Be(1);
        queue.Items.Select(t => t.Kind).Should().Equal(AlarmEventKind.Raised, AlarmEventKind.ReturnedToNormal);
    }

    [Fact]
    public async Task Pushes_go_out_in_order_and_the_queue_is_bounded()
    {
        var recording = new RecordingNotifier();
        var queue = new ListQueue();
        using var manager = Manager(recording, queue);

        await manager.ProcessAsync([new Sample("T-1", 99, T0)], CancellationToken.None);
        await manager.ProcessAsync([new Sample("T-1", 80, T0.AddSeconds(1))], CancellationToken.None);
        await manager.DrainNotificationsAsync(TimeSpan.FromSeconds(5));

        recording.Alarms.Select(n => n.Kind).Should().Equal(AlarmEventKind.Raised, AlarmEventKind.ReturnedToNormal);
        AlarmManager.NotificationQueueCapacity.Should().BeGreaterThan(0);
        manager.DroppedNotifications.Should().Be(0);
    }
}

public class LiveValueStoreTests
{
    [Fact]
    public void MarkStale_flags_only_the_named_tags()
    {
        var store = new LiveValueStore();
        var now = DateTime.UtcNow;
        store.Set("A", 1, null, now);
        store.Set("B", 2, null, now);

        store.MarkStale(["A", "unknown"]);

        store.All().Single(v => v.Tag == "A").Quality.Should().Be(ValueQuality.Stale);
        store.All().Single(v => v.Tag == "B").Quality.Should().Be(ValueQuality.Good);
    }
}
