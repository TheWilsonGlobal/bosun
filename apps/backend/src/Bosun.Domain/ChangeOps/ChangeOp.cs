using System.Text.Json.Serialization;

namespace Bosun.Domain;

/// <summary>The limit settings of one alarm level.</summary>
public sealed record AlarmLimitValue(double Value, double DelayS, double Hysteresis);

/// <summary>
/// A typed configuration operation, serialised with a "type" discriminator. Every op that changes existing
/// state carries the <c>Old</c> value it expects, so a change set can be diffed, reverted, and applied with
/// optimistic concurrency (plan s5).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CreateTagOp), "createTag")]
[JsonDerivedType(typeof(UpdateTagOp), "updateTag")]
[JsonDerivedType(typeof(SetAlarmLimitOp), "setAlarmLimit")]
[JsonDerivedType(typeof(AssignGroupOp), "assignGroup")]
[JsonDerivedType(typeof(BindChannelOp), "bindChannel")]
[JsonDerivedType(typeof(DeleteTagOp), "deleteTag")]
public abstract record ChangeOp
{
    /// <summary>Tags are addressed by name: stable for humans, the agent and references within a change set.</summary>
    public required string TagName { get; init; }

    [JsonIgnore]
    public abstract string TypeName { get; }
}

/// <summary>Creates a tag. Alarm limits and channel bindings are separate ops.</summary>
public sealed record CreateTagOp : ChangeOp
{
    public string? Description { get; init; }
    public SignalType SignalType { get; init; }
    public string? Unit { get; init; }
    public double? RangeLow { get; init; }
    public double? RangeHigh { get; init; }
    public SafetyClass SafetyClass { get; init; }

    /// <summary>Optional alarm group name; must already exist.</summary>
    public string? AlarmGroup { get; init; }

    [JsonIgnore]
    public override string TypeName => "createTag";
}

/// <summary>
/// Updates one scalar field. Fields: description, unit, signalType, rangeLow, rangeHigh, safetyClass.
/// Values are strings (invariant culture for numbers; empty/null clears an optional field).
/// </summary>
public sealed record UpdateTagOp : ChangeOp
{
    public required string Field { get; init; }
    public string? Old { get; init; }
    public string? New { get; init; }
    [JsonIgnore]
    public override string TypeName => "updateTag";
}

/// <summary>Sets (or, when New is null, removes) the limit of one level.</summary>
public sealed record SetAlarmLimitOp : ChangeOp
{
    public required AlarmLevel Level { get; init; }
    public AlarmLimitValue? Old { get; init; }
    public AlarmLimitValue? New { get; init; }
    [JsonIgnore]
    public override string TypeName => "setAlarmLimit";
}

/// <summary>Assigns the tag to an existing alarm group by name (null = ungroup).</summary>
public sealed record AssignGroupOp : ChangeOp
{
    public string? Old { get; init; }
    public string? New { get; init; }
    [JsonIgnore]
    public override string TypeName => "assignGroup";
}

/// <summary>Binds the tag to a channel id (null = unbind).</summary>
public sealed record BindChannelOp : ChangeOp
{
    public int? Old { get; init; }
    public int? New { get; init; }
    [JsonIgnore]
    public override string TypeName => "bindChannel";
}

/// <summary>Deletes a tag. <c>Old</c> is the full snapshot, which allows reverting.</summary>
public sealed record DeleteTagOp : ChangeOp
{
    public TagSnapshot? Old { get; init; }
    [JsonIgnore]
    public override string TypeName => "deleteTag";
}
