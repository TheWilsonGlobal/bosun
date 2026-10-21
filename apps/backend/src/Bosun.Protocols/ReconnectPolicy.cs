namespace Bosun.Protocols;

/// <summary>
/// Exponential backoff with jitter for reconnecting to a device that went away. Kept apart from the Modbus client so the
/// timing rule can be tested (and reused by the NMEA listener) without any socket.
/// <para>delay(n) = min(max, initial * factor^n) * (1 + jitter * (2r - 1)), r uniform in [0,1), never above <c>max</c>.</para>
/// Call <see cref="NextDelay"/> after each failed attempt and <see cref="Reset"/> after a success.
/// </summary>
public sealed class ReconnectPolicy
{
    private readonly TimeSpan initial;
    private readonly TimeSpan max;
    private readonly double factor;
    private readonly double jitter;
    private readonly Func<double> random;

    public ReconnectPolicy(TimeSpan? initial = null, TimeSpan? max = null, double factor = 2.0, double jitter = 0.25, Func<double>? random = null)
    {
        this.initial = initial ?? TimeSpan.FromMilliseconds(500);
        this.max = max ?? TimeSpan.FromSeconds(15);
        if (this.initial <= TimeSpan.Zero || this.max < this.initial)
        {
            throw new ArgumentException("initial must be positive and max must not be below initial.");
        }

        if (factor < 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "factor must be at least 1.");
        }

        if (jitter is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(jitter), "jitter must be within 0..1.");
        }

        this.factor = factor;
        this.jitter = jitter;
        this.random = random ?? Random.Shared.NextDouble;
    }

    /// <summary>Failed attempts since the last <see cref="Reset"/>.</summary>
    public int Attempts { get; private set; }

    public TimeSpan NextDelay()
    {
        var exponent = Math.Min(Attempts, 60);
        var raw = initial.TotalMilliseconds * Math.Pow(factor, exponent);
        var capped = Math.Min(raw, max.TotalMilliseconds);
        var jittered = capped * (1 + (jitter * ((2 * random()) - 1)));
        Attempts++;
        return TimeSpan.FromMilliseconds(Math.Min(jittered, max.TotalMilliseconds));
    }

    public void Reset() => Attempts = 0;
}
