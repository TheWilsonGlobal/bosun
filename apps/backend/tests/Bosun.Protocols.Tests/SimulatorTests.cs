using System.Net.Sockets;
using System.Text;
using Bosun.Protocols.Modbus;
using Bosun.Protocols.Nmea;
using Bosun.Simulator;

namespace Bosun.Protocols.Tests;

public class ScenarioTests
{
    [Fact]
    public void Clock_scales_resets_and_skips()
    {
        var time = new ManualTime();
        var clock = new ScenarioClock(time, timeScale: 30);

        time.Advance(TimeSpan.FromSeconds(2));
        clock.Elapsed.Should().BeApproximately(60, 0.01);

        clock.Skip(25);
        clock.Elapsed.Should().BeApproximately(85, 0.01);
        clock.SkipTo(90);
        clock.Elapsed.Should().BeApproximately(90, 0.01);

        clock.SetTimeScale(1);
        time.Advance(TimeSpan.FromSeconds(5));
        clock.Elapsed.Should().BeApproximately(95, 0.01);

        clock.Reset();
        clock.Elapsed.Should().BeApproximately(0, 0.01);
        new ScenarioClock(time, 1, startAtSeconds: 60).Elapsed.Should().BeApproximately(60, 0.01);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(1e9)]
    public void Invalid_time_scales_are_refused(double scale)
    {
        var act = () => new ScenarioClock(new ManualTime(), scale);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Exhaust_rise_follows_the_script()
    {
        PlantModel.ExhaustRise(0).Should().Be(0);
        PlantModel.ExhaustRise(59.9).Should().Be(0);
        PlantModel.ExhaustRise(90).Should().Be(15);
        PlantModel.ExhaustRise(120).Should().Be(30);
        PlantModel.ExhaustRise(200).Should().Be(30);
        PlantModel.ExhaustRise(270).Should().Be(15);
        PlantModel.ExhaustRise(300).Should().Be(0);
        PlantModel.ExhaustRise(1000).Should().Be(0);
    }

    [Fact]
    public void Tt01_crosses_95_degrees_at_ninety_seconds_and_the_rest_follow()
    {
        var map = RegisterMap.Load(RegisterMap.FindDefaultFile(AppContext.BaseDirectory)!);
        var plant = new PlantModel(map, new ScenarioClock(new ManualTime()));
        var tt01 = map.ByTag["ME-EXH-TT01"];

        plant.Value(tt01, 80).Should().BeLessThan(95);
        plant.Value(tt01, 88).Should().BeLessThan(95);
        plant.Value(tt01, 92).Should().BeGreaterThan(95);
        plant.Value(tt01, 150).Should().BeGreaterThan(105);
        plant.Value(tt01, 400).Should().BeLessThan(95, "the scenario ends back at a normal run");

        foreach (var entry in map.Registers.Where(r => r.Tag.StartsWith("ME-EXH-TT", StringComparison.Ordinal)))
        {
            plant.Value(entry, 30).Should().BeInRange(70, 90, entry.Tag);
            plant.Value(entry, 150).Should().BeGreaterThan(95, entry.Tag);
        }
    }

    [Fact]
    public void Snapshot_quantises_to_the_register_scale()
    {
        var map = RegisterMap.Load(RegisterMap.FindDefaultFile(AppContext.BaseDirectory)!);
        var clock = new ScenarioClock(new ManualTime());
        var bank = new PlantModel(map, clock).Snapshot();

        bank.Length.Should().Be(map.Size);
        var tt01 = map.ByTag["ME-EXH-TT01"];
        tt01.ToEngineering(bank[tt01.Address]).Should().BeApproximately(80, 0.3);
    }
}

public class ModbusServerRulesTests
{
    private static readonly ushort[] Bank = [10, 20, 30, 40];

    [Fact]
    public void Answers_in_range_reads_for_both_functions()
    {
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(5, 1, 0x03, 1, 2), Bank)).Should().Be("00 05 00 00 00 07 01 03 04 00 14 00 1E");
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(6, 1, 0x04, 0, 1), Bank)).Should().Be("00 06 00 00 00 05 01 04 02 00 0A");
    }

    [Fact]
    public void Range_past_the_bank_is_illegal_data_address_0x02()
    {
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(7, 1, 0x03, 3, 2), Bank)).Should().Be("00 07 00 00 00 03 01 83 02");
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(7, 1, 0x04, 100, 1), Bank)).Should().Be("00 07 00 00 00 03 01 84 02");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public void Bad_quantity_is_illegal_data_value_0x03(int quantity)
    {
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(8, 1, 0x03, 0, (ushort)quantity), Bank)).Should().Be("00 08 00 00 00 03 01 83 03");
    }

    [Theory]
    [InlineData(0x06)] // write single register
    [InlineData(0x10)] // write multiple registers
    [InlineData(0x2B)]
    public void Unsupported_functions_are_illegal_function_0x01(int function)
    {
        var expected = $"00 09 00 00 00 03 01 {function | 0x80:X2} 01";
        Hex.Of(ModbusTcpServer.Respond(new ModbusRequest(9, 1, (byte)function, 0, 0), Bank)).Should().Be(expected);
    }
}

