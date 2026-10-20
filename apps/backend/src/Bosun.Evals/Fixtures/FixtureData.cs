namespace Bosun.Evals.Fixtures;

/// <summary>One tag as the ground truth knows it: canonical (normalised) values, exactly what a perfect extraction returns.</summary>
public sealed record TruthRow(
    string TagName,
    string? Description,
    string? SignalType,
    string? Unit,
    double? RangeLow,
    double? RangeHigh,
    double? AlarmHh,
    double? AlarmH,
    double? AlarmL,
    double? AlarmLl,
    double? DelayS)
{
    public int Page { get; set; }

    /// <summary>"text" (read from the text layer) or "scanned" (read from a page image).</summary>
    public string Kind { get; set; } = "text";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnalog => SignalType is "AI" or "AO";
}

/// <summary>A kind of sensor of one piece of equipment, from which tags are generated.</summary>
internal sealed record Sensor(string Meas, int Count, string Description, string? Unit, double Low, double High, string Limits, string Type = "AI");

internal sealed record Equipment(string System, string Code, Sensor[] Sensors);

/// <summary>
/// The synthetic tag pool of the extraction fixtures: marine tags in the project's naming convention
/// <c>{SYSTEM}-{EQUIP}-{MEASURE}{NN}</c> on equipment the demo project does not have (so an upload does not collide with the seed),
/// with plausible ranges, units and ordered alarm limits. Everything comes from a fixed seed: regenerating gives the same pool.
/// </summary>
internal static class FixtureData
{
    public const int Seed = 20261020;

