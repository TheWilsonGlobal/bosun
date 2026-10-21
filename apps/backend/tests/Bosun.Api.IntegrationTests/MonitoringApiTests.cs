extern alias sim;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Bosun.Application;
using Bosun.Application.Monitoring;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimulatorHost = sim::Bosun.Simulator.SimulatorHost;
using SimulatorOptions = sim::Bosun.Simulator.SimulatorOptions;

namespace Bosun.Api.IntegrationTests;

/// <summary>Live monitoring against an in-process simulator on ephemeral ports (plan s11 phase 7). Each test owns its app and database file.</summary>
public class MonitoringApiTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static SimulatorHost StartSim(double scale, double startAt, int modbusPort = 0, int nmeaPort = 0)
    {
        var sim = new SimulatorHost(new SimulatorOptions
        {
            ModbusPort = modbusPort,
            NmeaPort = nmeaPort,
            TimeScale = scale,
            StartAtSeconds = startAt,
            NmeaInterval = TimeSpan.FromMilliseconds(100),
        });
        sim.Start();
        return sim;
    }

    private static CustomApp MonitoringApp(int modbusPort, int nmeaPort) =>
        new("Development", new Dictionary<string, string?>
        {
            ["Monitoring:Enabled"] = "true",
            ["Monitoring:Modbus:Port"] = modbusPort.ToString(),
            ["Monitoring:Nmea:Port"] = nmeaPort.ToString(),
            ["Monitoring:Modbus:PollIntervalMs"] = "50",
            ["Monitoring:Modbus:ReconnectInitialMs"] = "50",
            ["Monitoring:Modbus:ReconnectMaxMs"] = "300",
            ["Monitoring:Nmea:ReconnectInitialMs"] = "50",
            ["Monitoring:Nmea:ReconnectMaxMs"] = "300",
            ["Monitoring:BroadcastIntervalMs"] = "500",
            ["Monitoring:AlarmFlushIntervalMs"] = "50",
        });

    private static async Task<string> CookieAsync(TestApp app)
    {
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var response = await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword });
        response.EnsureSuccessStatusCode();
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("bosun.session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static HubConnection Hub(TestApp app, string? cookie)
    {
        var server = app.Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/monitoring"), options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
            })
            .AddJsonProtocol(o => BosunJson.Configure(o.PayloadSerializerOptions))
            .Build();
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, int seconds = 25, string what = "condition")
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } found)
            {
                return found;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> probe, int seconds = 25, string what = "condition")
        where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe() is { } found)
            {
                return found;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static Task SetLimit(HttpClient client, string tag, double value, double delay, double hysteresis) =>
        client.ApplyOpsAsync($"H {value} on {tag}", new { type = "setAlarmLimit", tagName = tag, level = "H", @new = new { value, delayS = delay, hysteresis } });

    [Fact]
    public async Task Limit_applied_through_the_change_set_API_raises_an_alarm_on_hub_and_REST_which_can_be_acknowledged()
    {
        await using var sim = StartSim(scale: 1, startAt: 100);
        using var app = MonitoringApp(sim.ModbusPort, sim.NmeaPort!.Value);
        var client = await app.LoginAsync();
        var cookie = await CookieAsync(app);
        await using var hub = Hub(app, cookie);
        var alarms = new ConcurrentQueue<AlarmNotification>();
        var values = new ConcurrentQueue<(DateTime At, ValuesBatchDto Batch)>();
        var navigation = new ConcurrentQueue<NavigationDto>();
        MonitoringSnapshotDto? snapshot = null;
        hub.On<AlarmNotification>("AlarmEvent", alarms.Enqueue);
        hub.On<ValuesBatchDto>("Values", b => values.Enqueue((DateTime.UtcNow, b)));
        hub.On<NavigationDto>("Navigation", navigation.Enqueue);
        hub.On<MonitoringSnapshotDto>("Snapshot", s => snapshot = s);
        await hub.StartAsync();
        await WaitFor(() => snapshot, what: "snapshot");

        // The seed has no limit on ME-EXH-TT01, so nothing is raised however hot it gets until the change set is applied.
        await SetLimit(client, "ME-EXH-TT01", 95, delay: 1, hysteresis: 1);

        var raised = await WaitFor(() => alarms.FirstOrDefault(a => a.Kind == AlarmEventKind.Raised && a.Alarm.Tag == "ME-EXH-TT01"), what: "raised event");
        raised.Alarm.State.Should().Be(AlarmState.Unacknowledged);
        raised.Alarm.Level.Should().Be(AlarmLevel.H);
        raised.Alarm.LimitValue.Should().Be(95);
        raised.Alarm.DelayS.Should().Be(1);
        raised.Alarm.RaisedValue.Should().BeGreaterThan(95);
        alarms.Where(a => a.Alarm.Tag != "ME-EXH-TT01").Should().BeEmpty("only the configured tag has a limit");

        var list = await (await client.GetAsync("/api/monitoring/alarms?state=unacknowledged")).ReadAsync<AlarmListDto>();
        list.Items.Should().ContainSingle(a => a.Id == raised.Alarm.Id);
        (await client.GetFromJsonAsync<MonitoringStatusDto>("/api/monitoring/status", BosunJson.Options))!.UnacknowledgedAlarms.Should().Be(1);

        var live = await (await client.GetAsync("/api/monitoring/values")).ReadAsync<ValuesBatchDto>();
        live.Values.Should().Contain(v => v.Tag == "ME-EXH-TT01" && v.Value > 95 && v.Quality == "good");
        await WaitFor(() => navigation.FirstOrDefault(n => n.Valid && n.HeadingDeg != null && n.Latitude != null), what: "NMEA navigation");

        // Throttling: a tag is pushed at most about every 500 ms (2 Hz).
        foreach (var times in values.SelectMany(v => v.Batch.Values.Select(x => (x.Tag, v.At))).GroupBy(x => x.Tag).Select(g => g.OrderBy(x => x.At).ToList()))
        {
            for (var i = 1; i < times.Count; i++)
            {
                (times[i].At - times[i - 1].At).TotalMilliseconds.Should().BeGreaterThan(350, $"{times[0].Tag} must not be pushed faster than 2 Hz");
            }
        }

        using var anonymous = app.CreateClient();
        (await anonymous.PostJsonAsync($"/api/monitoring/alarms/{raised.Alarm.Id}/ack")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var acked = await (await client.PostJsonAsync($"/api/monitoring/alarms/{raised.Alarm.Id}/ack")).ReadAsync<AlarmDto>();
        acked.State.Should().Be(AlarmState.Acknowledged);
        acked.AcknowledgedBy.Should().Be(TestApp.AdminUser);
        var ackEvent = await WaitFor(() => alarms.FirstOrDefault(a => a.Kind == AlarmEventKind.Acknowledged), what: "ack event");
        ackEvent.Alarm.Id.Should().Be(raised.Alarm.Id);

        (await (await client.PostJsonAsync($"/api/monitoring/alarms/{raised.Alarm.Id}/ack")).ReadProblemAsync(409)).Code.Should().Be("already_acknowledged");
        (await (await client.PostJsonAsync("/api/monitoring/alarms/9999/ack")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await client.GetAsync("/api/monitoring/alarms?state=bogus")).IsSuccessStatusCode.Should().BeFalse();

        await app.Services.GetRequiredService<AlarmEventWriter>().FlushAsync(CancellationToken.None);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        var stored = await db.Alarms.AsNoTracking().SingleAsync(a => a.Id == raised.Alarm.Id);
        stored.State.Should().Be(AlarmState.Acknowledged);
        (await db.AlarmEvents.AsNoTracking().Where(e => e.AlarmId == stored.Id).Select(e => e.Kind).ToListAsync())
            .Should().BeEquivalentTo([AlarmEventKind.Raised, AlarmEventKind.Acknowledged]);
    }

    [Fact]
    public async Task An_acknowledged_alarm_is_cleared_when_the_value_returns_to_normal()
    {
        // t = 100 s: exhaust is ~100 degC and held there until t = 240 s; the test then jumps the scenario clock to 280 s (normal).
        await using var sim = StartSim(scale: 1, startAt: 100);
        using var app = MonitoringApp(sim.ModbusPort, sim.NmeaPort!.Value);
        var client = await app.LoginAsync();
        await using var hub = Hub(app, await CookieAsync(app));
        var alarms = new ConcurrentQueue<AlarmNotification>();
        hub.On<AlarmNotification>("AlarmEvent", alarms.Enqueue);
        await hub.StartAsync();

        await SetLimit(client, "ME-EXH-TT02", 95, delay: 0, hysteresis: 1);
        var raised = await WaitFor(() => alarms.FirstOrDefault(a => a.Kind == AlarmEventKind.Raised), what: "raised");
        (await client.PostJsonAsync($"/api/monitoring/alarms/{raised.Alarm.Id}/ack")).EnsureSuccessStatusCode();
        sim.Clock.SkipTo(280);
        await WaitFor(() => alarms.FirstOrDefault(a => a.Kind == AlarmEventKind.Cleared), what: "cleared");

        var cleared = await (await client.GetAsync("/api/monitoring/alarms?state=cleared")).ReadAsync<AlarmListDto>();
        cleared.Items.Should().ContainSingle(a => a.Id == raised.Alarm.Id && a.State == AlarmState.Cleared && a.ClearedAt != null);
        cleared.Active.Should().Be(0);
    }

    [Fact]
    public async Task Link_loss_is_reported_once_and_the_poller_reconnects_when_the_simulator_returns()
    {
        var modbusPort = FreePort();
        var nmeaPort = FreePort();
        var sim = StartSim(1, 0, modbusPort, nmeaPort);
        try
        {
            using var app = MonitoringApp(modbusPort, nmeaPort);
            var client = await app.LoginAsync();
            await using var hub = Hub(app, await CookieAsync(app));
            var connections = new ConcurrentQueue<ConnectionStatusDto>();
            hub.On<ConnectionStatusDto>("Connection", connections.Enqueue);
            await hub.StartAsync();

            async Task<MonitoringStatusDto> Status() => (await client.GetFromJsonAsync<MonitoringStatusDto>("/api/monitoring/status", BosunJson.Options))!;
            await WaitForAsync(async () => (await Status()).Modbus.State == ConnectionState.Connected ? "ok" : null, what: "modbus connected");

            await sim.DisposeAsync();
            await WaitForAsync(async () => (await Status()).Modbus.State == ConnectionState.Disconnected ? "ok" : null, what: "modbus disconnected");
            // A refused loopback connect can take ~1 s on Windows, so wait for several failed attempts (they must not produce more events).
            await WaitForAsync(async () => (await Status()).Modbus.Attempts >= 3 ? "ok" : null, what: "three failed reconnect attempts");
            connections.Count(c => c.Name == "modbus" && c.State == ConnectionState.Disconnected).Should().Be(1);
            (await client.GetFromJsonAsync<ValuesBatchDto>("/api/monitoring/values", BosunJson.Options))!.Values.Should().OnlyContain(v => v.Quality == "stale");

            sim = StartSim(1, 0, modbusPort, nmeaPort);
            await WaitForAsync(async () => (await Status()).Modbus.State == ConnectionState.Connected ? "ok" : null, what: "modbus reconnected");
            await WaitForAsync(async () => (await Status()).Nmea.State == ConnectionState.Connected ? "ok" : null, what: "nmea reconnected");
            (await client.GetFromJsonAsync<ValuesBatchDto>("/api/monitoring/values", BosunJson.Options))!.Values.Should().OnlyContain(v => v.Quality == "good");
        }
        finally
        {
            await sim.DisposeAsync();
        }
    }

    [Fact]
    public async Task With_no_simulator_the_backend_degrades_cleanly()
    {
        using var app = MonitoringApp(FreePort(), FreePort());
        var client = await app.LoginAsync();
        var status = await WaitForAsync(
            async () => (await client.GetFromJsonAsync<MonitoringStatusDto>("/api/monitoring/status", BosunJson.Options))! is { Modbus.State: ConnectionState.Disconnected, Nmea.State: ConnectionState.Disconnected } s ? s : null,
            what: "both links disconnected");
        status.Modbus.LastError.Should().NotBeNullOrEmpty();
        (await client.GetAsync("/health")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_hub_needs_a_session_and_sends_a_snapshot_to_a_signed_in_client()
    {
        using var app = new CustomApp("Development");
        await using var anonymous = Hub(app, null);
        var failure = await Record.ExceptionAsync(() => anonymous.StartAsync());
        failure.Should().NotBeNull("an unauthenticated connection is refused");

        await using var signedIn = Hub(app, await CookieAsync(app));
        MonitoringSnapshotDto? snapshot = null;
        signedIn.On<MonitoringSnapshotDto>("Snapshot", s => snapshot = s);
        await signedIn.StartAsync();
        var got = await WaitFor(() => snapshot, what: "snapshot");
        got.Status.Enabled.Should().BeFalse();
        got.Status.Modbus.State.Should().Be(ConnectionState.Disabled);
    }

    [Fact]
    public async Task Monitoring_disabled_opens_no_socket_and_reports_disabled()
    {
        using var app = new CustomApp("Development");
        var client = await app.LoginAsync();
        var status = (await client.GetFromJsonAsync<MonitoringStatusDto>("/api/monitoring/status", BosunJson.Options))!;
        status.Should().Match<MonitoringStatusDto>(s => !s.Enabled && s.Modbus.State == ConnectionState.Disabled && s.Nmea.State == ConnectionState.Disabled && s.TagCount == 0);
        (await client.GetFromJsonAsync<AlarmListDto>("/api/monitoring/alarms", BosunJson.Options))!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_batch_writer_and_change_set_applies_share_the_database_without_lock_errors()
    {
        using var app = new CustomApp("Development", new Dictionary<string, string?> { ["Monitoring:AlarmFlushIntervalMs"] = "5", ["Monitoring:AlarmBatchSize"] = "40" });
        var client = await app.LoginAsync();
        var writer = app.Services.GetRequiredService<AlarmEventWriter>();
        const int alarmCount = 600;

        var producing = Task.Run(async () =>
        {
            var at = DateTime.UtcNow;
            for (var id = 1; id <= alarmCount; id++)
            {
                var alarm = new Alarm { Id = id, ProjectId = 1, TagName = "ME-EXH-TT03", Level = AlarmLevel.H, LimitValue = 95, State = AlarmState.Unacknowledged, RaisedAt = at, RaisedValue = 96, PeakValue = 96 };
                writer.Enqueue(new AlarmTransition(AlarmEventKind.Raised, alarm, at, 96, null, null));
                if (id % 20 == 0)
                {
                    await Task.Delay(1);
                }
            }
        });

        // Applies run while the writer is flushing: every one must succeed (a 'database is locked' would surface as a 500).
        for (var i = 0; i < 6; i++)
        {
            await SetLimit(client, "ME-EXH-TT03", 90 + i, delay: 10, hysteresis: 1);
        }

        await producing;
        await writer.FlushAsync(CancellationToken.None);
        writer.EventsWritten.Should().Be(alarmCount);
        writer.BatchesWritten.Should().BeGreaterThan(1).And.BeLessThan(alarmCount, "events are written in batches, not one by one");

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
        (await db.Alarms.CountAsync()).Should().Be(alarmCount);
        (await db.AlarmEvents.CountAsync()).Should().Be(alarmCount);
        var tag = await client.TagAsync("ME-EXH-TT03");
        tag.AlarmLimits.Should().Contain(l => l.Level == AlarmLevel.H && l.Value == 95);
    }

    [Fact]
    public async Task Mimic_is_served_with_bindings_that_all_exist_in_the_project()
    {
        using var app = new CustomApp("Development");
        var client = await app.LoginAsync();
        var mimic = JsonDocument.Parse(await (await client.GetAsync("/api/projects/1/mimic")).Content.ReadAsStringAsync()).RootElement;
        mimic.GetProperty("missingTags").GetArrayLength().Should().Be(0, "mimic.json may only reference tags that exist in the seed");
        var bindings = mimic.GetProperty("bindings").EnumerateArray().Where(b => b.TryGetProperty("tag", out _)).Select(b => b.GetProperty("tag").GetString()!).ToList();
        bindings.Should().Contain(Enumerable.Range(1, 8).Select(i => $"ME-EXH-TT{i:00}"));
        var tags = mimic.GetProperty("tags");
        bindings.Where(t => !tags.TryGetProperty(t, out _)).Should().BeEmpty();
        mimic.GetProperty("shapes").GetArrayLength().Should().BeGreaterThan(10);

        (await (await client.GetAsync("/api/projects/9999/mimic")).ReadProblemAsync(404)).Code.Should().Be("not_found");
    }
}
