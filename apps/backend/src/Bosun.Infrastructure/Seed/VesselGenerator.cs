using Bosun.Domain;
using Bosun.Domain.Entities;

namespace Bosun.Infrastructure.Seed;

public sealed record GeneratorOptions(int TargetTags = 5000, int Seed = 20260101);

/// <summary>
/// Deterministic generator for the synthetic "MV Ha Long" project: ~5,000 tags over ME, AE, BLR, PMS, BWTS and
/// TANK, with controllers, I/O modules, channels, alarm groups and pre-existing alarm limits on many tags.
/// The same options always produce the same graph (names, order, limits, which tags are unbound).
/// </summary>
public static class VesselGenerator
{
    public const string ProjectName = "MV Ha Long - Automation Configuration";

    private sealed record Sensor(
        string Measure,
        int Count,
        string Description,
        SignalType Type,
        string? Unit,
        double Low,
        double High,
        double? LL = null,
        double? L = null,
        double? H = null,
        double? HH = null,
        string? Group = null,
        int SafetyCount = 0,
        SafetyClass Safety = SafetyClass.SIL);

    private sealed record Equip(string System, string Code, string Name, int ControllerIndex, IReadOnlyList<Sensor> Sensors);

    private static Sensor AI(string m, int n, string d, string unit, double lo, double hi, double? ll = null, double? l = null, double? h = null,
        double? hh = null, string? group = null, int safety = 0, SafetyClass cls = SafetyClass.SIL) =>
        new(m, n, d, SignalType.AI, unit, lo, hi, ll, l, h, hh, group, safety, cls);

    private static Sensor DI(string m, int n, string d, string? group = null, int safety = 0, SafetyClass cls = SafetyClass.ESD) =>
        new(m, n, d, SignalType.DI, null, 0, 0, Group: group, SafetyCount: safety, Safety: cls);

    private static Sensor DO(string m, int n, string d) => new(m, n, d, SignalType.DO, null, 0, 0);

    private static Sensor AO(string m, int n, string d, string unit, double lo, double hi) => new(m, n, d, SignalType.AO, unit, lo, hi);

    public static Vessel Generate(GeneratorOptions? options = null)
    {
        options ??= new GeneratorOptions();
        var rng = new Random(options.Seed);
        var equipment = BuildEquipment(options.TargetTags);

        var project = new Project
        {
            Name = ProjectName,
            Description = "Synthetic demo project: integrated alarm, monitoring and control configuration of a 8-cylinder bulk carrier.",
            CreatedAt = new DateTime(2026, 1, 15, 8, 0, 0, DateTimeKind.Utc),
        };
        var vessel = new Vessel { Name = "MV Ha Long", ImoNumber = "9876543", Projects = [project] };

        // Alarm groups: everything the templates reference, plus ME-EXH (deliberately empty: the demo fills it).
        var groupNames = equipment.SelectMany(e => e.Sensors).Select(s => s.Group).Where(g => g is not null).Select(g => g!)
            .Append("ME-EXH").Append("ME-GENERAL").Append("COMMON-MISC").Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
        var groups = groupNames.ToDictionary(
            g => g,
            g => new AlarmGroup { Name = g, Description = $"Alarm group {g}" });
        project.AlarmGroups.AddRange(groups.Values);

        var pool = new ChannelPool(project);
        var tagCount = 0;
        foreach (var equip in equipment)
        {
            // Several sensor groups of one equipment can share a measure code (e.g. two TT groups): number them continuously.
            var counters = new Dictionary<string, int>();
            foreach (var sensor in equip.Sensors)
            {
                for (var i = 1; i <= sensor.Count; i++)
                {
                    if (tagCount >= options.TargetTags)
                    {
                        break;
                    }

                    var sequence = counters[sensor.Measure] = counters.GetValueOrDefault(sensor.Measure) + 1;
                    var tag = new Tag
                    {
                        TagName = $"{equip.System}-{equip.Code}-{sensor.Measure}{sequence:00}",
                        Description = $"{equip.Name}: {sensor.Description.Replace("{n}", i.ToString(System.Globalization.CultureInfo.InvariantCulture))}",
                        System = equip.System,
                        Equipment = equip.Code,
                        SignalType = sensor.Type,
                        Unit = sensor.Unit,
                        RangeLow = sensor.Type is SignalType.AI or SignalType.AO ? sensor.Low : null,
                        RangeHigh = sensor.Type is SignalType.AI or SignalType.AO ? sensor.High : null,
                        SafetyClass = i <= sensor.SafetyCount ? sensor.Safety : SafetyClass.None,
                    };

                    if (sensor.Group is { } group)
                    {
                        tag.AlarmGroup = groups[group];
                    }

                    AddLimits(tag, sensor);

                    // ~4% of tags are left unbound so BindChannel has something to do.
                    if (rng.Next(25) != 0)
                    {
                        tag.Channel = pool.Next(equip.System, equip.ControllerIndex, sensor.Type);
                    }

                    project.Tags.Add(tag);
                    tagCount++;
                }
            }
        }

        return vessel;
    }

