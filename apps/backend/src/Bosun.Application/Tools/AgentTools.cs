using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using Bosun.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Application.Tools;

// The tool catalogue (plan s6.2). Argument records are the single source of truth: they produce the function declaration
// the model sees and the JSON schema the arguments are validated against. Write tools only ever append ops to the run's
// DRAFT change set through IAgentDraft; nothing in this file can touch a tag, approve or apply (plan s7.1).

/// <summary>Strips control characters and caps the length of free text from the database before it reaches the model.</summary>
public static class ToolText
{
    public const int MaxDescriptionChars = 120;

    public static string? Clean(string? text, int max = MaxDescriptionChars)
    {
        if (text is null)
        {
            return null;
        }

        var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..max] + "…";
    }
}

// ---------- shared result shapes ----------

public sealed record TagView(
    string TagName,
    string? Description,
    SignalType SignalType,
    string? Unit,
    double? RangeLow,
    double? RangeHigh,
    SafetyClass SafetyClass,
    string? AlarmGroup,
    IReadOnlyList<string> Alarms)
{
    public static TagView From(TagDto t) => new(
        t.TagName,
        ToolText.Clean(t.Description),
        t.SignalType,
        t.Unit,
        t.RangeLow,
        t.RangeHigh,
        t.SafetyClass,
        t.AlarmGroup,
        t.AlarmLimits.Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Level} {l.Value} (delay {l.DelayS} s, hysteresis {l.Hysteresis})")).ToList());
}

public sealed record NoArgs;

// ---------- read tools ----------

public sealed record SearchTagsArgs(
    [property: Description("Words that must ALL appear in the tag name or description, for example 'exhaust gas temperature' or 'ME-EXH-TT'. Case-insensitive substring match, no synonyms.")]
    [property: MinLength(2)]
    [property: MaxLength(120)]
    string Query,
    [property: Description("Optional system prefix to restrict the search, for example ME, AE, BLR, PMS, BWTS, TANK.")]
    [property: MaxLength(20)]
    string? System = null,
    [property: Description("Maximum number of tags to return (default 20, at most 50).")]
    [property: Range(1, 50)]
    int? Limit = null);

public sealed class SearchTagsTool(TagQueries tags) : AgentTool<SearchTagsArgs>
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxTerms = 6;

    public override string Name => ToolNames.SearchTags;

    public override string Description =>
        "Find tags by words in their name or description. Returns matching tags (up to limit) with unit, range, alarm group and alarm limits, plus the total number of matches. Not a semantic search: if nothing matches, retry with fewer or different words.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/projects/{projectId}/tags"];

    protected override async Task<ToolResult> RunAsync(SearchTagsArgs args, ToolContext context, CancellationToken ct)
    {
        var terms = args.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxTerms).ToList();
        if (terms.Count == 0)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, "query must contain at least one word.");
        }

        var limit = Math.Clamp(args.Limit ?? DefaultLimit, 1, MaxLimit);
        var (items, total) = await tags.SearchAsync(context.ProjectId, terms, args.System, limit, ct);
        return ToolResult.Success(new
        {
            total_matches = total,
            returned = items.Count,
            truncated = total > items.Count,
            tags = items.Select(TagView.From).ToList(),
        });
    }
}

public sealed record GetTagsArgs(
    [property: Description("Exact tag names, for example ['ME-EXH-TT01'].")]
    [property: MinLength(1)]
    [property: MaxLength(GetTagsTool.MaxNames)]
    IReadOnlyList<string> TagNames);

public sealed class GetTagsTool(TagQueries tags) : AgentTool<GetTagsArgs>
{
    public const int MaxNames = 25;

    public override string Name => ToolNames.GetTags;

    public override string Description =>
        "Get the full details (signal type, unit, range, safety class, alarm group, every alarm limit) of tags by exact tag name. Unknown names are listed in not_found.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/projects/{projectId}/tags"];

    protected override async Task<ToolResult> RunAsync(GetTagsArgs args, ToolContext context, CancellationToken ct)
    {
        var names = args.TagNames.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0 || names.Any(n => n.Length > 100))
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, "tag_names must contain tag names of 1 to 100 characters.");
        }

        var found = await tags.GetByNamesAsync(context.ProjectId, names, ct);
        var foundNames = found.Select(t => t.TagName).ToHashSet(StringComparer.Ordinal);
        return ToolResult.Success(new
        {
            tags = found.Select(TagView.From).ToList(),
            not_found = names.Where(n => !foundNames.Contains(n)).ToList(),
        });
    }
}

