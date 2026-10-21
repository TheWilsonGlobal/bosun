using Bosun.Protocols.Modbus;

namespace Bosun.Simulator;

/// <summary>
/// The scripted engine room. Every mapped register idles at its <c>sim.nominal</c> with a deterministic wobble (sums of sines, so a
/// run is reproducible), then the scenario overlays the demo event:
/// <code>
///   t &lt; 60 s        normal run
///   60 .. 120 s     main engine exhaust gas temperatures (ME-EXH-TT*) climb 0.5 degC/s: TT01 passes 95 degC at t = 90 s
///   120 .. 240 s    held 30 degC above normal (about 110 degC)
///   240 .. 300 s    cool back to normal (below 95 degC again at about t = 270 s)
///   t &gt; 300 s       normal run
/// </code>
/// The exhaust pressure follows the temperature a little, so the mimic does not look scripted. Times are scenario seconds
/// (see <see cref="ScenarioClock"/>).
/// </summary>
public sealed class PlantModel(RegisterMap map, ScenarioClock clock)
{
    public const double RampStart = 60;
    public const double RampPerSecond = 0.5;
    public const double RampEnd = 120;
    public const double HoldEnd = 240;
    public const double CoolEnd = 300;

    /// <summary>Extra degrees C on the exhaust temperatures at scenario time <paramref name="t"/>.</summary>
    public static double ExhaustRise(double t)
    {
        if (t < RampStart)
        {
            return 0;
        }

        if (t <= RampEnd)
        {
            return (t - RampStart) * RampPerSecond;
        }

        if (t <= HoldEnd)
        {
            return (RampEnd - RampStart) * RampPerSecond;
        }

        return t < CoolEnd ? (RampEnd - RampStart) * RampPerSecond * (CoolEnd - t) / (CoolEnd - HoldEnd) : 0;
    }

    public RegisterMap Map => map;

    /// <summary>Engineering value of one tag now.</summary>
    public double Value(RegisterEntry entry) => Value(entry, clock.Elapsed);

    public double Value(RegisterEntry entry, double t)
    {
        var hint = entry.Sim ?? new SimHint(0);
        var phase = entry.Address * 0.7;
        var wobble = hint.Noise * ((0.6 * Math.Sin((2 * Math.PI * t / 7.0) + phase)) + (0.4 * Math.Sin((2 * Math.PI * t / 2.3) + (2 * phase))));
        var value = hint.Nominal + wobble + (hint.DriftPerMinute * t / 60.0);

        if (entry.Tag.StartsWith("ME-EXH-TT", StringComparison.Ordinal))
        {
            value += ExhaustRise(t);
        }
        else if (entry.Tag == "ME-EXH-PT01")
        {
            value += ExhaustRise(t) * 0.01;
        }

        return value;
    }

    /// <summary>The register bank (index = address) at the current scenario time. Unmapped addresses read 0.</summary>
    public ushort[] Snapshot()
    {
        var t = clock.Elapsed;
        var bank = new ushort[map.Size];
        foreach (var entry in map.Registers)
        {
            bank[entry.Address] = entry.ToRaw(Value(entry, t));
        }

        return bank;
    }
}