    private static void AddLimits(Tag tag, Sensor s)
    {
        var hysteresis = Math.Round((s.High - s.Low) * 0.01, 3);
        void Add(AlarmLevel level, double? value, double delay)
        {
            if (value is { } v)
            {
                tag.AlarmLimits.Add(new AlarmLimit { Level = level, Value = v, DelayS = delay, Hysteresis = hysteresis });
            }
        }

        Add(AlarmLevel.LL, s.LL, 2);
        Add(AlarmLevel.L, s.L, 10);
        Add(AlarmLevel.H, s.H, 10);
        Add(AlarmLevel.HH, s.HH, 2);
    }

    /// <summary>Allocates 16-channel modules per controller and signal type, and hands out channels in order.</summary>
    private sealed class ChannelPool(Project project)
    {
        private const int ChannelsPerModule = 16;
        private readonly Dictionary<string, Controller> controllers = [];
        private readonly Dictionary<(Controller, SignalType), Queue<Channel>> free = [];
        private readonly Dictionary<(Controller, SignalType), int> moduleCount = [];

        public Channel Next(string system, int controllerIndex, SignalType type)
        {
            var name = $"PLC-{system}-{controllerIndex}";
            if (!controllers.TryGetValue(name, out var controller))
            {
                controller = new Controller
                {
                    Name = name,
                    Model = "BX-2000",
                    IpAddress = $"10.20.{controllers.Count + 1}.10",
                };
                controllers[name] = controller;
                project.Controllers.Add(controller);
            }

            if (!free.TryGetValue((controller, type), out var queue))
            {
                queue = new Queue<Channel>();
                free[(controller, type)] = queue;
            }

            if (queue.Count == 0)
            {
                var index = moduleCount.GetValueOrDefault((controller, type)) + 1;
                moduleCount[(controller, type)] = index;
                var module = new IoModule
                {
                    Name = $"{type}-{index:00}",
                    Slot = controller.Modules.Count + 1,
                    SignalType = type,
                };
                for (var n = 1; n <= ChannelsPerModule; n++)
                {
                    var channel = new Channel { Number = n, SignalType = type };
                    module.Channels.Add(channel);
                    queue.Enqueue(channel);
                }

                controller.Modules.Add(module);
            }

            return queue.Dequeue();
        }
    }

    private static List<Equip> BuildEquipment(int target)
    {
        var list = new List<Equip>();
        AddMainEngine(list);
        for (var g = 1; g <= 4; g++)
        {
            AddAuxEngine(list, g);
        }

        for (var b = 1; b <= 2; b++)
        {
            AddBoiler(list, b);
        }

        AddPms(list);
        for (var u = 1; u <= 2; u++)
        {
            AddBwts(list, u);
        }

        AddBallastPumpsAndValves(list);

        var used = list.Sum(e => e.Sensors.Sum(s => s.Count));
        var tankNo = 1;
        while (used < target)
        {
            var equip = Tank(tankNo++);
            list.Add(equip);
            used += equip.Sensors.Sum(s => s.Count);
        }

        return list;
    }