    private static readonly Equipment[] Bank =
    [
        new("ME", "SCR", [
            new("TT", 4, "SCR catalyst bed {n} temperature", "°C", 0, 500, "H,HH"),
            new("PT", 2, "SCR catalyst differential pressure {n}", "mbar", 0, 300, "H"),
            new("AT", 2, "NOx analyser reading {n}", "ppm", 0, 2000, "H,HH"),
            new("FT", 1, "Urea dosing flow", "L/h", 0, 120, "LL,L"),
            new("XS", 3, "Urea pump {n} running", null, 0, 0, "", "DI"),
        ]),
        new("ME", "EGR", [
            new("TT", 3, "EGR cooler outlet temperature {n}", "°C", 0, 400, "H,HH"),
            new("PT", 2, "EGR scrubber pressure {n}", "bar", 0, 5, "L,H"),
            new("ST", 1, "EGR blower speed", "rpm", 0, 12000, "H,HH"),
            new("ZT", 2, "EGR valve {n} position", "%", 0, 100, ""),
            new("YC", 2, "EGR valve {n} setpoint", "%", 0, 100, "", "AO"),
        ]),
        new("ME", "WHR", [
            new("TT", 4, "Waste heat steam temperature {n}", "°C", 0, 450, "H,HH"),
            new("PT", 3, "Waste heat steam pressure {n}", "bar", 0, 25, "L,H,HH"),
            new("LT", 2, "WHR drum level {n}", "%", 0, 100, "LL,L,H,HH"),
            new("FT", 2, "WHR feed water flow {n}", "m3/h", 0, 60.5, "LL,H"),
            new("XA", 2, "WHR safety valve {n} alarm", null, 0, 0, "", "DI"),
        ]),
        new("ME", "GOV", [
            new("ST", 3, "Governor speed pickup {n}", "rpm", 0, 120, "H,HH"),
            new("PT", 2, "Governor hydraulic pressure {n}", "bar", 0, 100, "LL,L"),
            new("II", 1, "Governor actuator current", "A", 0, 20, "H"),
            new("ZT", 2, "Fuel rack feedback {n}", "%", 0, 100, ""),
        ]),
        new("ME", "TAC", [
            new("ST", 2, "Tacho generator {n} speed", "rpm", 0, 150, "H,HH"),
            new("VT", 3, "Turning gear vibration {n}", "mm/s", 0, 50, "H,HH"),
            new("TT", 3, "Turning gear bearing temperature {n}", "°C", 0, 150, "H,HH"),
        ]),
        new("AE", "CLR", [
            new("TT", 4, "Charge air cooler outlet temperature {n}", "°C", 0, 120, "H,HH"),
            new("PT", 2, "Charge air pressure {n}", "bar", 0, 6, "L"),
            new("FT", 1, "Cooler sea water flow", "m3/h", 0, 400, "LL,L"),
            new("XS", 2, "Cooler pump {n} running", null, 0, 0, "", "DI"),
        ]),
        new("AE", "AFS", [
            new("TT", 3, "Auxiliary fuel temperature {n}", "°C", 0, 160, "H,HH"),
            new("PT", 3, "Auxiliary fuel pressure {n}", "bar", 0, 16, "LL,L"),
            new("FT", 2, "Auxiliary fuel consumption {n}", "kg/h", 0, 800, ""),
            new("LT", 1, "Auxiliary day tank level", "%", 0, 100, "LL,L,H"),
        ]),
        new("AE", "GBX", [
            new("TT", 4, "Reduction gear bearing temperature {n}", "°C", 0, 130, "H,HH"),
            new("PT", 2, "Reduction gear oil pressure {n}", "bar", 0, 10, "LL,L"),
            new("VT", 2, "Reduction gear vibration {n}", "mm/s", 0, 30, "H,HH"),
        ]),
        new("BLR", "ECO", [
            new("TT", 4, "Economiser gas temperature {n}", "°C", 0, 400, "H,HH"),
            new("PT", 2, "Economiser water pressure {n}", "bar", 0, 20, "L,H"),
            new("AT", 1, "Flue gas oxygen content", "%", 0, 25, "LL,L"),
            new("FT", 1, "Economiser circulation flow", "t/h", 0, 30, "LL"),
        ]),
        new("BLR", "FDB", [
            new("LT", 3, "Boiler feed tank level {n}", "%", 0, 100, "LL,L,H,HH"),
            new("PT", 2, "Feed pump discharge pressure {n}", "bar", 0, 30, "LL,L,H,HH"),
            new("TT", 2, "Feed water temperature {n}", "°C", 0, 150, "L,H"),
            new("XA", 3, "Feed pump {n} trip", null, 0, 0, "", "DI"),
        ]),
        new("BLR", "BRN", [
            new("TT", 3, "Burner flame temperature {n}", "°C", 0, 1200, "H,HH"),
            new("PT", 2, "Burner atomising pressure {n}", "mbar", 0, 100, "LL,L"),
            new("ST", 2, "Burner fan speed {n}", "rpm", 0, 3600, "H,HH"),
            new("FT", 2, "Burner fuel flow {n}", "kg/h", 0, 1500.5, "H"),
            new("YS", 2, "Burner {n} start command", null, 0, 0, "", "DO"),
        ]),
        new("PMS", "GRB", [
            new("VT", 3, "Main busbar voltage section {n}", "kV", 0, 12, "L,H,HH"),
            new("II", 4, "Generator breaker current {n}", "A", 0, 3500, "H,HH"),
            new("JT", 3, "Generator active power {n}", "kW", 0, 5000, "HH"),
            new("ST", 2, "Busbar frequency {n}", "Hz", 45, 65, "LL,L,H,HH"),
            new("XS", 4, "Generator breaker {n} closed", null, 0, 0, "", "DI"),
        ]),
        new("PMS", "SHR", [
            new("JT", 2, "Shore connection power {n}", "kW", 0, 2500, "HH"),
            new("II", 3, "Shore connection current {n}", "A", 0, 1600, "H,HH"),
            new("VT", 2, "Shore connection voltage {n}", "V", 0, 480, "LL,L,H,HH"),
        ]),
        new("BWTS", "UVR", [
            new("II", 2, "UV reactor lamp current {n}", "A", 0, 30, "L,H"),
            new("TT", 2, "UV reactor water temperature {n}", "°C", 0, 80, "H,HH"),
            new("PT", 3, "UV reactor pressure {n}", "bar", 0, 10, "LL,L,H"),
            new("FT", 2, "Ballast treatment flow {n}", "m3/h", 0, 3000, "H"),
            new("AT", 1, "Residual oxidant", "ppm", 0, 10, "H"),
        ]),
        new("BWTS", "FLT", [
            new("PT", 3, "Filter differential pressure {n}", "bar", 0, 2, "H,HH"),
            new("XS", 3, "Filter {n} backflush active", null, 0, 0, "", "DI"),
            new("ZT", 2, "Filter inlet valve {n} position", "%", 0, 100, ""),
        ]),
        new("TANK", "TK21", [
            new("LT", 3, "Cargo oil tank {t} level {n}", "%", 0, 100, "LL,L,H,HH"),
            new("TT", 2, "Cargo oil tank {t} temperature {n}", "°C", 0, 100, "H"),
            new("PT", 1, "Cargo oil tank {t} vapour pressure", "mbar", -50, 150, "L,H"),
        ]),
        new("TANK", "TK22", [
            new("LT", 3, "Slop tank {t} level {n}", "%", 0, 100, "LL,L,H,HH"),
            new("TT", 2, "Slop tank {t} temperature {n}", "°C", 0, 100, "H"),
            new("PT", 1, "Slop tank {t} vapour pressure", "mbar", -50, 150, "L,H"),
        ]),
        new("TANK", "TK23", [
            new("LT", 4, "Fresh water tank {t} level {n}", "%", 0, 100, "LL,L,H"),
            new("TT", 1, "Fresh water tank {t} temperature", "°C", 0, 60, "H"),
        ]),
        new("TANK", "OWS", [
            new("AT", 2, "Oil content monitor {n}", "ppm", 0, 30, "H"),
            new("LT", 2, "Bilge holding tank level {n}", "%", 0, 100, "L,H,HH"),
            new("FT", 1, "Separator discharge flow", "m3/h", 0, 5, "H"),
            new("XA", 2, "Separator alarm {n}", null, 0, 0, "", "DI"),
        ]),
        new("ME", "HPU", [
            new("PT", 3, "Hydraulic power unit pressure {n}", "bar", 0, 400, "LL,L,H"),
            new("TT", 2, "Hydraulic oil temperature {n}", "°C", 0, 100, "H,HH"),
            new("LT", 1, "Hydraulic oil tank level", "%", 0, 100, "LL,L"),
            new("XS", 4, "Hydraulic pump {n} running", null, 0, 0, "", "DI"),
        ]),
        new("ME", "CPD", [
            new("PT", 4, "Cylinder pressure indicator {n}", "bar", 0, 200, "H"),
            new("TT", 4, "Cylinder liner temperature {n}", "°C", 0, 300, "H,HH"),
            new("VT", 2, "Cylinder block vibration {n}", "mm/s", 0, 50, "H,HH"),
        ]),
        new("AE", "SCV", [
            new("TT", 3, "Scavenge air temperature {n}", "°C", 0, 150, "H,HH"),
            new("PT", 3, "Scavenge air pressure {n}", "bar", 0, 4, "L,H"),
            new("ST", 2, "Turbocharger speed {n}", "rpm", 0, 40000, "H,HH"),
        ]),
        new("BLR", "SPH", [
            new("TT", 3, "Steam superheater outlet temperature {n}", "°C", 0, 450, "H,HH"),
            new("PT", 3, "Steam header pressure {n}", "bar", 0, 25, "L,H,HH"),
            new("FT", 2, "Steam flow {n}", "t/h", 0, 40.5, "H"),
        ]),
        new("PMS", "UPS", [
            new("VT", 3, "UPS battery voltage {n}", "V", 0, 300, "LL,L,H"),
            new("II", 3, "UPS output current {n}", "A", 0, 200, "H"),
            new("TT", 2, "UPS cabinet temperature {n}", "°C", 0, 80, "H,HH"),
            new("XA", 3, "UPS alarm {n}", null, 0, 0, "", "DI"),
        ]),
        new("BWTS", "CHM", [
            new("LT", 2, "Neutralising chemical tank level {n}", "%", 0, 100, "LL,L"),
            new("FT", 2, "Chemical dosing flow {n}", "L/h", 0, 250, "LL,H"),
            new("PT", 2, "Chemical dosing pressure {n}", "bar", 0, 10, "H"),
            new("YS", 2, "Dosing pump {n} start", null, 0, 0, "", "DO"),
        ]),
    ];

