using System.Collections.Immutable;
using Bosun.Domain;

namespace Bosun.Rules;

/// <summary>
/// Applies ops, in order, to an in-memory overlay on top of an <see cref="IProjectView"/> and reports op-level
/// violations (unknown tag, stale <c>old</c>, duplicate name, ...). Nothing is persisted: the Application layer
/// materialises <see cref="FinalState"/> after the rules pass. This is the single definition of what each op means.
/// </summary>
public sealed class ChangeSetSimulator(IProjectView view)
{
    private readonly Dictionary<string, TagSnapshot?> overlay = new(StringComparer.Ordinal);
    private readonly HashSet<string> created = new(StringComparer.Ordinal);
    private readonly List<string> touched = [];
    private readonly Dictionary<int, string?> channelOwner = [];

    /// <summary>Tag names touched so far, in first-touch order.</summary>
    public IReadOnlyList<string> Touched => touched;

    /// <summary>Final state per touched tag; null value = deleted.</summary>
    public IReadOnlyDictionary<string, TagSnapshot?> FinalState => overlay;

    /// <summary>The tag as it stands after the ops applied so far.</summary>
    public TagSnapshot? Current(string tagName) => overlay.TryGetValue(tagName, out var tag) ? tag : view.GetTag(tagName);

    public IReadOnlyList<RuleViolation> Apply(ChangeOp op)
    {
        var found = new List<RuleViolation>();
        var current = Current(op.TagName);

        void Add(string code, string? field, string message, Severity severity = Severity.Error) =>
            found.Add(new RuleViolation(code, severity, op.TagName, field, message));

        // Defence in depth behind the JSON converter (which already refuses integers): an undefined enum value never
        // reaches the overlay, whichever caller (HTTP, agent, extraction) built the op.
        if (UndefinedEnum(op) is { } bad)
        {
            Add(ViolationCodes.InvalidValue, bad.Field, $"{bad.Value} is not a defined {bad.Field}.");
            return found;
        }

        if (op is CreateTagOp create)
        {
            ApplyCreate(create, current, Add);
            return found;
        }

        if (current is null)
        {
            Add(ViolationCodes.TagNotFound, "tagName", $"Tag '{op.TagName}' does not exist.");
            return found;
        }

        var next = op switch
        {
            UpdateTagOp update => ApplyUpdate(update, current, Add),
            SetAlarmLimitOp limit => ApplySetLimit(limit, current, Add),
            AssignGroupOp assign => ApplyAssign(assign, current, Add),
            BindChannelOp bind => ApplyBind(bind, current, Add),
            _ => ApplyDelete((DeleteTagOp)op, current, Add),
        };

        Set(op.TagName, next);
        return found;
    }

    /// <summary>The first enum member of <paramref name="op"/> that is not a defined value (field name, raw value), or null.</summary>
    public static (string Field, int Value)? UndefinedEnum(ChangeOp op) => op switch
    {
        CreateTagOp c when !Enum.IsDefined(c.SignalType) => ("signalType", (int)c.SignalType),
        CreateTagOp c when !Enum.IsDefined(c.SafetyClass) => ("safetyClass", (int)c.SafetyClass),
        SetAlarmLimitOp s when !Enum.IsDefined(s.Level) => ("level", (int)s.Level),
        _ => null,
    };

    private void ApplyCreate(CreateTagOp op, TagSnapshot? current, AddViolation add)
    {
        if (current is not null)
        {
            if (created.Contains(op.TagName))
            {
                add(ViolationCodes.DuplicateInChangeSet, "tagName", $"Tag '{op.TagName}' is created more than once in this change set.");
            }
            else
            {
                add(ViolationCodes.DuplicateInProject, "tagName", $"Tag '{op.TagName}' already exists in the project.");
            }

            return;
        }

        if (op.AlarmGroup is { } group && !view.AlarmGroupExists(group))
        {
            add(ViolationCodes.AlarmGroupUnknown, "alarmGroup", $"Alarm group '{group}' does not exist.");
        }

        created.Add(op.TagName);
        Set(op.TagName, new TagSnapshot
        {
            TagName = op.TagName,
            Description = op.Description,
            SignalType = op.SignalType,
            Unit = op.Unit,
            RangeLow = op.RangeLow,
            RangeHigh = op.RangeHigh,
            SafetyClass = op.SafetyClass,
            AlarmGroup = op.AlarmGroup,
        });
    }