    private static void AddMainEngine(List<Equip> l)
    {
        const string S = "ME";
        l.Add(new(S, "EXH", "ME exhaust", 1,
        [
            AI("TT", 8, "Exhaust gas temperature cyl {n}", "°C", 0, 600),
            AI("PT", 1, "Exhaust receiver pressure", "bar", 0, 4),
        ]));
        l.Add(new(S, "JCW", "ME jacket cooling water", 1,
        [
            AI("TT", 8, "Jacket CW outlet temperature cyl {n}", "°C", 0, 120, h: 90, hh: 95, group: "ME-JCW"),
            AI("PT", 2, "Jacket CW pressure {n}", "bar", 0, 6, ll: 1.5, l: 2.0, group: "ME-JCW"),
        ]));
        l.Add(new(S, "PCO", "ME piston cooling oil", 1,
        [
            AI("TT", 8, "Piston cooling oil outlet temperature cyl {n}", "°C", 0, 100, h: 70, hh: 75, group: "ME-PCO"),
            DI("FS", 8, "Piston cooling oil flow switch cyl {n}", "ME-PCO"),
        ]));
        l.Add(new(S, "CYL", "ME cylinders", 1,
        [
            AI("PT", 8, "Maximum firing pressure cyl {n}", "bar", 0, 200),
            AI("TT", 8, "Liner wall temperature cyl {n}", "°C", 0, 300, h: 250, hh: 270, group: "ME-CYL"),
            AI("VT", 8, "Cylinder vibration cyl {n}", "mm/s", 0, 50, h: 30, hh: 40, group: "ME-CYL"),
        ]));
        l.Add(new(S, "BRG", "ME main bearings", 1, [AI("TT", 9, "Main bearing temperature {n}", "°C", 0, 150, h: 85, hh: 95, group: "ME-BRG")]));
        l.Add(new(S, "XHB", "ME crosshead bearings", 1, [AI("TT", 8, "Crosshead bearing temperature cyl {n}", "°C", 0, 150, h: 80, hh: 90, group: "ME-BRG")]));
        l.Add(new(S, "TRB", "ME thrust bearing", 1, [AI("TT", 4, "Thrust pad temperature {n}", "°C", 0, 150, h: 80, hh: 90, group: "ME-BRG")]));
        l.Add(new(S, "SCV", "ME scavenge air", 2,
        [
            AI("TT", 8, "Scavenge air temperature cyl {n}", "°C", 0, 150, h: 70, hh: 90, group: "ME-SCV"),
            AI("PT", 2, "Scavenge air pressure {n}", "bar", 0, 4),
            AI("TT", 2, "Air cooler outlet temperature {n}", "°C", 0, 100),
            DI("XS", 8, "Scavenge fire detector cyl {n}", "ME-SCV"),
        ]));
        l.Add(new(S, "LUB", "ME lubricating oil", 2,
        [
            AI("PT", 6, "Lube oil pressure point {n}", "bar", 0, 8, ll: 1.5, l: 2.5, group: "ME-LUB", safety: 2, cls: SafetyClass.ESD),
            AI("TT", 6, "Lube oil temperature point {n}", "°C", 0, 100, h: 70, hh: 80, group: "ME-LUB"),
            AI("LT", 2, "LO sump level {n}", "%", 0, 100, ll: 20, l: 30, group: "ME-LUB"),
            AI("FT", 2, "LO flow {n}", "m3/h", 0, 100),
            DI("XS", 4, "LO pump {n} running", "ME-LUB"),
        ]));
        l.Add(new(S, "FO", "ME fuel oil", 2,
        [
            AI("PT", 4, "Fuel oil pressure {n}", "bar", 0, 16, ll: 3, l: 5, group: "ME-FUEL"),
            AI("TT", 4, "Fuel oil temperature {n}", "°C", 0, 160, h: 150, hh: 155, group: "ME-FUEL"),
            AI("FT", 2, "Fuel consumption {n}", "kg/h", 0, 3000),
            AI("AT", 1, "Fuel viscosity", "cSt", 0, 30),
            DI("XS", 4, "Fuel booster pump {n} running", "ME-FUEL"),
        ]));
        l.Add(new(S, "TCH", "ME turbochargers", 2,
        [
            AI("ST", 2, "Turbocharger {n} speed", "rpm", 0, 30000, h: 22000, hh: 24000, group: "ME-TCH"),
            AI("TT", 4, "Turbocharger gas inlet temperature {n}", "°C", 0, 700, h: 550, hh: 600, group: "ME-TCH"),
            AI("TT", 2, "Turbocharger gas outlet temperature {n}", "°C", 0, 500),
            AI("PT", 4, "Turbocharger pressure {n}", "bar", 0, 5),
            AI("VT", 2, "Turbocharger vibration {n}", "mm/s", 0, 50, h: 25, hh: 35, group: "ME-TCH"),
        ]));
        l.Add(new(S, "SPD", "ME speed and power", 2,
        [
            AI("ST", 3, "Engine speed {n}", "rpm", 0, 150, h: 110, hh: 120, group: "ME-SPD", safety: 3, cls: SafetyClass.SIL),
            AI("ZT", 8, "Fuel rack position cyl {n}", "%", 0, 100),
            AI("JT", 1, "Shaft power", "kW", 0, 20000),
            AI("JT", 1, "Shaft torque", "kNm", 0, 3000),
        ]));
        l.Add(new(S, "AIR", "ME starting air", 2,
        [
            AI("PT", 4, "Starting air pressure {n}", "bar", 0, 40, ll: 12, l: 18, group: "ME-AIR"),
            DI("XS", 4, "Starting air valve {n} open", "ME-AIR"),
        ]));
        l.Add(new(S, "CTL", "ME control system", 2,
        [
            DI("XS", 24, "Control status {n}", "ME-GENERAL"),
            DI("XA", 16, "Common alarm {n}", "ME-GENERAL"),
            DO("YS", 8, "Command output {n}"),
            AO("YC", 4, "Setpoint output {n}", "%", 0, 100),
        ]));
        for (var c = 1; c <= 8; c++)
        {
            l.Add(new(S, $"C{c}", $"ME cylinder {c} auxiliaries", 2,
            [
                AI("TT", 2, "Fuel valve cooling temperature {n}", "°C", 0, 150, h: 110, hh: 120, group: "ME-CYL"),
                AI("PT", 3, "Cylinder auxiliary pressure {n}", "bar", 0, 20),
                AI("ZT", 2, "Exhaust valve position {n}", "%", 0, 100),
                AI("FT", 1, "Cylinder lube oil flow", "L/h", 0, 10),
                DI("XS", 4, "Valve status {n}", "ME-CYL"),
                DI("XA", 2, "Cylinder alarm {n}", "ME-CYL"),
            ]));
        }

        l.Add(new(S, "ECS", "ME electronic control system", 2,
        [
            DI("XS", 40, "Controller status {n}", "ME-GENERAL"),
            DI("XA", 30, "Controller alarm {n}", "ME-GENERAL"),
            DO("YS", 20, "Controller command {n}"),
            AI("ST", 6, "Auxiliary blower speed {n}", "rpm", 0, 4000, h: 3600, hh: 3800, group: "ME-SCV"),
        ]));
    }