public sealed class ListAlarmGroupsTool(ProjectQueries projects) : AgentTool<NoArgs>
{
    public override string Name => ToolNames.ListAlarmGroups;

    public override string Description => "List the project's alarm groups with the number of tags in each. Group names are case-sensitive.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/projects/{projectId}/alarm-groups"];

    protected override async Task<ToolResult> RunAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        var groups = await projects.ListAlarmGroupsAsync(context.ProjectId, ct);
        return ToolResult.Success(new { alarm_groups = groups.Select(g => new { name = g.Name, tag_count = g.TagCount }).ToList() });
    }
}

public sealed class ListControllersTool(ProjectQueries projects) : AgentTool<NoArgs>
{
    public override string Name => ToolNames.ListControllers;

    public override string Description => "List the controllers with a summary of their I/O: modules, channels and bound channels per signal type. Individual modules and channels are not listed.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/projects/{projectId}/controllers"];

    protected override async Task<ToolResult> RunAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        var controllers = await projects.ListControllersAsync(context.ProjectId, ct);
        return ToolResult.Success(new
        {
            controllers = controllers.Select(c => new
            {
                name = c.Name,
                model = c.Model,
                module_count = c.Modules.Count,
                io = c.Modules.GroupBy(m => m.SignalType).OrderBy(g => g.Key).Select(g => new
                {
                    signal_type = g.Key,
                    modules = g.Count(),
                    channels = g.Sum(m => m.ChannelCount),
                    bound_channels = g.Sum(m => m.BoundChannelCount),
                }).ToList(),
            }).ToList(),
        });
    }
}

// ---------- write tools (draft only) ----------

/// <summary>The scalar tag fields propose_update_tag may change (the names TagFields accepts).</summary>
public enum TagField
{
    [JsonStringEnumMemberName("description")] Description,
    [JsonStringEnumMemberName("unit")] Unit,
    [JsonStringEnumMemberName("signalType")] SignalType,
    [JsonStringEnumMemberName("rangeLow")] RangeLow,
    [JsonStringEnumMemberName("rangeHigh")] RangeHigh,
    [JsonStringEnumMemberName("safetyClass")] SafetyClass,
}

internal static class ProposeSupport
{
    public const int MaxTagsPerCall = 200;

    public static List<string>? CleanNames(IReadOnlyList<string> names, out string? problem)
    {
        var cleaned = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        problem = cleaned.Count == 0 || cleaned.Any(n => n.Length > 100) ? "tag_names must contain tag names of 1 to 100 characters." : null;
        return problem is null ? cleaned : null;
    }

    public static async Task<ToolResult> AppendAsync(ToolContext context, IReadOnlyList<ChangeOp> ops, CancellationToken ct)
    {
        var result = await context.Draft.AppendAsync(ops, ct);
        if (!result.Ok)
        {
            var details = result.Errors.Take(10).Select(e => $"{e.Code} {e.TagName}: {e.Message}").ToList();
            if (result.Errors.Count > 10)
            {
                details.Add($"... and {result.Errors.Count - 10} more.");
            }

            return ToolResult.Error(
                ToolErrorCodes.Rejected,
                "Nothing was added to the draft because at least one operation is not possible. Correct the arguments (tag names: use search_tags; group names: use list_alarm_groups) and call the tool again.",
                details);
        }

        var summary = await context.Draft.CheckAsync(ct);
        return ToolResult.Success(new
        {
            added = result.Added,
            draft_op_count = result.Total,
            requires_second_approver = summary.RequiresSecondApprover,
        });
    }
}

public sealed record ProposeCreateTagArgs(
    [property: Description("New tag name, format {SYSTEM}-{EQUIP}-{MEASURE}{NN}, for example ME-EXH-TT09.")]
    [property: MinLength(5)]
    [property: MaxLength(100)]
    string TagName,
    [property: Description("AI/AO analog input/output, DI/DO digital input/output.")]
    SignalType SignalType,
    [property: MaxLength(500)] string? Description = null,
    [property: Description("Engineering unit of an analog tag, for example °C or bar.")][property: MaxLength(20)] string? Unit = null,
    [property: Description("Analog tags only.")] double? RangeLow = null,
    [property: Description("Analog tags only.")] double? RangeHigh = null,
    [property: Description("NONE unless the user says the tag is safety-classified.")] SafetyClass? SafetyClass = null,
    [property: Description("Existing alarm group name.")][property: MaxLength(100)] string? AlarmGroup = null);

public sealed class ProposeCreateTagTool : AgentTool<ProposeCreateTagArgs>
{
    public override string Name => ToolNames.ProposeCreateTag;

