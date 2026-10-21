using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bosun.Protocols.Nmea;
using Microsoft.Extensions.Logging;

namespace Bosun.Simulator;

/// <summary>
/// NMEA 0183 talker: every client that connects receives <c>$GPRMC</c> and <c>$HEHDT</c> once per <c>interval</c> (1 s, i.e. 1 Hz,
/// by default), CR LF terminated, with valid checksums. The ship steers 045 degrees at about 12 knots from a point in Ha Long Bay;
/// the position advances with scenario time, so a fast time scale also makes the ship sail faster.
/// </summary>
public sealed class NmeaTalker(ScenarioClock clock, TimeProvider time, IPAddress address, int port, TimeSpan? interval = null, ILogger? logger = null) : IAsyncDisposable
{
    public const double StartLatitude = 20.95;
    public const double StartLongitude = 107.05;
    public const double CourseDegrees = 45.0;
    public const double SpeedKnots = 12.0;

    private readonly TimeSpan interval = interval ?? TimeSpan.FromSeconds(1);
    private readonly TcpListener listener = new(address, port);
    private readonly CancellationTokenSource stop = new();
    private readonly List<TcpClient> clients = [];
    private readonly object gate = new();
    private Task? acceptLoop;
    private Task? talkLoop;

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int ActiveConnections
    {
        get
        {
            lock (gate)
            {
                return clients.Count;
            }
        }
    }

    public void Start()
    {
        listener.Start();
        acceptLoop = Task.Run(AcceptLoopAsync);
        talkLoop = Task.Run(TalkLoopAsync);
        logger?.LogInformation("NMEA 0183 talker listening on {Endpoint} ({Interval} ms per sentence pair)", listener.LocalEndpoint, interval.TotalMilliseconds);
    }

    private bool stopped;

    public async Task StopAsync()
    {
        if (stopped)
        {
            return;
        }

        stopped = true;
        await stop.CancelAsync();
        listener.Stop();
        lock (gate)
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }

            clients.Clear();
        }

        await Task.WhenAll(acceptLoop ?? Task.CompletedTask, talkLoop ?? Task.CompletedTask);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        stop.Dispose();
    }

    /// <summary>The two sentences (no line terminator) for the given scenario time and wall-clock UTC time.</summary>
    public static (string Rmc, string Hdt) Sentences(double scenarioSeconds, DateTime utcNow)
    {
        var distanceNm = SpeedKnots * scenarioSeconds / 3600.0;
        var course = CourseDegrees * Math.PI / 180.0;
        var lat = StartLatitude + (Math.Cos(course) * distanceNm / 60.0);
        var lon = StartLongitude + (Math.Sin(course) * distanceNm / (60.0 * Math.Cos(lat * Math.PI / 180.0)));
        var speed = SpeedKnots + (0.3 * Math.Sin(scenarioSeconds / 11.0));
        var heading = CourseDegrees + (1.5 * Math.Sin(scenarioSeconds / 20.0));

        var inv = CultureInfo.InvariantCulture;
        var rmc = NmeaParser.Build(string.Create(inv,
            $"GPRMC,{utcNow:HHmmss}.00,A,{Coordinate(lat, 2)},{(lat >= 0 ? 'N' : 'S')},{Coordinate(lon, 3)},{(lon >= 0 ? 'E' : 'W')},{speed:000.0},{heading:000.0},{utcNow:ddMMyy},,,A"));
        var hdt = NmeaParser.Build(string.Create(inv, $"HEHDT,{heading:000.0},T"));
        return (rmc, hdt);
    }

    /// <summary>Decimal degrees to ddmm.mmmm (latitude, 2 degree digits) or dddmm.mmmm (longitude, 3).</summary>
    public static string Coordinate(double decimalDegrees, int degreeDigits)
    {
        var abs = Math.Abs(decimalDegrees);
        var degrees = (int)Math.Floor(abs);
        var minutes = Math.Round((abs - degrees) * 60.0, 4);
        if (minutes >= 60.0)
        {
            degrees++;
            minutes = 0;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{degrees.ToString(new string('0', degreeDigits), CultureInfo.InvariantCulture)}{minutes:00.0000}");
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                client.NoDelay = true;
                lock (gate)
                {
                    clients.Add(client);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
        }
    }

    private async Task TalkLoopAsync()
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                var (rmc, hdt) = Sentences(clock.Elapsed, time.GetUtcNow().UtcDateTime);
                var bytes = Encoding.ASCII.GetBytes($"{rmc}\r\n{hdt}\r\n");
                TcpClient[] snapshot;
                lock (gate)
                {
                    snapshot = [.. clients];
                }

                foreach (var client in snapshot)
                {
                    try
                    {
                        await client.GetStream().WriteAsync(bytes, stop.Token);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
                    {
                        lock (gate)
                        {
                            clients.Remove(client);
                        }

                        client.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