    private static void AddAuxEngine(List<Equip> l, int g)
    {
        const string S = "AE";
        var n = $"AE{g}";
        var c = $"G{g}";
        l.Add(new(S, $"{c}EXH", $"{n} exhaust", 1,
        [
            AI("TT", 6, "Exhaust gas temperature cyl {n}", "°C", 0, 600, h: 480, hh: 520, group: "AE-EXH"),
            AI("PT", 1, "Exhaust receiver pressure", "bar", 0, 4),
        ]));
        l.Add(new(S, $"{c}JCW", $"{n} jacket cooling water", 1,
        [
            AI("TT", 6, "Jacket CW outlet temperature cyl {n}", "°C", 0, 120, h: 90, hh: 95, group: "AE-COOL"),
            AI("TT", 2, "Jacket CW inlet temperature {n}", "°C", 0, 120, h: 85, hh: 92, group: "AE-COOL"),
            AI("PT", 2, "Jacket CW pressure {n}", "bar", 0, 6, ll: 1.2, l: 1.8, group: "AE-COOL"),
        ]));
        l.Add(new(S, $"{c}LUB", $"{n} lubricating oil", 1,
        [
            AI("PT", 3, "Lube oil pressure {n}", "bar", 0, 8, ll: 1.5, l: 2.2, group: "AE-LUB", safety: 1, cls: SafetyClass.ESD),
            AI("TT", 3, "Lube oil temperature {n}", "°C", 0, 100, h: 75, hh: 85, group: "AE-LUB"),
            AI("LT", 1, "Sump level", "%", 0, 100, ll: 20, l: 30, group: "AE-LUB"),
        ]));
        l.Add(new(S, $"{c}FO", $"{n} fuel oil", 1,
        [
            AI("PT", 2, "Fuel oil pressure {n}", "bar", 0, 12, ll: 2, l: 3, group: "AE-FUEL"),
            AI("TT", 2, "Fuel oil temperature {n}", "°C", 0, 160, h: 145, hh: 150, group: "AE-FUEL"),
            AI("FT", 1, "Fuel consumption", "kg/h", 0, 800),
        ]));
        l.Add(new(S, $"{c}TCH", $"{n} turbocharger and air", 1,
        [
            AI("ST", 1, "Turbocharger speed", "rpm", 0, 40000, h: 32000, hh: 35000, group: "AE-EXH"),
            AI("TT", 2, "Turbocharger gas inlet temperature {n}", "°C", 0, 700),
            AI("PT", 1, "Charge air pressure", "bar", 0, 4),
            AI("TT", 2, "Charge air temperature {n}", "°C", 0, 100, h: 60, hh: 70, group: "AE-COOL"),
        ]));
        l.Add(new(S, $"{c}GEN", $"{n} generator", 1,
        [
            AI("II", 3, "Phase current {n}", "A", 0, 2500, h: 2000, hh: 2300, group: "AE-GEN"),
            AI("VT", 3, "Phase voltage {n}", "V", 0, 500, l: 420, h: 480, group: "AE-GEN"),
            AI("FT", 1, "Frequency", "Hz", 40, 70, l: 58, h: 62, group: "AE-GEN"),
            AI("JT", 1, "Active power", "kW", 0, 2000),
            AI("TT", 6, "Winding temperature {n}", "°C", 0, 200, h: 130, hh: 150, group: "AE-GEN"),
        ]));
        l.Add(new(S, $"{c}BRG", $"{n} bearings and vibration", 1,
        [
            AI("TT", 7, "Main bearing temperature {n}", "°C", 0, 150, h: 85, hh: 95, group: "AE-LUB"),
            AI("TT", 6, "Liner wall temperature cyl {n}", "°C", 0, 300, h: 250, hh: 270, group: "AE-COOL"),
            AI("VT", 2, "Engine vibration {n}", "mm/s", 0, 50, h: 25, hh: 35, group: "AE-LUB"),
        ]));
        l.Add(new(S, $"{c}CTL", $"{n} control", 1,
        [
            DI("XS", 10, "Status {n}", "AE-GEN"),
            DI("XA", 8, "Alarm {n}", "AE-GEN"),
            DO("YS", 4, "Command {n}"),
        ]));
    }