/// <summary>The hand-written client against the real simulator server over real sockets (ephemeral ports).</summary>
public class SimulatorIntegrationTests
{
    private static SimulatorHost StartSimulator(double timeScale = 1, double startAt = 0, TimeSpan? nmeaInterval = null)
    {
        var host = new SimulatorHost(new SimulatorOptions { ModbusPort = 0, NmeaPort = 0, TimeScale = timeScale, StartAtSeconds = startAt, NmeaInterval = nmeaInterval ?? TimeSpan.FromMilliseconds(40) });
        host.Start();
        return host;
    }

    private static async Task<ModbusClient> ClientAsync(SimulatorHost host)
    {
        var client = new ModbusClient(new TcpStreamConnector("127.0.0.1", host.ModbusPort));
        await client.ConnectAsync(CancellationToken.None);
        return client;
    }

    [Fact]
    public async Task Client_reads_every_mapped_register_through_a_real_socket()
    {
        await using var host = StartSimulator();
        await using var client = await ClientAsync(host);
        var map = host.Map;

        var raw = await client.ReadHoldingRegistersAsync(map.UnitId, 0, (ushort)map.Size, CancellationToken.None);

        raw.Should().HaveCount(map.Size);
        var tt01 = map.ByTag["ME-EXH-TT01"];
        tt01.ToEngineering(raw[tt01.Address]).Should().BeApproximately(80, 0.5);
        var lube = map.ByTag["ME-LUB-PT01"];
        lube.ToEngineering(raw[lube.Address]).Should().BeApproximately(4.5, 0.1);
    }

    [Fact]
    public async Task Function_04_serves_the_same_bank()
    {
        await using var host = StartSimulator();
        await using var client = await ClientAsync(host);

        var holding = await client.ReadHoldingRegistersAsync(1, 0, 8, CancellationToken.None);
        var input = await client.ReadInputRegistersAsync(1, 0, 8, CancellationToken.None);

        input.Should().HaveCount(8);
        for (var i = 0; i < 8; i++)
        {
            Math.Abs(input[i] - holding[i]).Should().BeLessThan(10, "scenario time moves a little between the two reads");
        }
    }

