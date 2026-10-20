using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Bosun.Extraction;

/// <summary>The fields of an extracted tag row. Keys are camelCase (the REST contract); the model schema uses the snake_case names.</summary>
public static class Fields
{
    public const string TagName = "tagName";
    public const string Description = "description";
    public const string SignalType = "signalType";
    public const string Unit = "unit";
    public const string RangeLow = "rangeLow";
    public const string RangeHigh = "rangeHigh";
    public const string AlarmHh = "alarmHh";
    public const string AlarmH = "alarmH";
    public const string AlarmL = "alarmL";
    public const string AlarmLl = "alarmLl";
    public const string DelayS = "delayS";

    public static IReadOnlyList<string> All { get; } =
        [TagName, Description, SignalType, Unit, RangeLow, RangeHigh, AlarmHh, AlarmH, AlarmL, AlarmLl, DelayS];

    public static IReadOnlyList<string> Numeric { get; } = [RangeLow, RangeHigh, AlarmHh, AlarmH, AlarmL, AlarmLl, DelayS];

    public static IReadOnlyList<string> Limits { get; } = [AlarmLl, AlarmL, AlarmH, AlarmHh];

    public static bool IsNumeric(string field) => Numeric.Contains(field);

    public static bool IsKnown(string field) => All.Contains(field);

    /// <summary>The snake_case name the model schema uses (plan s6.3) for a field key.</summary>
    public static string SnakeName(string field) => field switch
    {
        TagName => "tag_name",
        Description => "description",
        SignalType => "signal_type",
        Unit => "unit",
        RangeLow => "range_low",
        RangeHigh => "range_high",
        AlarmHh => "alarm_hh",
        AlarmH => "alarm_h",
        AlarmL => "alarm_l",
        AlarmLl => "alarm_ll",
        DelayS => "delay_s",
        _ => field,
    };

    /// <summary>The alarm level a limit field stands for; null for any other field.</summary>
    public static Bosun.Domain.AlarmLevel? LevelOf(string field) => field switch
    {
        AlarmHh => Bosun.Domain.AlarmLevel.HH,
        AlarmH => Bosun.Domain.AlarmLevel.H,
        AlarmL => Bosun.Domain.AlarmLevel.L,
        AlarmLl => Bosun.Domain.AlarmLevel.LL,
        _ => null,
    };

    public static string FieldOfLevel(Bosun.Domain.AlarmLevel level) => level switch
    {
        Bosun.Domain.AlarmLevel.HH => AlarmHh,
        Bosun.Domain.AlarmLevel.H => AlarmH,
        Bosun.Domain.AlarmLevel.L => AlarmL,
        _ => AlarmLl,
    };
}

/// <summary>
/// One row as the model returned it: every value is a string exactly as the document printed it (numbers are NOT converted by the
/// model; the code normalises them, so "1.200,5" vs "1,200.5" is a deterministic, tested step). The names are the snake_case ones
/// of plan s6.3, and every member is required (null when the cell is empty) so the model cannot silently omit one.
/// </summary>
public sealed record ReadRow(
    [property: JsonPropertyName("tag_name")] string TagName,
    string? Description,
    [property: JsonPropertyName("signal_type")] string? SignalType,
    string? Unit,
    [property: JsonPropertyName("range_low")] string? RangeLow,
    [property: JsonPropertyName("range_high")] string? RangeHigh,
    [property: JsonPropertyName("alarm_hh")] string? AlarmHh,
    [property: JsonPropertyName("alarm_h")] string? AlarmH,
    [property: JsonPropertyName("alarm_l")] string? AlarmL,
    [property: JsonPropertyName("alarm_ll")] string? AlarmLl,
    [property: JsonPropertyName("delay_s")] string? DelayS,
    [property: JsonPropertyName("source_ref")] string? SourceRef,
    [property: JsonPropertyName("unclear_fields")] IReadOnlyList<string> UnclearFields)
{
    public string? Get(string field) => field switch
    {
        Fields.TagName => TagName,
        Fields.Description => Description,
        Fields.SignalType => SignalType,
        Fields.Unit => Unit,
        Fields.RangeLow => RangeLow,
        Fields.RangeHigh => RangeHigh,
        Fields.AlarmHh => AlarmHh,
        Fields.AlarmH => AlarmH,
        Fields.AlarmL => AlarmL,
        Fields.AlarmLl => AlarmLl,
        Fields.DelayS => DelayS,
        _ => null,
    };

    /// <summary>Field keys the model reported as hard to read (names it invented are ignored).</summary>
    public IReadOnlySet<string> UnclearKeys() =>
        UnclearFields.Select(CanonicalField).Where(k => k is not null).Select(k => k!).ToImmutableHashSet(StringComparer.Ordinal);

    private static string? CanonicalField(string name)
    {
        var cleaned = name.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return Fields.All.FirstOrDefault(f => f.ToLowerInvariant() == cleaned);
    }
}

/// <summary>The model reply for one page: the rows it found.</summary>
public sealed record ReadReply(IReadOnlyList<ReadRow> Rows);