    public override string Description =>
        "Add a createTag operation to the draft. Alarm limits are separate: call propose_set_alarm_limit afterwards. Nothing is applied; a human reviews the draft.";

    public override ToolKind Kind => ToolKind.WriteDraft;

    public override IReadOnlyList<string> Routes => ["POST /api/change-sets/{id}/ops"];

    protected override Task<ToolResult> RunAsync(ProposeCreateTagArgs args, ToolContext context, CancellationToken ct) =>
        ProposeSupport.AppendAsync(
            context,
            [
                new CreateTagOp
                {
                    TagName = args.TagName.Trim(),
                    SignalType = args.SignalType,
                    Description = args.Description,
                    Unit = args.Unit,
                    RangeLow = args.RangeLow,
                    RangeHigh = args.RangeHigh,
                    SafetyClass = args.SafetyClass ?? Domain.SafetyClass.None,
                    AlarmGroup = string.IsNullOrWhiteSpace(args.AlarmGroup) ? null : args.AlarmGroup.Trim(),
                },
            ],
            ct);
}

public sealed record ProposeUpdateTagArgs(
    [property: Description("Existing tags to change (tag names).")]
    [property: MinLength(1)]
    [property: MaxLength(ProposeSupport.MaxTagsPerCall)]
    IReadOnlyList<string> TagNames,
    [property: Description("The field to change.")] TagField Field,
    [property: Description("The new value as text; numbers in plain decimal (95.5); an empty string clears an optional field.")]
    [property: MaxLength(500)]
    string Value);

public sealed class ProposeUpdateTagTool : AgentTool<ProposeUpdateTagArgs>
{
    public override string Name => ToolNames.ProposeUpdateTag;

    public override string Description =>
        "Add updateTag operations to the draft: set ONE field to the same new value on one or more existing tags. For alarm limits use propose_set_alarm_limit, for groups propose_assign_group.";

    public override ToolKind Kind => ToolKind.WriteDraft;

    public override IReadOnlyList<string> Routes => ["POST /api/change-sets/{id}/ops"];

    protected override Task<ToolResult> RunAsync(ProposeUpdateTagArgs args, ToolContext context, CancellationToken ct)
    {
        var names = ProposeSupport.CleanNames(args.TagNames, out var problem);
        if (names is null)
        {
            return Task.FromResult(ToolResult.Error(ToolErrorCodes.InvalidArguments, problem!));
        }

        var field = args.Field switch
        {
            TagField.Description => "description",
            TagField.Unit => "unit",
            TagField.SignalType => "signalType",
            TagField.RangeLow => "rangeLow",
            TagField.RangeHigh => "rangeHigh",
            _ => "safetyClass",
        };
        var ops = names.Select(n => (ChangeOp)new UpdateTagOp { TagName = n, Field = field, New = args.Value }).ToList();
        return ProposeSupport.AppendAsync(context, ops, ct);
    }
}

public sealed record ProposeSetAlarmLimitArgs(
    [property: Description("Existing analog tags to set the limit on (tag names).")]
    [property: MinLength(1)]
    [property: MaxLength(ProposeSupport.MaxTagsPerCall)]
    IReadOnlyList<string> TagNames,
    [property: Description("LL (low-low), L (low), H (high) or HH (high-high).")] AlarmLevel Level,
    [property: Description("The alarm threshold in the tag's engineering unit. Required unless remove is true.")] double? Value = null,
    [property: Description("On-delay in seconds before the alarm is raised. Omitted: keep the tag's current delay for this level (0 for a new limit).")]
    [property: Range(0, 86400)]
    double? DelayS = null,
    [property: Description("Hysteresis in engineering units. Omitted: keep the current value for this level (0 for a new limit).")]
    [property: Range(0, 1000000)]
    double? Hysteresis = null,
    [property: Description("true deletes the limit of this level instead of setting it.")] bool? Remove = null);

public sealed class ProposeSetAlarmLimitTool(TagQueries tags) : AgentTool<ProposeSetAlarmLimitArgs>
{
    public override string Name => ToolNames.ProposeSetAlarmLimit;

    public override string Description =>
        "Add setAlarmLimit operations to the draft: set the alarm limit of one level (LL, L, H or HH) to the same threshold and delay on one or more existing analog tags. Replaces an existing limit of that level.";

    public override ToolKind Kind => ToolKind.WriteDraft;

    public override IReadOnlyList<string> Routes => ["POST /api/change-sets/{id}/ops"];

