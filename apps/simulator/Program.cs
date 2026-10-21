using System.Globalization;
using Bosun.Simulator;
using Microsoft.Extensions.Logging;

// Bosun simulator: a Modbus TCP server (the engine-room PLC) and an NMEA 0183 talker on one scripted scenario clock.
//
//   pnpm run sim [-- options]            options (an option beats the environment, which beats the root .env):
//     --time-scale <x>    scenario seconds per second          (SIM_TIME_SCALE, default 1)
//     --start-at <s>      begin the scenario at t = <s>        (skip the quiet first minute)
//     --modbus-port <p>   (SIM_MODBUS_PORT, default 5483)      --nmea-port <p>  (SIM_NMEA_PORT, default 5484)
//     --map <file>        register map (default apps/simulator/mapping/registers.json)
//     --no-nmea           do not start the NMEA talker
//
//   Commands on standard input while it runs:  status | reset | skip <seconds> | at <seconds> | scale <x> | quit
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Usage: Bosun.Simulator [--time-scale x] [--start-at s] [--modbus-port p] [--nmea-port p] [--map file] [--no-nmea]");
    Console.WriteLine("Stdin commands: status | reset | skip <seconds> | at <seconds> | scale <x> | quit");
    return 0;
}

var env = new SimulatorEnvironment();

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

double Number(string? text, double fallback, string what) =>
    text is null ? fallback
    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value
    : throw new ArgumentException($"{what}: '{text}' is not a number.");

var options = new SimulatorOptions
{
    ModbusPort = (int)Number(Arg("--modbus-port") ?? env.Get("SIM_MODBUS_PORT"), 5483, "modbus port"),
    NmeaPort = (int)Number(Arg("--nmea-port") ?? env.Get("SIM_NMEA_PORT"), 5484, "nmea port"),
    TimeScale = Number(Arg("--time-scale") ?? env.Get("SIM_TIME_SCALE"), 1.0, "time scale"),
    StartAtSeconds = Number(Arg("--start-at"), 0, "start-at"),
    NmeaEnabled = !args.Contains("--no-nmea"),
    RegisterMapPath = Arg("--map"),
};

using var loggers = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.TimestampFormat = "HH:mm:ss.fff ").SetMinimumLevel(LogLevel.Information));
var log = loggers.CreateLogger("Simulator");

SimulatorHost host;
try
{
    host = new SimulatorHost(options, loggers: loggers);
    host.Start();
}
catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or ArgumentException or FileNotFoundException or FormatException)
{
    // Most often: the port is already taken (another simulator, or something else on 5483/5484).
    Console.Error.WriteLine($"Simulator failed to start: {ex.Message}");
    return 1;
}

await using var hostLifetime = host;
log.LogInformation(
    "Simulator running: {Registers} registers, time scale x{Scale}, scenario t={T:0.0}s. Exhaust gas temperatures pass 95 degC at t=90 s.",
    host.Map.Registers.Count, host.Clock.TimeScale, host.Clock.Elapsed);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Commands on stdin. When stdin is closed or not attached (pnpm run dev), the loop just ends and the simulator keeps running.
_ = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        string? line;
        try
        {
            line = await Console.In.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (line is null)
        {
            return;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            continue;
        }

        double? value = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "reset":
                    host.Clock.Reset();
                    log.LogInformation("Scenario reset to t=0");
                    break;
                case "skip" when value is not null:
                    host.Clock.Skip(value.Value);
                    log.LogInformation("Scenario skipped to t={T:0.0}s", host.Clock.Elapsed);
                    break;
                case "at" when value is not null:
                    host.Clock.SkipTo(value.Value);
                    log.LogInformation("Scenario set to t={T:0.0}s", host.Clock.Elapsed);
                    break;
                case "scale" when value is not null:
                    host.Clock.SetTimeScale(value.Value);
                    log.LogInformation("Time scale is now x{Scale}", host.Clock.TimeScale);
                    break;
                case "status":
                    log.LogInformation(
                        "t={T:0.0}s x{Scale}; modbus clients {M}, requests {R}; nmea clients {N}",
                        host.Clock.Elapsed, host.Clock.TimeScale, host.Modbus.ActiveConnections, host.Modbus.RequestsServed, host.Nmea?.ActiveConnections ?? 0);
                    break;
                case "quit" or "exit":
                    await cts.CancelAsync();
                    return;
                default:
                    log.LogWarning("Unknown command '{Line}'. Try: status | reset | skip <s> | at <s> | scale <x> | quit", line);
                    break;
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            log.LogWarning("{Message}", ex.Message);
        }
    }
});

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C or "quit"
}

log.LogInformation("Stopping");
await host.StopAsync();
return 0;