    private static void AddBoiler(List<Equip> l, int b)
    {
        const string S = "BLR";
        var n = $"Boiler {b}";
        var c = $"B{b}";
        l.Add(new(S, $"{c}WTR", $"{n} water and steam", 1,
        [
            AI("LT", 3, "Drum level {n}", "%", 0, 100, ll: 20, l: 30, h: 70, hh: 80, group: "BLR-WATER", safety: 3, cls: SafetyClass.SIL),
            AI("PT", 3, "Steam pressure {n}", "bar", 0, 12, h: 8.5, hh: 9.5, group: "BLR-STEAM"),
            AI("TT", 4, "Steam temperature {n}", "°C", 0, 300, h: 200, hh: 220, group: "BLR-STEAM"),
            AI("FT", 2, "Feed water flow {n}", "t/h", 0, 20),
            AI("AT", 2, "Water conductivity {n}", "mS/cm", 0, 20, h: 8, group: "BLR-WATER"),
            AI("AT", 1, "Feed water pH", "pH", 0, 14),
        ]));
        l.Add(new(S, $"{c}BNR", $"{n} burner", 1,
        [
            AI("PT", 3, "Fuel pressure {n}", "bar", 0, 30, ll: 5, l: 8, group: "BLR-COMB"),
            AI("TT", 3, "Fuel temperature {n}", "°C", 0, 160, h: 140, hh: 150, group: "BLR-COMB"),
            AI("TT", 4, "Flue gas temperature {n}", "°C", 0, 500, h: 380, hh: 420, group: "BLR-COMB"),
            AI("AT", 1, "Flue gas oxygen", "%", 0, 25),
            DI("ZS", 4, "Flame detector {n}", "BLR-COMB", safety: 4, cls: SafetyClass.ESD),
            DI("XS", 12, "Burner sequence status {n}", "BLR-COMB"),
            DI("XA", 10, "Burner alarm {n}", "BLR-COMB"),
            DO("YS", 8, "Burner command {n}"),
        ]));
        if (b == 1)
        {
            l.Add(new(S, "HWL", "Hotwell and dump condenser", 1,
            [
                AI("LT", 2, "Hotwell level {n}", "%", 0, 100, ll: 15, l: 25, h: 80, hh: 90, group: "BLR-WATER"),
                AI("TT", 3, "Condensate temperature {n}", "°C", 0, 150, h: 95, group: "BLR-STEAM"),
                AI("PT", 3, "Condenser pressure {n}", "bar", -1, 3),
                AI("FT", 2, "Condensate flow {n}", "t/h", 0, 30),
                DI("XS", 10, "Pump and valve status {n}", "BLR-WATER"),
            ]));
            l.Add(new(S, "SYS", "Steam system", 1,
            [
                AI("PT", 6, "Steam header pressure {n}", "bar", 0, 12, h: 9, hh: 10, group: "BLR-STEAM"),
                AI("TT", 6, "Steam header temperature {n}", "°C", 0, 300, h: 200, hh: 220, group: "BLR-STEAM"),
                DI("XS", 12, "Steam valve status {n}", "BLR-STEAM"),
                DI("XA", 8, "Steam system alarm {n}", "BLR-STEAM"),
            ]));
            l.Add(new(S, "FOS", "Boiler fuel supply", 1,
            [
                AI("PT", 4, "Fuel supply pressure {n}", "bar", 0, 30, ll: 5, l: 8, group: "BLR-COMB"),
                AI("TT", 4, "Fuel supply temperature {n}", "°C", 0, 160, h: 140, hh: 150, group: "BLR-COMB"),
                DI("XS", 12, "Fuel supply status {n}", "BLR-COMB"),
            ]));
            l.Add(new(S, "EGE", "Exhaust gas economiser", 1,
            [
                AI("TT", 6, "Gas temperature {n}", "°C", 0, 500, h: 400, hh: 450, group: "BLR-STEAM"),
                AI("PT", 3, "Differential pressure {n}", "mbar", 0, 100, h: 60, hh: 80, group: "BLR-STEAM"),
                DI("XS", 6, "Soot blower status {n}", "BLR-STEAM"),
            ]));
        }
    }

