using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bosun.Evals;

/// <summary>
/// One agent eval case (plan s9): an instruction plus every outcome that counts as a pass. A case passes when ANY accepted
/// outcome matches: an exact op set, an acceptable alternative op set, a clarifying question, or a refusal with no ops.
/// </summary>
public sealed class EvalCase
{
    public string Id { get; set; } = "";

    /// <summary>simple | multi-step | ambiguous | impossible | unsafe</summary>
    public string Category { get; set; } = "";

    public string Instruction { get; set; } = "";

    public EvalSetup? Setup { get; set; }

    public List<EvalAccept> Accept { get; set; } = [];

    public string? Notes { get; set; }
}

public sealed class EvalSetup
{
    /// <summary>Descriptions to overwrite before the run (the injection fixtures put hostile text here).</summary>
    public Dictionary<string, string>? TagDescriptions { get; set; }
}

public sealed class EvalAccept
{
    /// <summary>ops | clarify | refuse</summary>
    public string Kind { get; set; } = "";

    /// <summary>For kind ops: the exact NET effect the draft must have (duplicates and no-ops are ignored, anything extra fails).</summary>
    public List<ExpectedOp> Ops { get; set; } = [];
}

public sealed class ExpectedOp
{
    /// <summary>setAlarmLimit | assignGroup | updateTag | createTag</summary>
    public string Type { get; set; } = "";

    public string? Tag { get; set; }

    public List<string>? Tags { get; set; }

    /// <summary>Shorthand "ME-EXH-TT01..08" for a numbered run of tags.</summary>
    public string? TagRange { get; set; }

    // setAlarmLimit
    public string? Level { get; set; }
    public double? Value { get; set; }
    public double? DelayS { get; set; }

    /// <summary>Compared only when given.</summary>
    public double? Hysteresis { get; set; }
    public bool? Remove { get; set; }

    // assignGroup (null Group = ungroup)
    public string? Group { get; set; }

    // updateTag
    public string? Field { get; set; }
    public string? To { get; set; }

    // createTag
    public string? SignalType { get; set; }
    public string? Unit { get; set; }
    public double? RangeLow { get; set; }
    public double? RangeHigh { get; set; }
    public string? SafetyClass { get; set; }

    public IEnumerable<string> TagNames()
    {
        if (Tag is not null)
        {
            yield return Tag;
        }

        foreach (var tag in Tags ?? [])
        {
            yield return tag;
        }

        if (TagRange is { } range)
        {
            var match = System.Text.RegularExpressions.Regex.Match(range, @"^(?<prefix>.*?)(?<from>\d+)\.\.(?<to>\d+)$");
            if (!match.Success)
            {
                throw new InvalidDataException($"tagRange '{range}' must look like ME-EXH-TT01..08.");
            }

            var width = match.Groups["from"].Length;
            for (var n = int.Parse(match.Groups["from"].Value); n <= int.Parse(match.Groups["to"].Value); n++)
            {
                yield return match.Groups["prefix"].Value + n.ToString().PadLeft(width, '0');
            }
        }
    }
}

public static class EvalCaseLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static IReadOnlyList<EvalCase> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No eval cases at {directory}.");
        }

        var cases = new List<EvalCase>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml").Order(StringComparer.Ordinal))
        {
            var loaded = Yaml.Deserialize<List<EvalCase>>(File.ReadAllText(file)) ?? [];
            cases.AddRange(loaded);
        }

        foreach (var duplicate in cases.GroupBy(c => c.Id).Where(g => g.Count() > 1))
        {
            throw new InvalidDataException($"Duplicate eval case id '{duplicate.Key}'.");
        }

        foreach (var c in cases)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || string.IsNullOrWhiteSpace(c.Instruction) || c.Accept.Count == 0)
            {
                throw new InvalidDataException($"Eval case '{c.Id}' needs an id, an instruction and at least one accepted outcome.");
            }
        }

        return cases;
    }
}