    protected override async Task<ToolResult> RunAsync(ProposeSetAlarmLimitArgs args, ToolContext context, CancellationToken ct)
    {
        var names = ProposeSupport.CleanNames(args.TagNames, out var problem);
        if (names is null)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, problem!);
        }

        var remove = args.Remove == true;
        if (!remove && args.Value is not { } value)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, "value is required unless remove is true.");
        }

        if (!remove && !double.IsFinite(args.Value!.Value))
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, "value must be a finite number.");
        }

        // Omitted delay/hysteresis keep what the tag has today for this level.
        var existing = remove
            ? new Dictionary<string, AlarmLimitDto>()
            : (await tags.GetByNamesAsync(context.ProjectId, names, ct))
                .Select(t => (t.TagName, Limit: t.AlarmLimits.FirstOrDefault(l => l.Level == args.Level)))
                .Where(x => x.Limit is not null)
                .ToDictionary(x => x.TagName, x => x.Limit!, StringComparer.Ordinal);

        var ops = names.Select(n =>
        {
            existing.TryGetValue(n, out var current);
            return (ChangeOp)new SetAlarmLimitOp
            {
                TagName = n,
                Level = args.Level,
                New = remove ? null : new AlarmLimitValue(args.Value!.Value, args.DelayS ?? current?.DelayS ?? 0, args.Hysteresis ?? current?.Hysteresis ?? 0),
            };
        }).ToList();
        return await ProposeSupport.AppendAsync(context, ops, ct);
    }
}

public sealed record ProposeAssignGroupArgs(
    [property: Description("Existing tags to put into the group (tag names).")]
    [property: MinLength(1)]
    [property: MaxLength(ProposeSupport.MaxTagsPerCall)]
    IReadOnlyList<string> TagNames,
    [property: Description("Name of an EXISTING alarm group, exactly as listed by list_alarm_groups. An empty string removes the tags from their group.")]
    [property: MaxLength(100)]
    string AlarmGroup);

public sealed class ProposeAssignGroupTool : AgentTool<ProposeAssignGroupArgs>
{
    public override string Name => ToolNames.ProposeAssignGroup;

    public override string Description =>
        "Add assignGroup operations to the draft: put one or more existing tags into an existing alarm group.";

    public override ToolKind Kind => ToolKind.WriteDraft;

    public override IReadOnlyList<string> Routes => ["POST /api/change-sets/{id}/ops"];

    protected override Task<ToolResult> RunAsync(ProposeAssignGroupArgs args, ToolContext context, CancellationToken ct)
    {
        var names = ProposeSupport.CleanNames(args.TagNames, out var problem);
        if (names is null)
        {
            return Task.FromResult(ToolResult.Error(ToolErrorCodes.InvalidArguments, problem!));
        }

        var group = string.IsNullOrWhiteSpace(args.AlarmGroup) ? null : args.AlarmGroup.Trim();
        return ProposeSupport.AppendAsync(context, names.Select(n => (ChangeOp)new AssignGroupOp { TagName = n, New = group }).ToList(), ct);
    }
}

// ---------- draft inspection ----------

public sealed class ValidateDraftTool : AgentTool<NoArgs>
{
    public const int MaxViolations = 25;

    public override string Name => ToolNames.ValidateDraft;

    public override string Description =>
        "Run the deterministic rule check on the draft so far. Returns valid plus the violations (severity error must be fixed with further propose_* calls; warning and info are advisory). Changes nothing.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["POST /api/change-sets/{id}/validate"];

    protected override async Task<ToolResult> RunAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        var result = await context.Draft.CheckAsync(ct);
        return ToolResult.Success(new
        {
            valid = result.Valid,
            op_count = context.Draft.OpCount,
            requires_second_approver = result.RequiresSecondApprover,
            violation_count = result.Violations.Count,
            violations = result.Violations.Take(MaxViolations).Select(v => new
            {
                code = v.Code,
                severity = v.Severity.ToString().ToLowerInvariant(),
                tag_name = v.TagName,
                field = v.Field,
                message = v.Message,
            }).ToList(),
        });
    }
}

public sealed class ShowDraftSummaryTool : AgentTool<NoArgs>
{
    public override string Name => ToolNames.ShowDraftSummary;

    public override string Description =>
        "Show what the reviewing human will see: the number of operations and a grouped list of what the draft changes. Changes nothing.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/change-sets/{id}"];

    protected override async Task<ToolResult> RunAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        var summary = await context.Draft.SummaryAsync(ct);
        return ToolResult.Success(new
        {
            op_count = summary.OpCount,
            requires_second_approver = summary.RequiresSecondApprover,
            changes = summary.Lines.Select(l => new { type = l.Type, change = l.Change, tag_count = l.TagCount, tags = l.Tags, more_tags = l.MoreTags }).ToList(),
        });
    }
}

