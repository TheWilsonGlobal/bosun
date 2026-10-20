using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Application;

public sealed record ProjectDto(int Id, int VesselId, string Name, string VesselName, string? ImoNumber, string? Description, int TagCount, int Version);

public sealed record AlarmLimitDto(AlarmLevel Level, double Value, double DelayS, double Hysteresis);

public sealed record TagDto(
    int Id,
    int ProjectId,
    string TagName,
    string? Description,
    string System,
    string Equipment,
    SignalType SignalType,
    string? Unit,
    double? RangeLow,
    double? RangeHigh,
    SafetyClass SafetyClass,
    int? AlarmGroupId,
    string? AlarmGroup,
    int? ChannelId,
    string? Channel,
    IReadOnlyList<AlarmLimitDto> AlarmLimits,
    bool HasAlarm,
    int Version);

public sealed record AlarmGroupDto(int Id, string Name, string? Description, int TagCount);

public sealed record ChannelDto(int Id, int Number, SignalType SignalType, string? BoundTag);

public sealed record IoModuleDto(int Id, string Name, int Slot, SignalType SignalType, int ChannelCount, int BoundChannelCount, IReadOnlyList<ChannelDto>? Channels);

public sealed record ControllerDto(int Id, string Name, string? Model, string? IpAddress, IReadOnlyList<IoModuleDto> Modules);

public sealed record TagFacetsDto(
    IReadOnlyList<string> Systems,
    IReadOnlyList<string> Units,
    IReadOnlyList<SignalType> SignalTypes,
    IReadOnlyList<SafetyClass> SafetyClasses,
    IReadOnlyList<string> AlarmGroups);

public sealed record ChangeSetSummaryDto(
    int Id,
    int ProjectId,
    string Title,
    ChangeSetSource Source,
    ChangeSetStatus Status,
    string CreatedBy,
    DateTime CreatedAt,
    int OpCount,
    bool RequiresSecondApprover,
    string? ApprovedBy,
    string? SecondApprovedBy,
    DateTime? AppliedAt,
    int Version);

public sealed record ChangeSetDto(
    int Id,
    int ProjectId,
    string Title,
    ChangeSetSource Source,
    ChangeSetStatus Status,
    string CreatedBy,
    DateTime CreatedAt,
    IReadOnlyList<ChangeOp> Ops,
    bool RequiresSecondApprover,
    bool PendingSecondApproval,
    string? ApprovedBy,
    DateTime? ApprovedAt,
    string? SecondApprovedBy,
    DateTime? SecondApprovedAt,
    string? AppliedBy,
    DateTime? AppliedAt,
    string? RejectedBy,
    DateTime? RejectedAt,
    string? RejectReason,
    string? Model,
    string? PromptVersion,
    int? AgentRunId,
    int Version);

/// <summary>Result of <see cref="ChangeSetService.TryAddOpsAsync"/>: the updated change set, or the op-level errors that kept the ops out.</summary>
public sealed record AddOpsOutcome(ChangeSetDto? ChangeSet, IReadOnlyList<ViolationDto> Errors);

public sealed record ViolationDto(string Code, Severity Severity, string? TagName, string? Field, string Message);

public sealed record ValidationResultDto(
    int ChangeSetId,
    ChangeSetStatus Status,
    bool Valid,
    bool RequiresSecondApprover,
    IReadOnlyList<ViolationDto> Violations);

public sealed record DiffRowDto(int Index, string Type, string TagName, string? Field, string? Old, string? New, ChangeOp Op);

public sealed record DiffDto(
    int ChangeSetId,
    ChangeSetStatus Status,
    bool RequiresSecondApprover,
    IReadOnlyList<DiffRowDto> Rows,
    IReadOnlyList<ViolationDto> Violations);

public sealed record AuditEntryDto(
    int Id,
    int ProjectId,
    ActorType ActorType,
    ActorType ProposedBy,
    string Actor,
    string? ApprovedBy,
    string? Model,
    string? PromptVersion,
    int? AgentRunId,
    int? ChangeSetId,
    string? TagName,
    string OpType,
    System.Text.Json.Nodes.JsonNode? Diff,
    DateTime Timestamp);

/// <summary>An op from a client plus whether it carried an explicit <c>old</c> (omitted = server fills it in).</summary>
public sealed record OpInput(ChangeOp Op, bool OldProvided);

public sealed record CreateChangeSetRequest(
    string Title,
    ChangeSetSource Source,
    IReadOnlyList<OpInput> Ops,
    string? Model = null,
    string? PromptVersion = null,
    int? AgentRunId = null);

public sealed record TagQuery
{
    public string? Text { get; init; }
    public IReadOnlyList<string> Systems { get; init; } = [];
    public IReadOnlyList<string> Units { get; init; } = [];
    public IReadOnlyList<SignalType> SignalTypes { get; init; } = [];
    public IReadOnlyList<SafetyClass> SafetyClasses { get; init; } = [];

    /// <summary>Group names; the literal "none" matches tags without a group.</summary>
    public IReadOnlyList<string> AlarmGroups { get; init; } = [];
    public bool? HasAlarm { get; init; }
    public bool? Bound { get; init; }
    public int? ControllerId { get; init; }
    public string Sort { get; init; } = "tagName";
    public bool Descending { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}
