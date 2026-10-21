namespace Bosun.Domain.Entities;

/// <summary>Base for persisted aggregates. <see cref="Version"/> is the app-incremented concurrency token (plan s5).</summary>
public abstract class VersionedEntity
{
    public int Id { get; set; }

    /// <summary>Starts at 1, incremented by the DbContext on every update. Works the same on SQLite and Postgres.</summary>
    public int Version { get; set; }
}

public sealed class Vessel : VersionedEntity
{
    public string Name { get; set; } = "";
    public string? ImoNumber { get; set; }
    public List<Project> Projects { get; set; } = [];
}

public sealed class Project : VersionedEntity
{
    public int VesselId { get; set; }
    public Vessel? Vessel { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Controller> Controllers { get; set; } = [];
    public List<AlarmGroup> AlarmGroups { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}

public sealed class Controller : VersionedEntity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public string? Model { get; set; }
    public string? IpAddress { get; set; }
    public List<IoModule> Modules { get; set; } = [];
}

public sealed class IoModule : VersionedEntity
{
    public int ControllerId { get; set; }
    public Controller? Controller { get; set; }
    public string Name { get; set; } = "";
    public int Slot { get; set; }
    public SignalType SignalType { get; set; }
    public List<Channel> Channels { get; set; } = [];
}

public sealed class Channel : VersionedEntity
{
    public int IoModuleId { get; set; }
    public IoModule? IoModule { get; set; }