    /// <summary>All tags of the bank in a fixed, shuffled-by-seed order (so fixtures mix systems instead of walking one machine).</summary>
    public static IReadOnlyList<TruthRow> Pool()
    {
        var rng = new Random(Seed);
        var rows = new List<TruthRow>();
        // three installations of every equipment (engine bank 1/2, tanks 21-23 and 24-26 ...) double the pool
        foreach (var (equipment, copy) in Enumerable.Range(1, 3).SelectMany(copy => Bank.Select(e => (e, copy))))
        {
            var code = equipment.Code.StartsWith("TK", StringComparison.Ordinal)
                ? "TK" + (int.Parse(equipment.Code[2..], System.Globalization.CultureInfo.InvariantCulture) + ((copy - 1) * 3))
                : equipment.Code + copy;
            foreach (var sensor in equipment.Sensors)
            {
                for (var n = 1; n <= sensor.Count; n++)
                {
                    var tag = $"{equipment.System}-{code}-{sensor.Meas}{n:00}";
                    var description = sensor.Description.Replace("{n}", n.ToString(), StringComparison.Ordinal).Replace("{t}", code.StartsWith("TK", StringComparison.Ordinal) ? code[2..] : "", StringComparison.Ordinal).Replace("  ", " ", StringComparison.Ordinal);
                    if (copy >= 2 && !equipment.Code.StartsWith("TK", StringComparison.Ordinal))
                    {
                        description += $" (bank {copy})";
                    }

                    rows.Add(MakeRow(tag, description, sensor, rng));
                }
            }
        }

        return rows;
    }