    private static void AddPms(List<Equip> l)
    {
        const string S = "PMS";
        for (var g = 1; g <= 4; g++)
        {
            l.Add(new(S, $"GN{g}", $"Generator {g} breaker and protection", 1,
            [
                AI("II", 3, "Phase current {n}", "A", 0, 2500, h: 2100, hh: 2400, group: "PMS-GEN"),
                AI("VT", 3, "Phase voltage {n}", "V", 0, 500, l: 410, h: 480, group: "PMS-GEN"),
                AI("FT", 1, "Frequency", "Hz", 40, 70, l: 57, h: 63, group: "PMS-GEN"),
                AI("JT", 2, "Power {n}", "kW", -200, 2200, h: 2000, hh: 2150, group: "PMS-GEN"),
                DI("XS", 10, "Breaker and sync status {n}", "PMS-GEN"),
                DI("XA", 10, "Protection trip {n}", "PMS-GEN", safety: 4),
                DO("YS", 6, "Breaker command {n}"),
            ]));
        }

        for (var b = 1; b <= 2; b++)
        {
            l.Add(new(S, $"BUS{b}", $"Main switchboard bus {b}", 1,
            [
                AI("VT", 3, "Bus voltage {n}", "V", 0, 500, l: 410, h: 480, group: "PMS-BUS"),
                AI("FT", 1, "Bus frequency", "Hz", 40, 70, l: 57, h: 63, group: "PMS-BUS"),
                AI("JT", 2, "Bus load {n}", "kW", 0, 6000, h: 5200, hh: 5600, group: "PMS-BUS"),
                DI("XS", 12, "Bus status {n}", "PMS-BUS"),
                DI("XA", 10, "Bus alarm {n}", "PMS-BUS"),
            ]));
        }

        for (var f = 1; f <= 90; f++)
        {
            l.Add(new(S, $"FD{f:00}", $"Feeder {f}", 2,
            [
                AI("II", 3, "Feeder current {n}", "A", 0, 800, h: 650, hh: 750, group: "PMS-FDR"),
                AI("JT", 1, "Feeder power", "kW", 0, 500),
                DI("XS", 4, "Feeder breaker status {n}", "PMS-FDR"),
                DI("XA", 2, "Feeder fault {n}", "PMS-FDR"),
            ]));
        }
    }

