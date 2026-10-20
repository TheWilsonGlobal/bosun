using System.Collections.Immutable;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Bosun.Rules;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Application;

/// <summary>
/// <see cref="IProjectView"/> over a bounded preload: only the tags, groups and channels a set of ops can touch.
/// Keeps rule evaluation pure (no queries inside Bosun.Rules) while avoiding loading 5,000 tags per request.
/// </summary>
public sealed class PreloadedProjectView : IProjectView
{
    private readonly Dictionary<string, TagSnapshot> tags = new(StringComparer.Ordinal);
    private readonly HashSet<string> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ChannelInfo> channels = [];

    public TagSnapshot? GetTag(string tagName) => tags.GetValueOrDefault(tagName);

    public bool AlarmGroupExists(string name) => groups.Contains(name);

    public ChannelInfo? GetChannel(int channelId) => channels.GetValueOrDefault(channelId);

    public static async Task<PreloadedProjectView> LoadAsync(IBosunDbContext db, int projectId, IEnumerable<ChangeOp> ops, CancellationToken ct)
    {
        var opList = ops.ToList();
        var view = new PreloadedProjectView();

        var names = opList.Select(o => o.TagName).Distinct().ToList();
        if (names.Count > 0)
        {
            var found = await db.Tags.AsNoTracking()
                .Include(t => t.AlarmLimits)
                .Include(t => t.AlarmGroup)
                .Where(t => t.ProjectId == projectId && names.Contains(t.TagName))
                .ToListAsync(ct);
            foreach (var tag in found)
            {
                view.tags[tag.TagName] = Snapshot(tag);
            }
        }

        foreach (var name in await db.AlarmGroups.AsNoTracking().Where(g => g.ProjectId == projectId).Select(g => g.Name).ToListAsync(ct))
        {
            view.groups.Add(name);
        }

        // Channels a bind op targets, plus the channels the loaded tags already sit on (a signalType change is checked against them).
        var channelIds = opList.OfType<BindChannelOp>().Where(o => o.New.HasValue).Select(o => o.New!.Value)
            .Concat(view.tags.Values.Where(t => t.ChannelId.HasValue).Select(t => t.ChannelId!.Value))
            .Distinct().ToList();
        if (channelIds.Count > 0)
        {
            var owners = await db.Tags.AsNoTracking()
                .Where(t => t.ProjectId == projectId && t.ChannelId != null && channelIds.Contains(t.ChannelId.Value))
                .Select(t => new { ChannelId = t.ChannelId!.Value, t.TagName })
                .ToDictionaryAsync(x => x.ChannelId, x => x.TagName, ct);
            var rows = await db.Channels.AsNoTracking()
                .Where(c => channelIds.Contains(c.Id) && c.IoModule!.Controller!.ProjectId == projectId)
                .Select(c => new { c.Id, c.SignalType })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                view.channels[row.Id] = new ChannelInfo(row.Id, row.SignalType, owners.GetValueOrDefault(row.Id));
            }
        }

        return view;
    }

    /// <summary>Entity -> persistence-free snapshot (group name resolved from the loaded navigation).</summary>
    public static TagSnapshot Snapshot(Tag tag) => new()
    {
        TagName = tag.TagName,
        Description = tag.Description,
        SignalType = tag.SignalType,
        Unit = tag.Unit,
        RangeLow = tag.RangeLow,
        RangeHigh = tag.RangeHigh,
        SafetyClass = tag.SafetyClass,
        AlarmGroup = tag.AlarmGroup?.Name,
        ChannelId = tag.ChannelId,
        Limits = tag.AlarmLimits.ToImmutableSortedDictionary(l => l.Level, l => new AlarmLimitValue(l.Value, l.DelayS, l.Hysteresis)),
    };
}
