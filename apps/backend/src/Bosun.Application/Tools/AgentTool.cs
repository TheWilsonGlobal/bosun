using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;
using Bosun.Domain;

namespace Bosun.Application.Tools;

/// <summary>Whether a tool only reads, or appends to the run's DRAFT change set. There is deliberately no third kind.</summary>
public enum ToolKind
{
    Read,
    WriteDraft,
}

/// <summary>What a tool may touch: the project it works on and the run's draft. Nothing here can apply or approve.</summary>
public sealed record ToolContext(int ProjectId, IAgentDraft Draft);

/// <summary>What the draft of a run can do for the tools. No member applies, approves or rejects: the human does that.</summary>
public interface IAgentDraft
{
    int? ChangeSetId { get; }

    int OpCount { get; }

    /// <summary>Appends ops (all or none). Creates the draft change set on first use.</summary>
    Task<DraftAppendResult> AppendAsync(IReadOnlyList<ChangeOp> ops, CancellationToken ct);

    /// <summary>Runs the rules over the draft without changing its status.</summary>
    Task<ValidationResultDto> CheckAsync(CancellationToken ct);

    Task<DraftSummary> SummaryAsync(CancellationToken ct);
}

public sealed record DraftAppendResult(int Added, int Total, IReadOnlyList<ViolationDto> Errors)
{
    public bool Ok => Errors.Count == 0;
}

public sealed record DraftSummaryLine(string Type, string Change, int TagCount, IReadOnlyList<string> Tags, int MoreTags);

public sealed record DraftSummary(int? ChangeSetId, int OpCount, bool RequiresSecondApprover, IReadOnlyList<DraftSummaryLine> Lines);

/// <summary>Outcome of one tool call as the model sees it: <c>{"ok":true,...}</c> or <c>{"ok":false,"error":{code,message,details}}</c>.</summary>
public sealed record ToolResult(bool Ok, object? Data, string? ErrorCode = null, string? Message = null, IReadOnlyList<string>? Details = null)
{
    public static ToolResult Success(object? data = null) => new(true, data);

    public static ToolResult Error(string code, string message, IReadOnlyList<string>? details = null) => new(false, null, code, message, details);
}

/// <summary>A finished tool call, ready to go back to the model.</summary>
public sealed record ToolExecution(string Name, JsonObject Response, bool Ok, string? ErrorCode, long LatencyMs);

/// <summary>Stable error codes of tool calls (returned to the model, never thrown).</summary>
public static class ToolErrorCodes
{
    public const string UnknownTool = "unknown_tool";
    public const string InvalidArguments = "invalid_arguments";
    public const string NotFound = "not_found";
    public const string Rejected = "op_rejected";
    public const string ScopeLimit = "scope_limit";
    public const string ResultTooLarge = "result_too_large";
    public const string Failed = "tool_failed";
}

public static class ToolNames
{
    public const string SearchTags = "search_tags";
    public const string GetTags = "get_tags";
    public const string ListAlarmGroups = "list_alarm_groups";
    public const string ListControllers = "list_controllers";
    public const string ProposeCreateTag = "propose_create_tag";
    public const string ProposeUpdateTag = "propose_update_tag";
    public const string ProposeSetAlarmLimit = "propose_set_alarm_limit";
    public const string ProposeAssignGroup = "propose_assign_group";
    public const string ValidateDraft = "validate_draft";
    public const string ShowDraftSummary = "show_draft_summary";
}

/// <summary>
/// One tool, defined ONCE (plan s6.2) and shared by the agent and, later, the MCP server. The declaration sent to the model
/// and the argument validation both come from <see cref="ArgsType"/>. Phase 6 adds <c>search_docs</c> by registering one more
/// <see cref="IAgentTool"/> in DI: nothing else changes.
/// </summary>
public interface IAgentTool
{
    string Name { get; }

    string Description { get; }

    ToolKind Kind { get; }

    /// <summary>The record the arguments deserialise into; the parameter schema is generated from it.</summary>
    Type ArgsType { get; }

    /// <summary>JSON Schema of the arguments, generated from <see cref="ArgsType"/>.</summary>
    JsonNode ParametersSchema { get; }

    /// <summary>
    /// The REST endpoints that do the same job (plan s7.1 test): every route reachable from the tool surface, as "METHOD pattern".
    /// None of them may write to tags or approve/apply a change set.
    /// </summary>
    IReadOnlyList<string> Routes { get; }

    /// <summary>Validates <paramref name="args"/> against <see cref="ParametersSchema"/>, then runs. Never throws for bad input.</summary>
    Task<ToolResult> InvokeAsync(JsonNode? args, ToolContext context, CancellationToken ct);
}

