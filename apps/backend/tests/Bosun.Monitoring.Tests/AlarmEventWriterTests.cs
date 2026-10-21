using Bosun.Application;
using Bosun.Application.Monitoring;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Bosun.Infrastructure.Monitoring;
using Bosun.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Monitoring.Tests;

/// <summary>The alarm batch writer on a real SQLite file: failure classes, the bounded queue, shutdown and flush.</summary>
public sealed class AlarmEventWriterTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "bosun-writer-tests", "t-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ServiceProvider provider;
    private int ids;

    public AlarmEventWriterTests()
    {
        Directory.CreateDirectory(dir);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "sqlite",
            ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "bosun.db")}",
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddBosunDatabase();
        services.AddBosunApplication();
        provider = services.BuildServiceProvider();
        DatabaseInitializer.MigrateAsync(provider).GetAwaiter().GetResult();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        db.Vessels.Add(new Vessel { Name = "V", Projects = [new Project { Name = "P", CreatedAt = DateTime.UtcNow }] });
        db.SaveChanges();
    }

    private string DeadLetterPath => Path.Combine(dir, "dead.jsonl");

    private MonitoringOptions Options(Action<MonitoringOptions>? change = null)
    {
        var o = new MonitoringOptions { AlarmFlushIntervalMs = 5, AlarmRetryInitialMs = 5, AlarmRetryMaxMs = 20, AlarmDeadLetterPath = DeadLetterPath };
        change?.Invoke(o);
        return o;
    }

    private AlarmEventWriter Writer(MonitoringOptions options, IWriteFailureClassifier? classifier = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<WriteGate>(), options, null, classifier ?? new SqliteWriteFailureClassifier());

    private AlarmTransition Transition(int projectId = 1, string tag = "T-1")
    {
        var id = Interlocked.Increment(ref ids);
        var at = DateTime.UtcNow;
        var alarm = new Alarm { Id = id, ProjectId = projectId, TagName = tag, Level = AlarmLevel.H, LimitValue = 95, State = AlarmState.Unacknowledged, RaisedAt = at, RaisedValue = 96, PeakValue = 96 };
        return new AlarmTransition(AlarmEventKind.Raised, alarm, at, 96, null, null);
    }

    private async Task<(int Alarms, int Events)> CountsAsync()
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        return (await db.Alarms.CountAsync(), await db.AlarmEvents.CountAsync());
    }

    public void Dispose()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // temp files outside the repo
        }
    }

    [Fact]
    public void The_sqlite_classifier_retries_busy_locked_and_io_but_never_a_constraint_violation()
    {
        var classifier = new SqliteWriteFailureClassifier();

        classifier.IsTransient(new SqliteException("busy", 5)).Should().BeTrue();
        classifier.IsTransient(new SqliteException("locked", 6)).Should().BeTrue();
        classifier.IsTransient(new SqliteException("io", 10)).Should().BeTrue();
        classifier.IsTransient(new DbUpdateException("wrapper", new SqliteException("busy", 5))).Should().BeTrue("EF wraps the provider exception");
        classifier.IsTransient(new SqliteException("FOREIGN KEY constraint failed", 19)).Should().BeFalse();
        classifier.IsTransient(new InvalidOperationException("bad row")).Should().BeFalse();
        classifier.IsTransient(new IOException("disk")).Should().BeTrue();
    }

    [Fact]
    public async Task A_poison_event_is_dead_lettered_alone_and_the_events_around_it_and_later_ones_are_stored()
    {
        var writer = Writer(Options());
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => writer.RunAsync(stop.Token));

        var poison = Transition(projectId: 999); // no such project: FOREIGN KEY constraint failed, which retrying cannot fix
        writer.Enqueue(Transition());
        writer.Enqueue(Transition());
        writer.Enqueue(poison);
        writer.Enqueue(Transition());
        await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        writer.Enqueue(Transition());
        await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        (await CountsAsync()).Should().Be((4, 4), "everything except the poison event, including the alarm that came after it");
        writer.DeadLetteredEvents.Should().Be(1);
        var lines = await File.ReadAllLinesAsync(DeadLetterPath);
        lines.Should().ContainSingle().Which.Should().Contain($"\"alarmId\":{poison.Alarm.Id}").And.Contain("\"reason\":");

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_nothing_is_dead_lettered_or_stored_twice()
    {
        var stored = new List<int>();
        var attempts = 0;
        var writer = Writer(Options(), new DefaultWriteFailureClassifier());
        writer.StoreOverride = (batch, _) =>
        {
            if (++attempts <= 3)
            {
                throw new IOException("disk hiccup");
            }

            stored.AddRange(batch.Select(t => t.Alarm.Id));
            return Task.CompletedTask;
        };
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => writer.RunAsync(stop.Token));

        var first = Transition();
        var second = Transition();
        writer.Enqueue(first);
        writer.Enqueue(second);
        await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        attempts.Should().Be(4);
        stored.Should().Equal(first.Alarm.Id, second.Alarm.Id);
        writer.DeadLetteredEvents.Should().Be(0);
        File.Exists(DeadLetterPath).Should().BeFalse();

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Shutdown_flushes_what_is_queued_and_a_batch_cancelled_in_flight_is_stored_once_by_the_final_drain()
    {
        var stored = new List<int>();
        var calls = 0;
        var inFlightStarted = new TaskCompletionSource();
        var writer = Writer(Options(o => o.AlarmFlushIntervalMs = 0), new DefaultWriteFailureClassifier());
        writer.StoreOverride = async (batch, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                inFlightStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // the stop signal arrives while this batch is being stored
            }

            stored.AddRange(batch.Select(t => t.Alarm.Id));
        };
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => writer.RunAsync(stop.Token));

        var first = Transition();
        writer.Enqueue(first);
        await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var queued = new[] { Transition(), Transition(), Transition() };
        foreach (var t in queued)
        {
            writer.Enqueue(t);
        }

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(20));

        stored.Should().BeEquivalentTo(new[] { first.Alarm.Id }.Concat(queued.Select(t => t.Alarm.Id)), "the in-flight batch was re-queued, not lost, and nothing was stored twice");
        stored.Should().OnlyHaveUniqueItems();
        writer.DeadLetteredEvents.Should().Be(0);
    }

    [Fact]
    public async Task Shutdown_with_a_database_that_stays_busy_dead_letters_the_rest_and_never_hangs_a_flush()
    {
        var writer = Writer(Options(o =>
        {
            o.AlarmShutdownFlushSeconds = 1;
            o.AlarmFlushIntervalMs = 0;
        }), new DefaultWriteFailureClassifier());
        writer.StoreOverride = (_, _) => throw new IOException("database stays busy");
        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => writer.RunAsync(stop.Token));

        writer.Enqueue(Transition());
        writer.Enqueue(Transition());
        var flush = writer.FlushAsync(CancellationToken.None);
        await Task.Delay(100);
        stop.Cancel();

        await running.WaitAsync(TimeSpan.FromSeconds(20));
        var act = async () => await flush.WaitAsync(TimeSpan.FromSeconds(10));
        await act.Should().ThrowAsync<OperationCanceledException>("the events were not stored, so the flush says so instead of hanging");
        writer.DeadLetteredEvents.Should().Be(2);
        (await File.ReadAllLinesAsync(DeadLetterPath)).Should().HaveCount(2);
        var after = async () => await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await after.Should().NotThrowAsync("a flush on a finished writer returns at once");
    }

    [Fact]
    public async Task A_full_queue_makes_the_producer_wait_and_only_gives_up_loudly_after_the_timeout()
    {
        var writer = Writer(Options(o =>
        {
            o.AlarmQueueCapacity = 16;
            o.AlarmEnqueueBlockMs = 150;
        }));

        for (var i = 0; i < 16; i++)
        {
            writer.Enqueue(Transition());
        }

        writer.QueueDepth.Should().Be(16);
        var overflow = Transition();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        writer.Enqueue(overflow); // nobody is draining: waits ~150 ms, then dead-letters
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));

        writer.QueueFullEvents.Should().Be(1);
        writer.DroppedEvents.Should().Be(1);
        writer.DeadLetteredEvents.Should().Be(1);
        (await File.ReadAllTextAsync(DeadLetterPath)).Should().Contain($"\"alarmId\":{overflow.Alarm.Id}").And.Contain("queue full");
        writer.QueueDepth.Should().Be(16, "the queued events are untouched");
    }

    [Fact]
    public async Task A_producer_blocked_on_a_full_queue_continues_without_loss_once_the_writer_drains()
    {
        var writer = Writer(Options(o =>
        {
            o.AlarmQueueCapacity = 16;
            o.AlarmEnqueueBlockMs = 20_000;
        }));
        var stored = 0;
        writer.StoreOverride = (batch, _) =>
        {
            Interlocked.Add(ref stored, batch.Count);
            return Task.CompletedTask;
        };
        for (var i = 0; i < 16; i++)
        {
            writer.Enqueue(Transition());
        }

        var blocked = Task.Run(() => writer.Enqueue(Transition()));
        await Task.Delay(100);
        blocked.IsCompleted.Should().BeFalse("the queue is full");

        using var stop = new CancellationTokenSource();
        var running = Task.Run(() => writer.RunAsync(stop.Token));
        await blocked.WaitAsync(TimeSpan.FromSeconds(20));
        await writer.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        stored.Should().Be(17);
        writer.DroppedEvents.Should().Be(0);
        writer.QueueFullEvents.Should().Be(1);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