    [Fact]
    public async Task Reading_past_the_bank_returns_exception_0x02_and_the_connection_survives()
    {
        await using var host = StartSimulator();
        await using var client = await ClientAsync(host);

        var act = () => client.ReadHoldingRegistersAsync(1, (ushort)host.Map.Size, 1, CancellationToken.None);

        (await act.Should().ThrowAsync<ModbusServerException>()).Which.Code.Should().Be(ModbusExceptionCode.IllegalDataAddress);
        client.IsConnected.Should().BeTrue();
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task An_unsupported_function_gets_exception_0x01_over_a_raw_socket()
    {
        await using var host = StartSimulator();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", host.ModbusPort);
        var stream = tcp.GetStream();

        await stream.WriteAsync(Hex.Bytes("00 2A 00 00 00 06 01 06 00 01 00 03")); // FC06 write single register
        var frame = await ModbusFraming.ReadFrameAsync(stream, CancellationToken.None);

        frame!.Value.Header.TransactionId.Should().Be(0x2A);
        Hex.Of(frame.Value.Pdu).Should().Be("86 01");
    }

    [Fact]
    public async Task Two_requests_in_one_segment_are_both_answered_in_order()
    {
        await using var host = StartSimulator();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", host.ModbusPort);
        var stream = tcp.GetStream();
        var both = ModbusFrame.BuildReadRequest(1, 1, ModbusFunction.ReadHoldingRegisters, 0, 1)
            .Concat(ModbusFrame.BuildReadRequest(2, 1, ModbusFunction.ReadHoldingRegisters, 1, 2)).ToArray();

        await stream.WriteAsync(both);
        var first = await ModbusFraming.ReadFrameAsync(stream, CancellationToken.None);
        var second = await ModbusFraming.ReadFrameAsync(stream, CancellationToken.None);

        first!.Value.Header.TransactionId.Should().Be(1);
        second!.Value.Header.TransactionId.Should().Be(2);
        second.Value.Pdu.Length.Should().Be(2 + 4);
    }

    [Fact]
    public async Task A_garbage_frame_closes_that_connection_but_not_the_server()
    {
        await using var host = StartSimulator();
        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync("127.0.0.1", host.ModbusPort);
            await bad.GetStream().WriteAsync(Hex.Bytes("00 01 12 34 00 06 01 03 00 00 00 01")); // protocol id 0x1234
            var buffer = new byte[16];
            var hungUp = false;
            try
            {
                hungUp = await bad.GetStream().ReadAsync(buffer) == 0; // clean close ...
            }
            catch (IOException)
            {
                hungUp = true; // ... or a reset, depending on whether unread bytes were pending
            }

            hungUp.Should().BeTrue("the server hangs up on a malformed frame");
        }

        await using var client = await ClientAsync(host);
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Skipping_the_scenario_makes_the_exhaust_temperature_cross_95()
    {
        await using var host = StartSimulator();
        await using var client = await ClientAsync(host);
        var tt01 = host.Map.ByTag["ME-EXH-TT01"];

        var before = tt01.ToEngineering((await client.ReadHoldingRegistersAsync(1, (ushort)tt01.Address, 1, CancellationToken.None))[0]);
        host.Clock.SkipTo(100);
        var after = tt01.ToEngineering((await client.ReadHoldingRegistersAsync(1, (ushort)tt01.Address, 1, CancellationToken.None))[0]);
        host.Clock.Reset();
        var reset = tt01.ToEngineering((await client.ReadHoldingRegistersAsync(1, (ushort)tt01.Address, 1, CancellationToken.None))[0]);

        before.Should().BeLessThan(95);
        after.Should().BeGreaterThan(95);
        reset.Should().BeLessThan(95);
    }

    [Fact]
    public async Task A_fast_time_scale_reaches_the_event_in_wall_clock_seconds()
    {
        await using var host = StartSimulator(timeScale: 100, startAt: 0);
        await using var client = await ClientAsync(host);
        var tt01 = host.Map.ByTag["ME-EXH-TT01"];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        double value = 0;

        while (DateTime.UtcNow < deadline && value <= 95)
        {
            value = tt01.ToEngineering((await client.ReadHoldingRegistersAsync(1, (ushort)tt01.Address, 1, CancellationToken.None))[0]);
            await Task.Delay(50);
        }

        value.Should().BeGreaterThan(95, "x100 puts t+90 s at 0.9 s");
    }

    [Fact]
    public async Task Stopping_the_server_makes_the_client_fail_and_a_restart_on_the_same_port_works()
    {
        var host = StartSimulator();
        var port = host.ModbusPort;
        await using var client = new ModbusClient(new TcpStreamConnector("127.0.0.1", port), new ModbusClientOptions { ResponseTimeout = TimeSpan.FromSeconds(1) });
        await client.ConnectAsync(CancellationToken.None);
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().HaveCount(1);

        await host.DisposeAsync();
        var down = () => client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None);
        await down.Should().ThrowAsync<ModbusException>();
        client.IsConnected.Should().BeFalse();
        var refused = () => client.ConnectAsync(CancellationToken.None);
        await refused.Should().ThrowAsync<ModbusConnectionException>();

        await using var again = new SimulatorHost(new SimulatorOptions { ModbusPort = port, NmeaPort = 0 });
        again.Start();
        await client.ConnectAsync(CancellationToken.None);
        (await client.ReadHoldingRegistersAsync(1, 0, 1, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Nmea_talker_sends_valid_rmc_and_hdt_sentences_with_crlf()
    {
        await using var host = StartSimulator();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", host.NmeaPort!.Value);
        var reader = new NmeaLineReader(tcp.GetStream());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var lines = new List<string>();
        while (lines.Count < 6)
        {
            lines.Add((await reader.ReadLineAsync(timeout.Token))!);
        }

        var sentences = lines.Select(NmeaParser.Parse).ToList();
        sentences.Select(s => s.Type).Distinct().Should().BeEquivalentTo("RMC", "HDT");
        var rmc = NmeaParser.ParseRmc(sentences.First(s => s.Type == "RMC"));
        rmc.Valid.Should().BeTrue();
        rmc.Latitude.Should().BeApproximately(NmeaTalker.StartLatitude, 0.05);
        rmc.Longitude.Should().BeApproximately(NmeaTalker.StartLongitude, 0.05);
        NmeaParser.ParseHdt(sentences.First(s => s.Type == "HDT")).HeadingTrueDeg.Should().BeInRange(43, 47);
    }

    [Fact]
    public void Talker_sentences_are_byte_valid_and_ship_moves_north_east()
    {
        var utc = new DateTime(2026, 10, 21, 8, 30, 15, DateTimeKind.Utc);
        var (rmc0, hdt0) = NmeaTalker.Sentences(0, utc);
        var (rmc1, _) = NmeaTalker.Sentences(3600, utc);

        rmc0.Should().StartWith("$GPRMC,083015.00,A,2057.0000,N,10703.0000,E,");
        hdt0.Should().StartWith("$HEHDT,");
        var start = NmeaParser.ParseRmc(NmeaParser.Parse(rmc0));
        var hourLater = NmeaParser.ParseRmc(NmeaParser.Parse(rmc1));
        hourLater.Latitude.Should().BeGreaterThan(start.Latitude!.Value);
        hourLater.Longitude.Should().BeGreaterThan(start.Longitude!.Value);
        Encoding.ASCII.GetBytes(rmc0).Length.Should().BeLessThan(83);
    }

    [Fact]
    public void Coordinate_formatting_carries_rounded_minutes()
    {
        NmeaTalker.Coordinate(20.95, 2).Should().Be("2057.0000");
        NmeaTalker.Coordinate(107.05, 3).Should().Be("10703.0000");
        NmeaTalker.Coordinate(19.99999999, 2).Should().Be("2000.0000");
    }
}