public abstract class AgentTool<TArgs> : IAgentTool
    where TArgs : class
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract ToolKind Kind { get; }

    public abstract IReadOnlyList<string> Routes { get; }

    public Type ArgsType => typeof(TArgs);

    public JsonNode ParametersSchema => ToolJson.SchemaFor<TArgs>();

    public async Task<ToolResult> InvokeAsync(JsonNode? args, ToolContext context, CancellationToken ct)
    {
        args ??= new JsonObject();
        if (args is not JsonObject)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, "The arguments must be a JSON object.");
        }

        var problems = JsonSchemaValidator.Validate(args, ParametersSchema);
        if (problems.Count > 0)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, $"The arguments of {Name} are invalid. Fix them and call {Name} again.", problems);
        }

        TArgs? typed;
        try
        {
            typed = args.Deserialize<TArgs>(ToolJson.Options);
        }
        catch (JsonException ex)
        {
            return ToolResult.Error(ToolErrorCodes.InvalidArguments, $"The arguments of {Name} are invalid. Fix them and call {Name} again.", [ex.Message]);
        }

        return typed is null
            ? ToolResult.Error(ToolErrorCodes.InvalidArguments, "The arguments must be a JSON object.")
            : await RunAsync(typed, context, ct);
    }

    protected abstract Task<ToolResult> RunAsync(TArgs args, ToolContext context, CancellationToken ct);
}

/// <summary>The tool surface of one scope: declarations for the model, and execution with a bounded result.</summary>
public sealed class ToolRegistry(IEnumerable<IAgentTool> tools)
{
    /// <summary>A tool result larger than this is replaced by an error: it would eat the token budget.</summary>
    public const int MaxResultChars = 20_000;

    private readonly IReadOnlyList<IAgentTool> all = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

    public IReadOnlyList<IAgentTool> Tools => all;

    public IAgentTool? Find(string name) => all.FirstOrDefault(t => t.Name == name);

    public IReadOnlyList<LlmToolDeclaration> Declarations() =>
        all.Select(t => Declare(t)).ToList();

    /// <summary>The function declaration of a tool, generated from its argument record.</summary>
    public static LlmToolDeclaration Declare(IAgentTool tool)
    {
        var schema = tool.ParametersSchema;
        var hasParameters = schema is JsonObject o && o["properties"] is JsonObject { Count: > 0 };
        return new LlmToolDeclaration(tool.Name, tool.Description, hasParameters ? schema : null);
    }

    public async Task<ToolExecution> ExecuteAsync(string name, JsonNode? args, ToolContext context, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        ToolResult result;
        if (Find(name) is not { } tool)
        {
            result = ToolResult.Error(
                ToolErrorCodes.UnknownTool,
                $"There is no tool named '{name}'.",
                [$"Available tools: {string.Join(", ", all.Select(t => t.Name))}."]);
        }
        else
        {
            try
            {
                result = await tool.InvokeAsync(args, context, ct);
            }
            catch (AppException ex)
            {
                // Expected, user-facing failures (limits, unknown project ...) are tool errors the model can read.
                result = ToolResult.Error(ex.Code == "too_many_ops" ? ToolErrorCodes.ScopeLimit : ToolErrorCodes.Failed, ex.Message);
            }
        }

        var response = ToResponse(result);
        if (response.ToJsonString().Length > MaxResultChars)
        {
            result = ToolResult.Error(
                ToolErrorCodes.ResultTooLarge,
                $"The result of {name} is too large to return. Narrow the request (fewer names, a more specific query, a smaller limit).");
            response = ToResponse(result);
        }

        return new ToolExecution(name, response, result.Ok, result.ErrorCode, watch.ElapsedMilliseconds);
    }

    private static JsonObject ToResponse(ToolResult result)
    {
        if (!result.Ok)
        {
            var error = new JsonObject { ["code"] = result.ErrorCode, ["message"] = result.Message };
            if (result.Details is { Count: > 0 })
            {
                error["details"] = new JsonArray(result.Details.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray());
            }

            return new JsonObject { ["ok"] = false, ["error"] = error };
        }

        var response = new JsonObject { ["ok"] = true };
        if (result.Data is not null && JsonSerializer.SerializeToNode(result.Data, result.Data.GetType(), ToolJson.Options) is JsonObject data)
        {
            foreach (var (key, value) in data)
            {
                response[key] = value?.DeepClone();
            }
        }

        return response;
    }
}
