using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Bosun.Application.Llm;
using Bosun.Application.Rag;

namespace Bosun.Application.Tools;

public sealed record SearchDocsArgs(
    [property: Description("A natural-language question about project rules or design, for example 'what is the standard delay for exhaust gas temperature alarms?'.")]
    [property: MinLength(3)]
    [property: MaxLength(500)]
    string Question,
    [property: Description("How many passages to return (default 4, at most 6).")]
    [property: Range(1, 6)]
    int? K = null);

/// <summary>
/// RAG over the project documents (alarm philosophy, cause and effect notes, functional design spec) as a READ tool. The passages are
/// untrusted DATA exactly like tag descriptions: each one is scanned for instruction-like text (flagged=true when it matches) and the
/// whole result goes to the model inside the delimited TOOL_DATA block that the agent loop puts around every read tool result.
/// Passages carry the citation fields (source, section, page). The tool cannot write anything.
/// </summary>
public sealed class SearchDocsTool(KnowledgeSearchService search) : AgentTool<SearchDocsArgs>
{
    public const int DefaultK = 4;
    public const int MaxChunkChars = 3500;

    public override string Name => ToolNames.SearchDocs;

    public override string Description =>
        "Search the project documents (alarm philosophy, cause and effect notes, functional design specification) for passages that answer a question about project rules, standard values or design. Returns the best passages with source, section and page for citing. If nothing is close enough, found is false: say so instead of guessing. Use it when a request depends on a documented rule; do not use it for tag lookups.";

    public override ToolKind Kind => ToolKind.Read;

    public override IReadOnlyList<string> Routes => ["GET /api/projects/{projectId}/knowledge/search"];

    protected override async Task<ToolResult> RunAsync(SearchDocsArgs args, ToolContext context, CancellationToken ct)
    {
        RetrievalResult result;
        try
        {
            result = await search.SearchAsync(context.ProjectId, args.Question, Math.Clamp(args.K ?? DefaultK, 1, 6), ct);
        }
        catch (AppException ex) when (ex.Code == KnowledgeErrorCodes.NotIndexed)
        {
            return ToolResult.Error(ToolErrorCodes.NotIndexed, "The project documents have not been indexed, so they cannot be searched. Continue without them and say so.");
        }
        catch (LlmException ex)
        {
            return ToolResult.Error(ToolErrorCodes.Failed, $"The document search is unavailable right now ({ex.Code}). Continue without it and say so.");
        }

        if (result.BelowThreshold)
        {
            return ToolResult.Success(new { found = false, message = "Nothing in the project documents matches this question closely enough.", passages = Array.Empty<object>() });
        }

        return ToolResult.Success(new
        {
            found = true,
            passages = result.Relevant.Select(c =>
            {
                var reason = ToolText.Inspect(c.Text, c.Section);
                return new
                {
                    source = c.Source,
                    section = c.Section,
                    page = c.Page,
                    cite = Citation.Format(c.Source, c.Section, c.Page),
                    text = c.Text.Length <= MaxChunkChars ? c.Text : c.Text[..MaxChunkChars] + "…",
                    flagged = reason is not null,
                    flag_reason = reason,
                };
            }).ToList(),
        });
    }
}

/// <summary>The one citation format of the project documents: <c>[doc §section p.N]</c>.</summary>
public static class Citation
{
    public static string Format(string source, string section, int page) => $"[{source} §{section} p.{page}]";
}
