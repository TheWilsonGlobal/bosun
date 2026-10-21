using Bosun.Application.Monitoring;
using Bosun.Protocols;
using Bosun.Protocols.Modbus;
using Bosun.Protocols.Nmea;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bosun.Infrastructure.Monitoring;

/// <summary>
/// Connects to the NMEA 0183 talker, validates every sentence (XOR checksum) and keeps the ship's position, speed, course and
/// heading current in <see cref="MonitoringState"/>. Sentences with a bad checksum, or cut off, are counted and dropped - they are
/// never used. Like the Modbus poller it survives a dead talker: one log line per outage, a "disconnected" state, backoff reconnects.
/// A connection that stays open but silent for <see cref="NmeaOptions.StaleAfterSeconds"/> is treated as dead.
/// </summary>
public sealed class NmeaListener(MonitoringOptions options, MonitoringState state, TimeProvider time, ILogger<NmeaListener> logger) : BackgroundService
{
    private bool reportedDown;

    /// <summary>Sentences accepted / rejected (bad checksum, truncated, malformed) since start.</summary>
    public long Accepted { get; private set; }

    public long Rejected { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nmea = options.Nmea;
        if (!options.Enabled || !nmea.Enabled)
        {
            await state.SetConnectionAsync(MonitoringState.NmeaName, ConnectionState.Disabled);
            return;
        }

        var endpoint = $"{nmea.Host}:{nmea.Port}";
        var policy = new ReconnectPolicy(TimeSpan.FromMilliseconds(nmea.ReconnectInitialMs), TimeSpan.FromMilliseconds(nmea.ReconnectMaxMs));
        var connector = new TcpStreamConnector(nmea.Host, nmea.Port, TimeSpan.FromMilliseconds(nmea.ConnectTimeoutMs));
        await state.SetConnectionAsync(MonitoringState.NmeaName, ConnectionState.Connecting, endpoint: endpoint);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var stream = await connector.ConnectAsync(stoppingToken);
                await ReadAsync(stream, policy, endpoint, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var delay = policy.NextDelay();
                if (!reportedDown)
                {
                    logger.LogWarning("NMEA link to {Endpoint} is down ({Reason}); retrying with backoff up to {Max} ms", endpoint, ex.Message, nmea.ReconnectMaxMs);
                    reportedDown = true;
                }
                else
                {
                    logger.LogDebug("NMEA reconnect {Attempt} to {Endpoint} failed: {Reason}", policy.Attempts, endpoint, ex.Message);
                }

                await state.UpdateNavigationAsync(current => (current ?? new NavigationDto(false, null, null, null, null, null, null)) with { Valid = false });
                await state.SetConnectionAsync(MonitoringState.NmeaName, ConnectionState.Disconnected, ex.Message, policy.Attempts, endpoint);
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

    private async Task ReadAsync(Stream stream, ReconnectPolicy policy, string endpoint, CancellationToken stop)
    {
        var reader = new NmeaLineReader(stream);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(1, options.Nmea.StaleAfterSeconds));
        var announced = false;
        while (true)
        {
            string? line;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                silence.CancelAfter(staleAfter);
                try
                {
                    line = await reader.ReadLineAsync(silence.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    throw new TimeoutException($"no NMEA sentence for {staleAfter.TotalSeconds:0} s");
                }
            }

            if (line is null)
            {
                throw new IOException("the NMEA talker closed the connection");
            }

            if (!Handle(line, out var progressed))
            {
                continue;
            }

            if (progressed && !announced)
            {
                announced = true;
                policy.Reset();
                await state.SetConnectionAsync(MonitoringState.NmeaName, ConnectionState.Connected, null, 0, endpoint);
                logger.LogInformation(reportedDown ? "NMEA link to {Endpoint} restored" : "NMEA link to {Endpoint} established", endpoint);
                reportedDown = false;
            }

            await PublishAsync();
        }
    }

    private NavigationDto? pending;

    private async Task PublishAsync()
    {
        var next = pending;
        pending = null;
        if (next is not null)
        {
            await state.UpdateNavigationAsync(_ => next);
        }
    }

    /// <summary>Validates and applies one line. False when the line was rejected.</summary>
    private bool Handle(string line, out bool progressed)
    {
        progressed = false;
        NmeaSentence sentence;
        try
        {
            sentence = NmeaParser.Parse(line);
        }
        catch (NmeaException ex)
        {
            Rejected++;
            if (Rejected is 1 or 10 or 100 or 1000)
            {
                logger.LogWarning("Dropped NMEA sentence #{Count} ({Reason}): {Message}", Rejected, ex.Error, ex.Message);
            }

            return false;
        }

        try
        {
            var current = pending ?? state.Navigation ?? new NavigationDto(false, null, null, null, null, null, null);
            switch (sentence.Type)
            {
                case "RMC":
                    var rmc = NmeaParser.ParseRmc(sentence);
                    pending = current with
                    {
                        Valid = rmc.Valid,
                        Latitude = rmc.Latitude,
                        Longitude = rmc.Longitude,
                        SpeedKnots = rmc.SpeedKnots,
                        CourseDeg = rmc.CourseTrueDeg,
                        Timestamp = rmc.TimeUtc,
                    };
                    break;
                case "HDT":
                    pending = current with { HeadingDeg = NmeaParser.ParseHdt(sentence).HeadingTrueDeg };
                    break;
                default:
                    return true; // valid but not one we use (GGA, VTG ...)
            }
        }
        catch (NmeaException ex)
        {
            Rejected++;
            logger.LogDebug("Dropped NMEA {Type}: {Message}", sentence.Type, ex.Message);
            return false;
        }

        Accepted++;
        progressed = true;
        return true;
    }
}
