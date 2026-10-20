using System.Collections.Immutable;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Rules.Tests;

internal sealed class FakeProject : IProjectView
{
    private readonly Dictionary<string, TagSnapshot> tags = new();
    private readonly HashSet<string> groups = new();
    private readonly Dictionary<int, ChannelInfo> channels = new();

    public FakeProject WithTag(TagSnapshot tag)
    {
        tags[tag.TagName] = tag;
        return this;
    }

    public FakeProject WithGroup(string name)
    {
        groups.Add(name);
        return this;
    }

    public FakeProject WithChannel(int id, SignalType type, string? boundTo = null)
    {
        channels[id] = new ChannelInfo(id, type, boundTo);
        return this;
    }

    public TagSnapshot? GetTag(string tagName) => tags.GetValueOrDefault(tagName);

    public bool AlarmGroupExists(string name) => groups.Contains(name);

    public ChannelInfo? GetChannel(int channelId) => channels.GetValueOrDefault(channelId);
}

internal static class Tags
{
    /// <summary>A rule-clean analog temperature tag, range 0..500 degC.</summary>
    public static TagSnapshot Analog(string name = "ME-EXH-TT01", Action<TagSnapshotBuilder>? tweak = null)
    {
        var b = new TagSnapshotBuilder(name);
        tweak?.Invoke(b);
        return b.Build();
    }

    public static ImmutableSortedDictionary<AlarmLevel, AlarmLimitValue> Limits(params (AlarmLevel Level, double Value)[] items) =>
        items.ToImmutableSortedDictionary(i => i.Level, i => new AlarmLimitValue(i.Value, 5, 1));
}

internal sealed class TagSnapshotBuilder(string name)
{
    public string Name { get; set; } = name;
    public SignalType Signal { get; set; } = SignalType.AI;
    public string? Unit { get; set; } = "°C";
    public double? Low { get; set; } = 0;
    public double? High { get; set; } = 500;
    public SafetyClass Safety { get; set; } = SafetyClass.None;
    public string? Group { get; set; }
    public int? Channel { get; set; }
    public ImmutableSortedDictionary<AlarmLevel, AlarmLimitValue> Limits { get; set; } =
        ImmutableSortedDictionary<AlarmLevel, AlarmLimitValue>.Empty;

    public TagSnapshot Build() => new()
    {
        TagName = Name,
        Description = "desc",
        SignalType = Signal,
        Unit = Unit,
        RangeLow = Low,
        RangeHigh = High,
        SafetyClass = Safety,
        AlarmGroup = Group,
        ChannelId = Channel,
        Limits = Limits,
    };
}
