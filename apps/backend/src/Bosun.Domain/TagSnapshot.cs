using System.Collections.Immutable;

namespace Bosun.Domain;

/// <summary>
/// Immutable, persistence-free view of a tag used by the rules engine, by <c>DeleteTagOp.Old</c> and by
/// the .bosun.json file. Limits are keyed by level.
/// </summary>
public sealed record TagSnapshot
{
    public required string TagName { get; init; }
    public string? Description { get; init; }
    public SignalType SignalType { get; init; }
    public string? Unit { get; init; }
    public double? RangeLow { get; init; }
    public double? RangeHigh { get; init; }
    public SafetyClass SafetyClass { get; init; }
    public string? AlarmGroup { get; init; }
    public int? ChannelId { get; init; }
    public ImmutableSortedDictionary<AlarmLevel, AlarmLimitValue> Limits { get; init; } =
        ImmutableSortedDictionary<AlarmLevel, AlarmLimitValue>.Empty;

    /// <summary>Value equality that compares <see cref="Limits"/> by content (the default record equality would compare the dictionary by reference).</summary>
    public bool Equals(TagSnapshot? other) =>
        other is not null
        && TagName == other.TagName
        && Description == other.Description
        && SignalType == other.SignalType
        && Unit == other.Unit
        && RangeLow == other.RangeLow
        && RangeHigh == other.RangeHigh
        && SafetyClass == other.SafetyClass
        && AlarmGroup == other.AlarmGroup
        && ChannelId == other.ChannelId
        && Limits.SequenceEqual(other.Limits);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TagName);
        hash.Add(Description);
        hash.Add(SignalType);
        hash.Add(Unit);
        hash.Add(RangeLow);
        hash.Add(RangeHigh);
        hash.Add(SafetyClass);
        hash.Add(AlarmGroup);
        hash.Add(ChannelId);
        foreach (var (level, limit) in Limits)
        {
            hash.Add(level);
            hash.Add(limit);
        }

        return hash.ToHashCode();
    }
}
