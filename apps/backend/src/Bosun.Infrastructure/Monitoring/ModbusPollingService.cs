using Bosun.Application.Monitoring;
using Bosun.Protocols;
using Bosun.Protocols.Modbus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Infrastructure.Monitoring;

/// <summary>
/// Polls the PLC (the simulator in the demo) over Modbus TCP: reads the registers in the shared register map, scales them to
/// engineering values, stores them in <see cref="LiveValueStore"/> and feeds them to the <see cref="AlarmManager"/>.
/// <para>
/// When the PLC is down the service does NOT crash and does not spam: the first failure of a streak is logged once, the link
/// state flips to "disconnected" (clients are told once), the last values are marked stale, and reconnects follow
/// <see cref="ReconnectPolicy"/> (exponential backoff with jitter). The next successful read flips it back to "connected".
/// </para>
/// </summary>
public sealed class ModbusPollingService(
    RegisterMap map,
    MonitoringOptions options,
    MonitoringState state,
    AlarmManager alarms,
    TimeProvider time,
    ILogger<ModbusPollingService> logger) : BackgroundService
{
    private static readonly TimeSpan AlarmErrorLogInterval = TimeSpan.FromMinutes(1);

    private bool reportedDown;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var modbus = options.Modbus;
        if (!options.Enabled || !modbus.Enabled)
        {
            await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Disabled);
            return;
        }

        var endpoint = $"{modbus.Host}:{modbus.Port}";
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(modbus.ReconnectInitialMs), TimeSpan.FromMilliseconds(modbus.ReconnectMaxMs));
        await using var client = new ModbusClient(
            new TcpStreamConnector(modbus.Host, modbus.Port, TimeSpan.FromMilliseconds(modbus.ConnectTimeoutMs)),
            new ModbusClientOptions { ResponseTimeout = TimeSpan.FromMilliseconds(modbus.ResponseTimeoutMs) });

        await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Connecting, endpoint: endpoint);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(stoppingToken);
                await PollAsync(client, policy, endpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.Values.MarkAllStale();
                var delay = policy.NextDelay();
                if (!reportedDown)
                {
                    logger.LogWarning("Modbus link to {Endpoint} is down ({Reason}); retrying with backoff up to {Max} ms", endpoint, ex.Message, options.Modbus.ReconnectMaxMs);
                    reportedDown = true;
                }
                else
                {
                    logger.LogDebug("Modbus reconnect {Attempt} to {Endpoint} failed: {Reason}", policy.Attempts, endpoint, ex.Message);
                }

                await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Disconnected, ex.Message, policy.Attempts, endpoint);
                try
                {
                    await Task.Delay(delay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Polls until something throws (link failure) or the service stops.</summary>
    private async Task PollAsync(ModbusClient client, ReconnectPolicy policy, string endpoint, CancellationToken stop)
    {
        var blocks = map.Blocks();
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, options.Modbus.PollIntervalMs));
        var announced = false;
        var refusedBlocks = new HashSet<int>();
        var lastAlarmError = DateTime.MinValue;

        while (!stop.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            var now = time.GetUtcNow().UtcDateTime;
            var samples = new List<Sample>(map.Registers.Count);
            foreach (var block in blocks)
            {
                ushort[] raw;
                try
                {
                    raw = await client.ReadAsync(
                        block.Bank == RegisterBank.Input ? ModbusFunction.ReadInputRegisters : ModbusFunction.ReadHoldingRegisters,
                        map.UnitId, (ushort)block.Address, (ushort)block.Count, stop);
                }
                catch (ModbusServerException ex)
                {
                    // The device answered but refused this range: a configuration mismatch, not a dead link.
                    if (refusedBlocks.Add(block.Address))
                    {
                        logger.LogError("Modbus device refused registers {From}..{To}: {Reason}. Check apps/simulator/mapping/registers.json against the device.", block.Address, block.Address + block.Count - 1, ex.Message);
                    }

                    continue;
                }

                foreach (var entry in block.Entries)
                {
                    var value = entry.ToEngineering(raw[entry.Address - block.Address]);
                    state.Values.Set(entry.Tag, value, entry.Unit, now);
                    samples.Add(new Sample(entry.Tag, value, now));
                }

                if (!announced)
                {
                    announced = true;
                    policy.Reset();
                    await state.SetConnectionAsync(MonitoringState.ModbusName, ConnectionState.Connected, null, 0, endpoint);
                    if (reportedDown)
                    {
                        reportedDown = false;
                        logger.LogInformation("Modbus link to {Endpoint} restored", endpoint);
                    }
                    else
                    {
                        logger.LogInformation("Modbus link to {Endpoint} established ({Registers} registers)", endpoint, map.Registers.Count);
                    }
                }
            }

            try
            {
                await alarms.ProcessAsync(samples, stop);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Alarm evaluation (database read) failing must not tear down the Modbus link; say so at most once a minute.
                if (now - lastAlarmError >= AlarmErrorLogInterval)
                {
                    lastAlarmError = now;
                    logger.LogWarning(ex, "Alarm evaluation failed for this poll cycle; values keep flowing");
                }
            }

            var remaining = interval - time.GetElapsedTime(started);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, time, stop);
            }
        }
    }
}
