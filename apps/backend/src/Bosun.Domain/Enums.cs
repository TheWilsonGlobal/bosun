using System.Text.Json.Serialization;

namespace Bosun.Domain;

/// <summary>I/O signal type of a tag or channel.</summary>
public enum SignalType
{
    [JsonStringEnumMemberName("AI")] AI,
    [JsonStringEnumMemberName("AO")] AO,
    [JsonStringEnumMemberName("DI")] DI,
    [JsonStringEnumMemberName("DO")] DO,
}

/// <summary>Alarm limit level. Numeric order is LL &lt; L &lt; H &lt; HH.</summary>
public enum AlarmLevel
{
    [JsonStringEnumMemberName("LL")] LL = 0,
    [JsonStringEnumMemberName("L")] L = 1,
    [JsonStringEnumMemberName("H")] H = 2,
    [JsonStringEnumMemberName("HH")] HH = 3,
}

/// <summary>Safety classification. SIL and ESD tags require a second approver (plan s7.3).</summary>
public enum SafetyClass
{
    [JsonStringEnumMemberName("NONE")] None = 0,
    [JsonStringEnumMemberName("SIL")] SIL = 1,
    [JsonStringEnumMemberName("ESD")] ESD = 2,
}

/// <summary>draft -> validated -> approved -> applied | rejected.</summary>
public enum ChangeSetStatus
{
    Draft = 0,
    Validated = 1,
    Approved = 2,
    Applied = 3,
    Rejected = 4,
}

/// <summary>Where a change set came from.</summary>
public enum ChangeSetSource
{
    Manual = 0,
    Agent = 1,
    Extraction = 2,
}

/// <summary>Who performed an audited action.</summary>
public enum ActorType
{
    User = 0,
    Agent = 1,
}

/// <summary>Lifecycle of an agent run (plan s6.2). Everything except <see cref="Running"/> is terminal.</summary>
public enum AgentRunStatus
{
    [JsonStringEnumMemberName("running")] Running = 0,

    /// <summary>The agent built a draft change set; a human reviews it. The agent can never go further than this.</summary>
    [JsonStringEnumMemberName("awaiting_review")] AwaitingReview = 1,

    /// <summary>The agent ended with a question for the user instead of ops.</summary>
    [JsonStringEnumMemberName("needs_clarification")] NeedsClarification = 2,

    /// <summary>The agent ended with a refusal (impossible or unsafe request), or with nothing to change.</summary>
    [JsonStringEnumMemberName("refused")] Refused = 3,

    /// <summary>A budget (tool calls, wall clock, tokens) ran out; the partial draft, if any, is kept.</summary>
    [JsonStringEnumMemberName("budget_exceeded")] BudgetExceeded = 4,
    [JsonStringEnumMemberName("failed")] Failed = 5,
    [JsonStringEnumMemberName("cancelled")] Cancelled = 6,
}

/// <summary>Kinds of persisted agent step (plan s6.2).</summary>
public enum AgentStepKind
{
    [JsonStringEnumMemberName("plan")] Plan = 0,
    [JsonStringEnumMemberName("tool_call")] ToolCall = 1,
    [JsonStringEnumMemberName("tool_result")] ToolResult = 2,
    [JsonStringEnumMemberName("verify")] Verify = 3,
    [JsonStringEnumMemberName("final")] Final = 4,
    [JsonStringEnumMemberName("error")] Error = 5,
}

public static class SafetyClassExtensions
{
    public static bool RequiresSecondApprover(this SafetyClass value) => value is SafetyClass.SIL or SafetyClass.ESD;
}