    private TagSnapshot ApplyUpdate(UpdateTagOp op, TagSnapshot current, AddViolation add)
    {
        if (!TagFields.IsKnown(op.Field))
        {
            add(ViolationCodes.UnknownField, "field",
                $"Field '{op.Field}' cannot be updated. Allowed: {string.Join(", ", TagFields.Names)}.");
            return current;
        }

        if (!TagFields.SameValue(op.Field, TagFields.Get(current, op.Field), op.Old))
        {
            add(ViolationCodes.OldValueMismatch, op.Field,
                $"{op.Field} is '{TagFields.Get(current, op.Field)}' but the change expected '{op.Old}'.");
        }

        var updated = TagFields.TrySet(current, op.Field, op.New);
        if (updated is null)
        {
            add(ViolationCodes.InvalidValue, op.Field, $"'{op.New}' is not a valid value for {op.Field}.");
            return current;
        }

        // Changing the signal type of a tag that is already wired must not leave it on a channel of another type.
        if (op.Field.Equals("signalType", StringComparison.OrdinalIgnoreCase)
            && updated.ChannelId is { } channelId
            && view.GetChannel(channelId) is { } channel
            && channel.SignalType != updated.SignalType)
        {
            add(ViolationCodes.ChannelSignalMismatch, "signalType",
                $"Channel {channelId} is {channel.SignalType} but the tag would become {updated.SignalType}.");
        }

        return updated;
    }

    private static TagSnapshot ApplySetLimit(SetAlarmLimitOp op, TagSnapshot current, AddViolation add)
    {
        current.Limits.TryGetValue(op.Level, out var existing);
        if (existing != op.Old)
        {
            add(ViolationCodes.OldValueMismatch, $"limits.{op.Level}", $"The {op.Level} limit is not what the change expected.");
        }

        return current with
        {
            Limits = op.New is { } value ? current.Limits.SetItem(op.Level, value) : current.Limits.Remove(op.Level),
        };
    }

    private TagSnapshot ApplyAssign(AssignGroupOp op, TagSnapshot current, AddViolation add)
    {
        if (!string.Equals(current.AlarmGroup ?? "", op.Old ?? "", StringComparison.Ordinal))
        {
            add(ViolationCodes.OldValueMismatch, "alarmGroup",
                $"The alarm group is '{current.AlarmGroup}' but the change expected '{op.Old}'.");
        }

        if (op.New is { } group && !view.AlarmGroupExists(group))
        {
            add(ViolationCodes.AlarmGroupUnknown, "alarmGroup", $"Alarm group '{group}' does not exist.");
        }

        return current with { AlarmGroup = op.New };
    }

    private TagSnapshot ApplyBind(BindChannelOp op, TagSnapshot current, AddViolation add)
    {
        if (current.ChannelId != op.Old)
        {
            add(ViolationCodes.OldValueMismatch, "channelId",
                $"The channel is '{current.ChannelId}' but the change expected '{op.Old}'.");
        }

        if (op.New is { } channelId)
        {
            var channel = view.GetChannel(channelId);
            if (channel is null)
            {
                add(ViolationCodes.ChannelUnknown, "channelId", $"Channel {channelId} does not exist.");
            }
            else if (channel.SignalType != current.SignalType)
            {
                add(ViolationCodes.ChannelSignalMismatch, "channelId",
                    $"Channel {channelId} is {channel.SignalType} but the tag is {current.SignalType}.");
            }
            else if (OwnerOf(channel) is { } owner && owner != op.TagName)
            {
                add(ViolationCodes.ChannelInUse, "channelId", $"Channel {channelId} is already bound to '{owner}'.");
            }
            else
            {
                Rebind(current, channelId, op.TagName);
                return current with { ChannelId = channelId };
            }

            return current;
        }

        Rebind(current, null, op.TagName);
        return current with { ChannelId = null };
    }

    private TagSnapshot? ApplyDelete(DeleteTagOp op, TagSnapshot current, AddViolation add)
    {
        if (op.Old is { } expected && !expected.Equals(current))
        {
            add(ViolationCodes.OldValueMismatch, null, $"Tag '{op.TagName}' no longer matches the snapshot the delete expected.");
        }

        if (current.Limits.Count > 0)
        {
            add(ViolationCodes.DeleteTagWithAlarms, null, $"Tag '{op.TagName}' has {current.Limits.Count} alarm limit(s) that will be deleted.",
                Severity.Warning);
        }

        Rebind(current, null, op.TagName);
        return null;
    }

    private string? OwnerOf(ChannelInfo channel) =>
        channelOwner.TryGetValue(channel.Id, out var owner) ? owner : channel.BoundTagName;

    private void Rebind(TagSnapshot current, int? newChannel, string tagName)
    {
        if (current.ChannelId is { } old)
        {
            channelOwner[old] = null;
        }

        if (newChannel is { } id)
        {
            channelOwner[id] = tagName;
        }
    }

    private void Set(string tagName, TagSnapshot? snapshot)
    {
        if (!overlay.ContainsKey(tagName))
        {
            touched.Add(tagName);
        }

        overlay[tagName] = snapshot;
    }

    private delegate void AddViolation(string code, string? field, string message, Severity severity = Severity.Error);
}
