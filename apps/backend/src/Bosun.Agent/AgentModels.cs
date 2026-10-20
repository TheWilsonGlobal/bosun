using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bosun.Application.Tools;
using Bosun.Domain;

namespace Bosun.Agent;

/// <summary>"Agent" configuration section: the budgets of plan s6.2 and s7.4. Every limit has a default; none is optional to enforce.</summary>
public sealed class AgentOptions
{
    /// <summary>Maximum executed tool calls per run (finish_task does not count).</summary>
    public int MaxToolCalls { get; set; } = 12;

    /// <summary>
    /// Wall clock for ACT plus the repair rounds, counted from the start of ACT (the plan call is NOT counted). Real Gemini calls
    /// take 14-50 s each, so the default leaves room for several. Config key <c>Agent:ActWallClockSeconds</c>
    /// (environment variable <c>Agent__ActWallClockSeconds</c>).
    /// </summary>
    public double ActWallClockSeconds { get; set; } = 120;

    /// <summary>Total tokens (input + output + reasoning) over every model call of one run.</summary>
    public int MaxTotalTokens { get; set; } = 150_000;

    /// <summary>Rounds in which the model may repair rule violations found by VERIFY.</summary>
    public int MaxRepairRounds { get; set; } = 2;

    /// <summary>How often a plain-text reply (no tool call, no finish_task) is answered with a nudge before the run stops.</summary>
    public int MaxNudges { get; set; } = 1;

    public int MaxMessageLength { get; set; } = 2000;

    public double? PlanTemperature { get; set; }

    public double? ActTemperature { get; set; }
}

public enum PlanDecision
{
    [JsonStringEnumMemberName("proceed")] Proceed,
    [JsonStringEnumMemberName("clarify")] Clarify,
    [JsonStringEnumMemberName("refuse")] Refuse,
}

/// <summary>The PLAN stage output (plan s6.2): shown to the user as a checklist.</summary>
public sealed record AgentPlan(
    PlanDecision Decision,
    string Message,
    string Title,
    IReadOnlyList<string> Steps,
    [property: JsonPropertyName("affected_scope")] string AffectedScope,
    IReadOnlyList<string> Risks);

public enum FinishOutcome
{
    [JsonStringEnumMemberName("draft_ready")] DraftReady,
    [JsonStringEnumMemberName("clarify")] Clarify,
    [JsonStringEnumMemberName("refuse")] Refuse,
}

public sealed record FinishArgs(
    [property: Description("draft_ready: the draft contains what was asked. clarify: you need information from the engineer. refuse: the request is impossible or not allowed.")]
    FinishOutcome Outcome,
    [property: Description("draft_ready: one or two sentences saying what the draft does. clarify: ONE short question. refuse: the reason.")]
    [property: MinLength(1)]
    [property: MaxLength(1000)]
    string Message);

/// <summary>
/// The control tool that ends ACT. It belongs to the agent loop, not to the shared tool registry: an MCP client has no use
/// for it and it can do nothing but end the run.
/// </summary>
public static class FinishTask
{
    public const string Name = "finish_task";

    public const string Description =
        "End the task. Call it exactly once, as your last action: outcome draft_ready when the draft holds the requested changes, clarify to ask the engineer ONE question, refuse when the request is impossible or not allowed.";

    public static JsonNode Schema => ToolJson.SchemaFor<FinishArgs>();

    public static Bosun.Application.Llm.LlmToolDeclaration Declaration => new(Name, Description, Schema);
}

public static class AgentFinalKinds
{
    public const string Draft = "draft";
    public const string Clarification = "clarification";
    public const string Refusal = "refusal";
    public const string NoChanges = "no_changes";
    public const string BudgetExceeded = "budget_exceeded";
    public const string Cancelled = "cancelled";
}

public static class AgentErrorCodes
{
    public const string BudgetToolCalls = "budget_tool_calls";
    public const string BudgetWallClock = "budget_wall_clock";
    public const string BudgetTokens = "budget_tokens";
    public const string InternalError = "internal_error";
    public const string ServerRestarted = "server_restarted";

    /// <summary>Cancel on a run that already ended (409).</summary>
    public const string RunAlreadyFinished = "run_already_finished";

    /// <summary>Cancel on a run still marked running that this process no longer holds (409).</summary>
    public const string RunNotActive = "run_not_active";
}

// ---------- DTOs (REST + SignalR) ----------

public sealed record AgentTokensDto(int Prompt, int Candidate, int Thought, int Total);

public sealed record AgentStepDto(int Seq, AgentStepKind Kind, JsonNode? Payload, int Tokens, int LatencyMs, DateTime CreatedAt);

public sealed record AgentRunDto(
    int Id,
    int ProjectId,
    string Message,
    AgentRunStatus Status,
    string? Phase,
    string CreatedBy,
    string? Model,
    string? PromptVersion,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int ToolCalls,
    int MaxToolCalls,
    int ElapsedMs,
    AgentTokensDto Tokens,
    int MaxTotalTokens,
    int? ChangeSetId,
    string? ResultKind,
    string? ResultMessage,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<AgentStepDto>? Steps);

// ---------- events (SignalR AgentHub) ----------

public sealed record AgentStepEvent(int RunId, int Seq, AgentStepKind Kind, JsonNode? Payload, int Tokens, int LatencyMs, DateTime CreatedAt);

public sealed record AgentStatusEvent(int RunId, AgentRunStatus Status, string? Phase);

public sealed record AgentDoneEvent(
    int RunId,
    int? ChangeSetId,
    AgentRunStatus Status,
    string? ResultKind,
    string? ResultMessage,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Where run progress goes. The API implements it with SignalR; tests and the eval runner use <see cref="NullAgentEventSink"/>.</summary>
public interface IAgentEventSink
{
    Task StepAsync(AgentStepEvent e);

    Task StatusAsync(AgentStatusEvent e);

    Task DoneAsync(AgentDoneEvent e);
}

public sealed class NullAgentEventSink : IAgentEventSink
{
    public Task StepAsync(AgentStepEvent e) => Task.CompletedTask;

    public Task StatusAsync(AgentStatusEvent e) => Task.CompletedTask;

    public Task DoneAsync(AgentDoneEvent e) => Task.CompletedTask;
}