public static class AgentToolRegistration
{
    /// <summary>
    /// The one tool catalogue. To add a tool (Phase 6: <c>search_docs</c>) register another <see cref="IAgentTool"/>;
    /// the agent and the MCP server both pick it up from <see cref="ToolRegistry"/>.
    /// </summary>
    public static IServiceCollection AddBosunTools(this IServiceCollection services)
    {
        services.AddScoped<IAgentTool, SearchTagsTool>();
        services.AddScoped<IAgentTool, GetTagsTool>();
        services.AddScoped<IAgentTool, ListAlarmGroupsTool>();
        services.AddScoped<IAgentTool, ListControllersTool>();
        services.AddScoped<IAgentTool, ProposeCreateTagTool>();
        services.AddScoped<IAgentTool, ProposeUpdateTagTool>();
        services.AddScoped<IAgentTool, ProposeSetAlarmLimitTool>();
        services.AddScoped<IAgentTool, ProposeAssignGroupTool>();
        services.AddScoped<IAgentTool, ValidateDraftTool>();
        services.AddScoped<IAgentTool, ShowDraftSummaryTool>();
        services.AddScoped<ToolRegistry>();
        return services;
    }
}

/// <summary>The draft of one agent run: created lazily on the first write, always source=agent with server-set provenance.</summary>
public sealed class AgentDraftSession(ChangeSetService changeSets, int projectId, int agentRunId, string title, string? model, string? promptVersion) : IAgentDraft
{
    private const int MaxTagsPerLine = 10;
    private int? changeSetId;
    private int opCount;

    public int? ChangeSetId => changeSetId;

    public int OpCount => opCount;

    public string Title { get; set; } = title;

    public string? Model { get; set; } = model;

    public async Task<DraftAppendResult> AppendAsync(IReadOnlyList<ChangeOp> ops, CancellationToken ct)
    {
        if (changeSetId is null)
        {
            var created = await changeSets.CreateAgentDraftAsync(projectId, Title, agentRunId, Model, promptVersion, ct);
            changeSetId = created.Id;
        }

        var outcome = await changeSets.TryAddOpsAsync(changeSetId.Value, ops.Select(o => new OpInput(o, OldProvided: false)).ToList(), ct);
        if (outcome.ChangeSet is { } cs)
        {
            opCount = cs.Ops.Count;
            return new DraftAppendResult(ops.Count, opCount, []);
        }

        return new DraftAppendResult(0, opCount, outcome.Errors);
    }

    public async Task<ValidationResultDto> CheckAsync(CancellationToken ct) =>
        changeSetId is { } id
            ? await changeSets.CheckAsync(id, ct)
            : new ValidationResultDto(0, ChangeSetStatus.Draft, false, false,
                [new ViolationDto("EMPTY_CHANGE_SET", Rules.Severity.Error, null, null, "The draft has no operations yet.")]);

    public async Task<DraftSummary> SummaryAsync(CancellationToken ct)
    {
        if (changeSetId is not { } id)
        {
            return new DraftSummary(null, 0, false, []);
        }

        var cs = await changeSets.GetAsync(id, ct);
        var lines = new List<DraftSummaryLine>();
        foreach (var group in cs.Ops.GroupBy(Describe))
        {
            var tags = group.Select(o => o.TagName).Distinct(StringComparer.Ordinal).ToList();
            var (type, change) = group.Key;
            lines.Add(new DraftSummaryLine(type, change, tags.Count, tags.Take(MaxTagsPerLine).ToList(), Math.Max(0, tags.Count - MaxTagsPerLine)));
        }

        return new DraftSummary(id, cs.Ops.Count, cs.RequiresSecondApprover, lines);
    }

    private static (string Type, string Change) Describe(ChangeOp op) => op switch
    {
        CreateTagOp c => (op.TypeName, $"create {c.SignalType} {c.Unit}".Trim() + $" [{op.TagName}]"),
        UpdateTagOp u => (op.TypeName, $"{u.Field}: '{u.Old}' -> '{u.New}'"),
        SetAlarmLimitOp s => (op.TypeName, s.New is null
            ? $"remove {s.Level} limit"
            : $"{s.Level} = {s.New.Value.ToString("R", CultureInfo.InvariantCulture)}, delay {s.New.DelayS.ToString("R", CultureInfo.InvariantCulture)} s, hysteresis {s.New.Hysteresis.ToString("R", CultureInfo.InvariantCulture)}"),
        AssignGroupOp a => (op.TypeName, a.New is null ? "remove from alarm group" : $"alarm group -> {a.New}"),
        _ => (op.TypeName, op.TypeName),
    };
}
