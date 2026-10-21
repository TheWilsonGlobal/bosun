using System.Net;
using Bosun.Protocols.Modbus;
using Microsoft.Extensions.Logging;

namespace Bosun.Simulator;

public sealed record SimulatorOptions
{
    /// <summary>Loopback only (127.0.0.1, never "localhost": the IPv6 fallback costs ~215 ms per connect on this machine).</summary>
    public string Host { get; init; } = "127.0.0.1";

    public int ModbusPort { get; init; } = 5483;

    public int NmeaPort { get; init; } = 5484;

    /// <summary>Scenario seconds per wall-clock second (SIM_TIME_SCALE). 1 = real time; 30 puts t+90 s at 3 s.</summary>
    public double TimeScale { get; init; } = 1.0;

    /// <summary>Scenario time the run starts at (skip the first minute: <c>--start-at 60</c>).</summary>
    public double StartAtSeconds { get; init; }

    public bool NmeaEnabled { get; init; } = true;

    /// <summary>Pause between NMEA sentence pairs. 1 s = 1 Hz as specified; tests shorten it.</summary>
    public TimeSpan NmeaInterval { get; init; } = TimeSpan.FromSeconds(1);

    public string? RegisterMapPath { get; init; }
}

/// <summary>The whole simulated engine room: plant model + Modbus server + NMEA talker, all on one scenario clock.</summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    private readonly ModbusTcpServer modbus;
    private readonly NmeaTalker? nmea;

    public SimulatorHost(SimulatorOptions options, TimeProvider? time = null, ILoggerFactory? loggers = null)
    {
        time ??= TimeProvider.System;
        var path = options.RegisterMapPath
            ?? RegisterMap.FindDefaultFile(AppContext.BaseDirectory, Directory.GetCurrentDirectory())
            ?? throw new FileNotFoundException("apps/simulator/mapping/registers.json was not found (set --map or run inside the repository).");
        Map = RegisterMap.Load(path);
        Clock = new ScenarioClock(time, options.TimeScale, options.StartAtSeconds);
        Plant = new PlantModel(Map, Clock);
        var address = IPAddress.Parse(options.Host);
        modbus = new ModbusTcpServer(Plant.Snapshot, address, options.ModbusPort, loggers?.CreateLogger("Modbus"));
        if (options.NmeaEnabled)
        {
            nmea = new NmeaTalker(Clock, time, address, options.NmeaPort, options.NmeaInterval, loggers?.CreateLogger("Nmea"));
        }
    }

    public RegisterMap Map { get; }

    public ScenarioClock Clock { get; }

    public PlantModel Plant { get; }

    public ModbusTcpServer Modbus => modbus;

    public NmeaTalker? Nmea => nmea;

    /// <summary>Bound Modbus port (the real one when the option was 0).</summary>
    public int ModbusPort => modbus.Port;

    public int? NmeaPort => nmea?.Port;

    public void Start()
    {
        modbus.Start();
        nmea?.Start();
    }

    public async Task StopAsync()
    {
        await modbus.StopAsync();
        if (nmea is not null)
        {
            await nmea.StopAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await modbus.DisposeAsync();
        if (nmea is not null)
        {
            await nmea.DisposeAsync();
        }
    }
}