    /// <summary>1-based channel number inside the module.</summary>
    public int Number { get; set; }
    public SignalType SignalType { get; set; }
}

public sealed class AlarmGroup : VersionedEntity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class Tag : VersionedEntity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Unique per project. Convention <c>{SYSTEM}-{EQUIP}-{MEASURE}{NN}</c>, e.g. ME-EXH-TT01.</summary>
    public string TagName { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>First segment of <see cref="TagName"/> (denormalised for filtering).</summary>
    public string System { get; set; } = "";

    /// <summary>Second segment of <see cref="TagName"/> (denormalised for filtering).</summary>
    public string Equipment { get; set; } = "";
    public SignalType SignalType { get; set; }
    public string? Unit { get; set; }
    public double? RangeLow { get; set; }
    public double? RangeHigh { get; set; }
    public SafetyClass SafetyClass { get; set; }
    public int? AlarmGroupId { get; set; }
    public AlarmGroup? AlarmGroup { get; set; }

    /// <summary>0..1 channel binding; unique when set.</summary>
    public int? ChannelId { get; set; }
    public Channel? Channel { get; set; }
    public List<AlarmLimit> AlarmLimits { get; set; } = [];
}

public sealed class AlarmLimit : VersionedEntity
{
    public int TagId { get; set; }
    public Tag? Tag { get; set; }
    public AlarmLevel Level { get; set; }
    public double Value { get; set; }
    public double DelayS { get; set; }
    public double Hysteresis { get; set; }
}

public sealed class ChangeSet : VersionedEntity
{
    public int ProjectId { get; set; }
    public string Title { get; set; } = "";
    public ChangeSetSource Source { get; set; }
    public ChangeSetStatus Status { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    /// <summary>Typed operations, stored as one JSON column with a "type" discriminator.</summary>
    public List<ChangeOp> Ops { get; set; } = [];

    public bool RequiresSecondApprover { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? SecondApprovedBy { get; set; }
    public DateTime? SecondApprovedAt { get; set; }
    public string? AppliedBy { get; set; }
    public DateTime? AppliedAt { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectReason { get; set; }

    // Provenance of AI-proposed change sets; copied to every AuditEntry on apply.
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>The agent run that drafted this change set (FK to agent_runs, set server-side only).</summary>
    public int? AgentRunId { get; set; }
}

/// <summary>Append-only record of an applied operation. Not versioned: never updated.</summary>
public sealed class AuditEntry
{
    public int Id { get; set; }
    public int ProjectId { get; set; }

    /// <summary>Who performed the audited action. Always "user": applying needs a human session (plan s7.1).</summary>
    public ActorType ActorType { get; set; }

    /// <summary>Who proposed the change: "agent" for an agent-drafted change set, otherwise "user". The human applier stays in <see cref="Actor"/>.</summary>
    public ActorType ProposedBy { get; set; }

    /// <summary>The human that approved/applied the change (always a person, even for agent proposals).</summary>
    public string Actor { get; set; } = "";

    /// <summary>Everyone who approved the change set ("alice" or "alice, bob" for SIL/ESD second approval).</summary>
    public string? ApprovedBy { get; set; }

    /// <summary>Model that proposed the change (agent proposals only).</summary>
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public int? AgentRunId { get; set; }
    public int? ChangeSetId { get; set; }
    public string? TagName { get; set; }
    public string OpType { get; set; } = "";

    /// <summary>JSON of the applied op including its old value.</summary>
    public string DiffJson { get; set; } = "{}";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// One call to an LLM or embedding provider (append-only, never updated). Backs <c>GET /api/ai/usage</c>, the eval
/// cost summary and the admin console's token/cost panel (plan s9). Never holds prompts, responses or keys.
/// </summary>
public sealed class LlmCall
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }

    /// <summary>"llm" or "embedding".</summary>
    public string Kind { get; set; } = "llm";

    /// <summary>Product feature that made the call: "ping", "agent.plan", "extraction.text", "rag.embed" ...</summary>
    public string Feature { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";

    /// <summary>"{prompt name}@{short SHA-256}" of the prompt file(s) used (plan s9).</summary>
    public string? PromptVersion { get; set; }
    public int PromptTokens { get; set; }
    public int CandidateTokens { get; set; }

    /// <summary>Reasoning tokens: billed as output and counted against maxOutputTokens (plan s12).</summary>
    public int ThoughtTokens { get; set; }
    public int TotalTokens { get; set; }
    public int LatencyMs { get; set; }

    /// <summary>HTTP attempts made for this call (1 = no retry).</summary>
    public int Attempts { get; set; }
    public string? FinishReason { get; set; }
    public bool Succeeded { get; set; }

    /// <summary>Stable error code (llm_rate_limited ...) when <see cref="Succeeded"/> is false.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>ESTIMATE from the configurable price table (llm-pricing.json), in USD. The free tier bills nothing.</summary>
    public double CostUsd { get; set; }
    public int? AgentRunId { get; set; }
}

/// <summary>
/// One run of the agent (plan s6.2): the user's instruction, how it ended, what it spent. The draft it produced is
/// <see cref="ChangeSetId"/> (a plain id: the foreign key points the other way, from change_sets.agent_run_id).
/// </summary>
public sealed class AgentRun
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The instruction, verbatim (at most 2,000 characters).</summary>
    public string Message { get; set; } = "";
    public AgentRunStatus Status { get; set; }

    /// <summary>"planning", "acting" or "verifying" while running; null once finished.</summary>
    public string? Phase { get; set; }

    /// <summary>The human who started the run.</summary>
    public string CreatedBy { get; set; } = "";
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Tool calls executed (the budget is <see cref="MaxToolCalls"/>).</summary>
    public int ToolCalls { get; set; }
    public int MaxToolCalls { get; set; }
    public int ElapsedMs { get; set; }
    public int MaxTotalTokens { get; set; }
    public int PromptTokens { get; set; }
    public int CandidateTokens { get; set; }
    public int ThoughtTokens { get; set; }
    public int TotalTokens { get; set; }
    public int? ChangeSetId { get; set; }

    /// <summary>"draft", "clarification", "refusal" or "no_changes" once the agent finished its work.</summary>
    public string? ResultKind { get; set; }

    /// <summary>The agent's closing message: a summary, the clarifying question or the reason for the refusal.</summary>
    public string? ResultMessage { get; set; }

    /// <summary>Stable error code (llm_rate_limited ..., budget_*, internal_error) for failed and budget-stopped runs.</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public List<AgentStep> Steps { get; set; } = [];
}

/// <summary>One persisted step of a run, replayable in the admin console. Append-only.</summary>
public sealed class AgentStep
{
    public int Id { get; set; }
    public int RunId { get; set; }
    public AgentRun? Run { get; set; }

    /// <summary>1-based position inside the run.</summary>
    public int Seq { get; set; }
    public AgentStepKind Kind { get; set; }

    /// <summary>The step's JSON document (shape depends on <see cref="Kind"/>).</summary>
    public string PayloadJson { get; set; } = "{}";
    public int Tokens { get; set; }
    public int LatencyMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>One execution of an evaluation suite (plan s9): backs the admin console's pass-rate trend.</summary>
public sealed class EvalRun
{
    public int Id { get; set; }
    public string Suite { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Model { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public int RunsPerCase { get; set; }
    public int Cases { get; set; }
    public int Attempts { get; set; }
    public int Passed { get; set; }

    /// <summary>Passed / Attempts, 0..1.</summary>
    public double PassRate { get; set; }
    public int PromptTokens { get; set; }
    public int CandidateTokens { get; set; }
    public int ThoughtTokens { get; set; }

    /// <summary>ESTIMATE in USD from the price table.</summary>
    public double CostUsd { get; set; }

    /// <summary>Relative path of the JSON results file, if one was written.</summary>
    public string? ResultsFile { get; set; }
    public List<EvalResult> Results { get; set; } = [];
}

public sealed class EvalResult
{
    public int Id { get; set; }
    public int EvalRunId { get; set; }
    public EvalRun? EvalRun { get; set; }
    public string CaseId { get; set; } = "";
    public string Category { get; set; } = "";

    /// <summary>1-based repetition of the case inside the run.</summary>
    public int Attempt { get; set; }
    public bool Passed { get; set; }

    /// <summary>"ops_match", "alternative_match", "clarified", "refused", "wrong_ops", "unexpected_ops" ...</summary>
    public string Outcome { get; set; } = "";
    public string? Detail { get; set; }
    public string? AgentStatus { get; set; }
    public string? ErrorCode { get; set; }
    public int ToolCalls { get; set; }
    public int Tokens { get; set; }
    public int LatencyMs { get; set; }
    public int? AgentRunId { get; set; }
}

/// <summary>
/// One searchable passage of a project document (plan s6.4): a heading-bounded slice of a Markdown file with the citation
/// fields (<see cref="Source"/>, <see cref="Section"/>, <see cref="Page"/>). The embedding is a little-endian float32[768],
/// L2-normalised, stored as a BLOB (the SQLite vector store scans these in process; Phase 8 moves them to pgvector).
/// <see cref="ContentHash"/> is the SHA-256 of the exact text that was embedded, so an unchanged chunk is never embedded twice.
/// </summary>
public sealed class KnowledgeChunk
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>Document id for citations: the file name without extension, for example "alarm-philosophy".</summary>
    public string Source { get; set; } = "";

    /// <summary>Heading text of the section this chunk belongs to.</summary>
    public string Section { get; set; } = "";

    /// <summary>1-based page of the deterministic pagination of the source (see Chunker.PageChars).</summary>
    public int Page { get; set; }

    /// <summary>Position of the chunk inside its source (0-based, document order).</summary>
    public int Ordinal { get; set; }

    public string Text { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public byte[]? Embedding { get; set; }
    public string? EmbeddingModel { get; set; }
    public DateTime IndexedAt { get; set; }
}

/// <summary>
/// One alarm occurrence (a tag crossing one limit level and staying across it for the limit's delay). Holds the current state;
/// the history of how it got there is in <see cref="AlarmEvent"/>. Keyed by tag NAME, not tag id, so deleting or renaming a tag
/// in a change set never rewrites or cascades into alarm history. Ids are allocated in process (see AlarmManager) because the
/// hub announces an alarm before the background writer has stored it; the writer inserts them with their explicit ids.
/// </summary>
public sealed class Alarm
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string TagName { get; set; } = "";
    public AlarmLevel Level { get; set; }

    /// <summary>The limit as it was configured when the alarm was raised (or last changed while active).</summary>
    public double LimitValue { get; set; }
    public double DelayS { get; set; }
    public double Hysteresis { get; set; }
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public AlarmState State { get; set; }
    public DateTime RaisedAt { get; set; }
    public double RaisedValue { get; set; }

    /// <summary>Worst value seen while the condition was active (highest for H/HH, lowest for L/LL).</summary>
    public double PeakValue { get; set; }
    public DateTime? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public double? ReturnedValue { get; set; }
    public DateTime? ClearedAt { get; set; }

    public Alarm Clone() => (Alarm)MemberwiseClone();
}

/// <summary>Append-only log of an alarm's transitions (raised, acknowledged, returned to normal, cleared).</summary>
public sealed class AlarmEvent
{
    public int Id { get; set; }
    public int AlarmId { get; set; }
    public Alarm? Alarm { get; set; }
    public AlarmEventKind Kind { get; set; }
    public DateTime At { get; set; }

    /// <summary>The process value at the moment of the transition (null for an acknowledgement, which has none).</summary>
    public double? Value { get; set; }

    /// <summary>The operator for an acknowledgement; null for transitions the system made.</summary>
    public string? Actor { get; set; }
    public string? Note { get; set; }
}
