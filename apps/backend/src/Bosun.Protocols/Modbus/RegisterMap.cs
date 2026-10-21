using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bosun.Protocols.Modbus;

/// <summary>Where a register is served from. The simulator mirrors one bank to both, a real PLC would not.</summary>
public enum RegisterBank
{
    [JsonStringEnumMemberName("holding")] Holding,
    [JsonStringEnumMemberName("input")] Input,
}

/// <summary>Simulator-only hint: the value the register idles at, its noise amplitude and a slow drift. The backend ignores it.</summary>
public sealed record SimHint(double Nominal, double Noise = 0, double DriftPerMinute = 0);

/// <summary>
/// One register of the plant: raw int16 = round(engineering * Scale), so engineering = raw / Scale.
/// <see cref="Tag"/> is a tag of the configurator project (it must exist there: a test checks every one against the seed).
/// </summary>
public sealed record RegisterEntry(
    int Address,
    string Tag,
    double Scale,
    string? Unit,
    RegisterBank Bank = RegisterBank.Holding,
    SimHint? Sim = null)
{
    public double ToEngineering(ushort raw) => unchecked((short)raw) / Scale;

    public ushort ToRaw(double engineering)
    {
        var scaled = Math.Round(engineering * Scale);
        return unchecked((ushort)(short)Math.Clamp(scaled, short.MinValue, short.MaxValue));
    }
}

/// <summary>A contiguous run of registers in one bank that one read can fetch.</summary>
public sealed record RegisterBlock(RegisterBank Bank, int Address, int Count, IReadOnlyList<RegisterEntry> Entries);

/// <summary>
/// The single register-to-tag data file shared by the simulator and the backend (<c>apps/simulator/mapping/registers.json</c>).
/// <code>
/// { "unitId": 1, "registers": [ { "address": 0, "tag": "ME-EXH-TT01", "scale": 10, "unit": "degC", "bank": "holding",
///                                 "sim": { "nominal": 80, "noise": 0.4 } } ] }
/// </code>
/// </summary>
public sealed class RegisterMap
{
    public const string FileName = "registers.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private RegisterMap(byte unitId, IReadOnlyList<RegisterEntry> registers)
    {
        UnitId = unitId;
        Registers = registers;
        ByTag = registers.ToDictionary(r => r.Tag, StringComparer.Ordinal);
        ByAddress = registers.ToDictionary(r => r.Address);
    }

    public byte UnitId { get; }

    public IReadOnlyList<RegisterEntry> Registers { get; }

    public IReadOnlyDictionary<string, RegisterEntry> ByTag { get; }

    public IReadOnlyDictionary<int, RegisterEntry> ByAddress { get; }

    /// <summary>One past the highest mapped address: the size of the bank the simulator serves.</summary>
    public int Size => Registers.Count == 0 ? 0 : Registers.Max(r => r.Address) + 1;

    public static RegisterMap Load(string path) => Parse(File.ReadAllText(path));

    public static RegisterMap Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RegisterFile>(json, Json) ?? throw new FormatException("The register map is empty.");
        var registers = file.Registers ?? throw new FormatException("The register map has no 'registers' array.");
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<(RegisterBank, int)>();
        foreach (var r in registers)
        {
            if (r.Address is < 0 or > 0xFFFF)
            {
                throw new FormatException($"Register address {r.Address} is outside 0..65535.");
            }

            if (string.IsNullOrWhiteSpace(r.Tag) || !tags.Add(r.Tag))
            {
                throw new FormatException($"Tag '{r.Tag}' is empty or mapped twice.");
            }

            if (!double.IsFinite(r.Scale) || r.Scale <= 0)
            {
                throw new FormatException($"Register {r.Address} ({r.Tag}): scale must be a positive number.");
            }

            if (!addresses.Add((r.Bank, r.Address)))
            {
                throw new FormatException($"Register address {r.Address} is mapped twice in the {r.Bank} bank.");
            }
        }

        return new RegisterMap(file.UnitId ?? 1, registers.OrderBy(r => r.Address).ToList());
    }

    /// <summary>Contiguous blocks per bank, each at most <paramref name="maxRegisters"/> long (the protocol allows 125).</summary>
    public IReadOnlyList<RegisterBlock> Blocks(int maxRegisters = ModbusFrame.MaxRegisters)
    {
        var blocks = new List<RegisterBlock>();
        foreach (var bank in Registers.Select(r => r.Bank).Distinct())
        {
            var run = new List<RegisterEntry>();
            foreach (var entry in Registers.Where(r => r.Bank == bank).OrderBy(r => r.Address))
            {
                if (run.Count > 0 && (entry.Address != run[^1].Address + 1 || entry.Address - run[0].Address + 1 > maxRegisters))
                {
                    Flush();
                }

                run.Add(entry);
            }

            Flush();

            void Flush()
            {
                if (run.Count > 0)
                {
                    blocks.Add(new RegisterBlock(bank, run[0].Address, run[^1].Address - run[0].Address + 1, run.ToArray()));
                    run.Clear();
                }
            }
        }

        return blocks;
    }

    /// <summary>Finds <c>apps/simulator/mapping/registers.json</c> by walking up from the given directories (the repo root holds Bosun.sln).</summary>
    public static string? FindDefaultFile(params string?[] startDirectories)
    {
        foreach (var start in startDirectories)
        {
            for (var dir = start is null ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "apps", "simulator", "mapping", FileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private sealed record RegisterFile(byte? UnitId, List<RegisterEntry>? Registers);
}
