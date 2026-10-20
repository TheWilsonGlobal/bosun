using System.Globalization;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Evals;

/// <summary>
/// What a draft DOES, independent of how many ops said it: later ops for the same target replace earlier ones, ops that end where
/// they started are dropped. Two drafts with the same net effect are the same answer (a model that sets a limit twice is not wrong).
/// </summary>
public sealed record Effect(string Key, string Value, double? Hysteresis = null)
{
    public override string ToString() => $"{Key} = {Value}";
}

public static class NetEffect
{
    public static IReadOnlyDictionary<string, Effect> Of(IEnumerable<ChangeOp> ops)
    {
        var first = new Dictionary<string, ChangeOp>();
        var last = new Dictionary<string, ChangeOp>();
        foreach (var op in ops)
        {
            var key = KeyOf(op);
            first.TryAdd(key, op);
            last[key] = op;
        }

        var result = new SortedDictionary<string, Effect>(StringComparer.Ordinal);
        foreach (var (key, op) in last)
        {
            if (IsNoOp(first[key], op))
            {
                continue;
            }

            result[key] = op switch
            {
                SetAlarmLimitOp { New: null } => new Effect(key, "removed"),
                SetAlarmLimitOp s => new Effect(key, $"{Num(s.New!.Value)}/{Num(s.New.DelayS)}", s.New.Hysteresis),
                AssignGroupOp a => new Effect(key, a.New ?? "(none)"),
                UpdateTagOp u => new Effect(key, Canonical(u.Field, u.New)),
                CreateTagOp c => new Effect(key, $"{c.SignalType}|{c.Unit}|{Num(c.RangeLow)}|{Num(c.RangeHigh)}|{c.SafetyClass.ToString().ToUpperInvariant()}"),
                BindChannelOp b => new Effect(key, b.New?.ToString(CultureInfo.InvariantCulture) ?? "(none)"),
                _ => new Effect(key, op.TypeName),
            };
        }

        // A group given on the createTag itself ends in the same state as a separate assignGroup.
        foreach (var (key, op) in last)
        {
            if (op is CreateTagOp { AlarmGroup: { Length: > 0 } group } && !result.ContainsKey($"group|{op.TagName}"))
            {
                result[$"group|{op.TagName}"] = new Effect($"group|{op.TagName}", group);
            }
        }

        return result;
    }

    /// <summary>The effects an eval case expects.</summary>
    public static IReadOnlyDictionary<string, Effect> Of(IEnumerable<ExpectedOp> expected)
    {
        var result = new SortedDictionary<string, Effect>(StringComparer.Ordinal);
        foreach (var op in expected)
        {
            foreach (var tag in op.TagNames())
            {
                var effect = op.Type switch
                {
                    "setAlarmLimit" when op.Remove == true => new Effect($"limit|{tag}|{op.Level}", "removed"),
                    "setAlarmLimit" => new Effect($"limit|{tag}|{op.Level}", $"{Num(op.Value)}/{Num(op.DelayS ?? 0)}", op.Hysteresis),
                    "assignGroup" => new Effect($"group|{tag}", op.Group ?? "(none)"),
                    "updateTag" => new Effect($"field|{tag}|{op.Field!.ToLowerInvariant()}", Canonical(op.Field, op.To)),
                    "createTag" => new Effect($"create|{tag}", $"{op.SignalType}|{op.Unit}|{Num(op.RangeLow)}|{Num(op.RangeHigh)}|{(op.SafetyClass ?? "NONE").ToUpperInvariant()}"),
                    _ => throw new InvalidDataException($"Unknown expected op type '{op.Type}'."),
                };
                result[effect.Key] = effect;
            }
        }

        return result;
    }

    /// <summary>Empty when the actual effects equal the expected ones; otherwise human-readable differences.</summary>
    public static IReadOnlyList<string> Diff(IReadOnlyDictionary<string, Effect> expected, IReadOnlyDictionary<string, Effect> actual)
    {
        var problems = new List<string>();
        foreach (var (key, want) in expected)
        {
            if (!actual.TryGetValue(key, out var got))
            {
                problems.Add($"missing {want}");
            }
            else if (got.Value != want.Value)
            {
                problems.Add($"wrong {key}: expected {want.Value}, got {got.Value}");
            }
            else if (want.Hysteresis is { } hysteresis && got.Hysteresis is { } actualHysteresis && Math.Abs(hysteresis - actualHysteresis) > 1e-9)
            {
                problems.Add($"wrong {key} hysteresis: expected {Num(hysteresis)}, got {Num(actualHysteresis)}");
            }
        }

        problems.AddRange(actual.Where(a => !expected.ContainsKey(a.Key)).Select(a => $"extra {a.Value}"));
        return problems;
    }

    private static string KeyOf(ChangeOp op) => op switch
    {
        SetAlarmLimitOp s => $"limit|{op.TagName}|{s.Level}",
        AssignGroupOp => $"group|{op.TagName}",
        UpdateTagOp u => $"field|{op.TagName}|{u.Field.ToLowerInvariant()}",
        CreateTagOp => $"create|{op.TagName}",
        BindChannelOp => $"channel|{op.TagName}",
        _ => $"{op.TypeName}|{op.TagName}",
    };

    private static bool IsNoOp(ChangeOp first, ChangeOp last) => (first, last) switch
    {
        (SetAlarmLimitOp a, SetAlarmLimitOp b) => a.Old == b.New,
        (AssignGroupOp a, AssignGroupOp b) => string.Equals(a.Old ?? "", b.New ?? "", StringComparison.Ordinal),
        (UpdateTagOp a, UpdateTagOp b) => TagFields.IsKnown(a.Field) && TagFields.SameValue(a.Field, a.Old, b.New),
        (BindChannelOp a, BindChannelOp b) => a.Old == b.New,
        _ => false,
    };

    private static string Canonical(string field, string? value) =>
        TagFields.IsKnown(field) ? (value is null or "" ? "(none)" : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && field.StartsWith("range", StringComparison.OrdinalIgnoreCase) ? Num(n) : value) : value ?? "(none)";

    private static string Num(double? value) => value is { } v ? v.ToString("R", CultureInfo.InvariantCulture) : "";
}
