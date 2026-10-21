namespace Bosun.Simulator;

/// <summary>
/// The scenario's own clock. Scenario seconds run <c>TimeScale</c> times faster than wall-clock seconds, so a test or a demo
/// can see "t+90 s" after a second or two. Reset and skip move the origin; nothing else about the plant changes.
/// </summary>
public sealed class ScenarioClock(TimeProvider time, double timeScale = 1.0, double startAtSeconds = 0)
{
    private readonly object gate = new();
    private long originTimestamp = time.GetTimestamp();
    private double offsetSeconds = startAtSeconds;
    private double scale = Validate(timeScale);

    public double TimeScale
    {
        get
        {
            lock (gate)
            {
                return scale;
            }
        }
    }

    /// <summary>Scenario seconds since the start (or since the last reset), including skips.</summary>
    public double Elapsed
    {
        get
        {
            lock (gate)
            {
                return offsetSeconds + (time.GetElapsedTime(originTimestamp).TotalSeconds * scale);
            }
        }
    }

    /// <summary>Back to t = 0 (a normal run again).</summary>
    public void Reset() => SetElapsed(0);

    /// <summary>Jumps to an absolute scenario time.</summary>
    public void SkipTo(double seconds) => SetElapsed(Math.Max(0, seconds));

    /// <summary>Jumps forward (or back, when negative) by <paramref name="seconds"/> of scenario time.</summary>
    public void Skip(double seconds) => SetElapsed(Math.Max(0, Elapsed + seconds));

    /// <summary>Changes the speed without moving the current scenario time.</summary>
    public void SetTimeScale(double newScale)
    {
        var now = Elapsed;
        lock (gate)
        {
            scale = Validate(newScale);
            offsetSeconds = now;
            originTimestamp = time.GetTimestamp();
        }
    }

    private void SetElapsed(double seconds)
    {
        lock (gate)
        {
            offsetSeconds = seconds;
            originTimestamp = time.GetTimestamp();
        }
    }

    private static double Validate(double value) =>
        double.IsFinite(value) && value > 0 && value <= 10_000 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Time scale must be within (0, 10000].");
}