    /// <summary>Equipment-ordered pool (consecutive rows belong together, as in a real I/O list).</summary>
    public static IReadOnlyList<TruthRow> Take(IReadOnlyList<TruthRow> pool, int start, int count) => [.. pool.Skip(start).Take(count)];

    private static TruthRow MakeRow(string tag, string description, Sensor sensor, Random rng)
    {
        if (sensor.Type is "DI" or "DO")
        {
            return new TruthRow(tag, description, sensor.Type, null, null, null, null, null, null, null, null);
        }

        var span = sensor.High - sensor.Low;
        var decimals = span >= 1000 ? 0 : span >= 100 ? (rng.Next(2) == 0 ? 0 : 1) : span >= 10 ? 1 : 2;
        double Round(double value) => Math.Round(value, decimals);
        double Place(double fraction) => Round(sensor.Low + (span * fraction));

        // limits from fractions of the span with a little jitter, strictly ordered LL < L < H < HH after rounding
        var l = Place(0.15 + (rng.Next(0, 3) * 0.05));
        var ll = Place(0.05 + (rng.Next(0, 3) * 0.03));
        var h = Place(0.75 + (rng.Next(0, 3) * 0.05));
        var hh = Place(0.88 + (rng.Next(0, 3) * 0.04));
        var step = Math.Pow(10, -decimals);
        if (ll >= l)
        {
            ll = Round(l - (step * 2));
        }

        if (hh <= h)
        {
            hh = Round(h + (step * 2));
        }

        var levels = sensor.Limits.Length == 0 ? [] : sensor.Limits.Split(',');
        double? Pick(string level, double value) => levels.Contains(level) ? value : null;
        double? delay = levels.Length == 0 ? null : new[] { 0.0, 2, 3, 5, 5, 10, 10, 15, 30 }[rng.Next(9)];
        return new TruthRow(tag, description, sensor.Type, sensor.Unit, sensor.Low, sensor.High, Pick("HH", hh), Pick("H", h), Pick("L", l), Pick("LL", ll), delay);
    }
}