    private static void AddBwts(List<Equip> l, int u)
    {
        const string S = "BWTS";
        l.Add(new(S, $"U{u}FLT", $"BWTS {u} filtration", 1,
        [
            AI("PT", 3, "Filter pressure {n}", "bar", 0, 10, h: 6, hh: 8, group: "BWTS-FILT"),
            AI("FT", 2, "Ballast flow {n}", "m3/h", 0, 1500),
            AI("PT", 2, "Differential pressure {n}", "bar", 0, 2, h: 1.0, hh: 1.5, group: "BWTS-FILT"),
            DI("XS", 8, "Filter valve status {n}", "BWTS-FILT"),
            DI("XA", 6, "Filter alarm {n}", "BWTS-FILT"),
        ]));
        l.Add(new(S, $"U{u}UVR", $"BWTS {u} UV reactor", 1,
        [
            AI("TT", 6, "Lamp temperature {n}", "°C", 0, 120, h: 80, hh: 95, group: "BWTS-UV"),
            AI("II", 6, "Lamp current {n}", "A", 0, 50, l: 5, h: 40, group: "BWTS-UV"),
            AI("AT", 2, "UV intensity {n}", "%", 0, 100, l: 40, group: "BWTS-UV"),
            DI("XS", 8, "Reactor status {n}", "BWTS-UV"),
            DI("XA", 6, "Reactor alarm {n}", "BWTS-UV"),
        ]));
        l.Add(new(S, $"U{u}DST", $"BWTS {u} dosing and sampling", 1,
        [
            AI("AT", 3, "Residual chemical concentration {n}", "ppm", 0, 20, h: 10, hh: 15, group: "BWTS-DOSE"),
            AI("LT", 2, "Chemical tank level {n}", "%", 0, 100, ll: 10, l: 20, group: "BWTS-DOSE"),
            DI("XS", 6, "Dosing pump status {n}", "BWTS-DOSE"),
            DO("YS", 4, "Dosing command {n}"),
        ]));
    }

    private static void AddBallastPumpsAndValves(List<Equip> l)
    {
        const string S = "BWTS";
        for (var p = 1; p <= 2; p++)
        {
            l.Add(new(S, $"BP{p}", $"Ballast pump {p}", 1,
            [
                AI("II", 1, "Motor current", "A", 0, 400, h: 340, hh: 380, group: "BWTS-FILT"),
                AI("PT", 2, "Pump pressure {n}", "bar", 0, 10, h: 7, hh: 8.5, group: "BWTS-FILT"),
                AI("TT", 2, "Bearing temperature {n}", "°C", 0, 150, h: 90, hh: 100, group: "BWTS-FILT"),
                AI("VT", 1, "Pump vibration", "mm/s", 0, 50, h: 18, hh: 28, group: "BWTS-FILT"),
                DI("XS", 4, "Pump status {n}", "BWTS-FILT"),
                DI("XA", 2, "Pump alarm {n}", "BWTS-FILT"),
                DO("YS", 2, "Pump command {n}"),
            ]));
        }

        l.Add(new(S, "VLV", "Ballast valves", 1,
        [
            DI("ZS", 40, "Valve position feedback {n}", "BWTS-FILT"),
            DO("YS", 20, "Valve command {n}"),
        ]));
    }

    private static readonly string[] TankFamilies = ["HFO", "MDO", "LSO", "LO", "FW", "DW", "BAL", "SLG", "BIL", "CGO"];

    private static Equip Tank(int number)
    {
        var family = TankFamilies[(number - 1) % TankFamilies.Length];
        var index = (number - 1) / TankFamilies.Length + 1;
        var code = $"{family}{index}";
        var fuel = family is "HFO" or "MDO" or "LSO";
        return new Equip("TANK", code, $"Tank {code}", (number - 1) % 3 + 1,
        [
            AI("LT", 3, "Level transmitter {n}", "%", 0, 100, ll: 5, l: 10, h: 85, hh: 95, group: "TANK-LEVEL"),
            AI("TT", 4, "Temperature {n}", "°C", 0, 120, h: fuel ? 60 : 50, hh: fuel ? 75 : null, group: "TANK-TEMP"),
            AI("PT", 2, "Air pipe pressure {n}", "mbar", -50, 150),
            DI("LS", 4, "Level switch {n}", "TANK-LEVEL"),
            DI("XS", 2, "Overflow alarm {n}", "TANK-OVERFLOW", safety: fuel ? 2 : 0),
            AI("ZT", 2, "Valve position {n}", "%", 0, 100),
            DI("XA", 2, "Tank alarm {n}", "TANK-LEVEL"),
        ]);
    }
}
