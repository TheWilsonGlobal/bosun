using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Application.Rag;
using Bosun.Application.Tools;

namespace Bosun.Agent;

/// <summary>A source the answer relies on, in the exact form of the citation: <c>[source §section p.N]</c>.</summary>
public sealed record AskCitation(string Source, string Section, int Page);

/// <summary>What the model is asked to return (the schema is generated from this record).</summary>
public sealed record AskModelOutput(string Answer, IReadOnlyList<AskCitation> Citations, bool Answered);

public sealed record AskRetrieved(string Source, string Section, int Page, double Score, double? Cosine, bool AboveThreshold, bool UsedForAnswer, bool Flagged = false, bool IsExclusion = false);

public sealed record AskUsage(int PromptTokens, int CandidateTokens, int ThoughtTokens, int TotalTokens, int LlmCalls, double EstimatedCostUsd);

/// <summary>Why an answer is a refusal. Null when the question was answered.</summary>
public static class RefusalReasons
{
    /// <summary>Nothing in the documents is close enough to the question; the model was not called.</summary>
    public const string BelowThreshold = "below_threshold";

    /// <summary>The passages were close but the model found no answer in them.</summary>
    public const string ModelDeclined = "model_declined";

    /// <summary>The model claimed an answer but none of its citations is a passage that was retrieved.</summary>
    public const string NoValidCitation = "no_valid_citation";
}

/// <summary>Result of <see cref="RagAskService.Ground"/>.</summary>
/// <param name="Dropped">Citations (list entries or inline markers) that did not match a retrieved passage.</param>
/// <param name="UnsupportedSentencesRemoved">Sentences removed from the answer because they carried an invented marker.</param>
public sealed record GroundedAnswer(string Answer, IReadOnlyList<AskCitation> Citations, int Dropped, int UnsupportedSentencesRemoved);

/// <param name="Answered">False = refusal ("not in the project documents"); <see cref="Citations"/> is then empty.</param>
/// <param name="Citations">Only passages that were really retrieved for this question (invented ones are removed).</param>
/// <param name="DroppedCitations">How many citations (list entries or inline markers) did not match a retrieved passage and were removed.</param>
/// <param name="UnsupportedSentencesRemoved">Sentences cut from the answer because they carried an invented citation marker.</param>
public sealed record AskResponse(
    string Question,
    bool Answered,
    string Answer,
    IReadOnlyList<AskCitation> Citations,
    string? RefusalReason,
    int DroppedCitations,
    int UnsupportedSentencesRemoved,
    IReadOnlyList<AskRetrieved> Retrieved,
    string? Model,
    string? PromptVersion,
    AskUsage? Usage,
    int LatencyMs);

/// <summary>
/// Grounded Q&amp;A over the project documents (plan s6.4): hybrid retrieval, then ONE structured model call that must answer from the
/// retrieved passages and cite them as <c>[doc §section p.N]</c>. The model's citations are not trusted: each one (in the list and
/// inline in the text) is checked against the passages that were actually retrieved, invented ones are removed and counted, and an
/// answer left without a valid citation becomes a refusal.
///
/// IMPORTANT, what a citation proves: a citation is a RETRIEVAL check ("this passage was given to the model for this question"),
/// not entailment ("this passage supports this sentence"). The service cannot tell whether a real passage really says what the
/// sentence claims; the faithfulness of the wording rests on the prompt, the threshold and the human reading the cited passage.
/// What it does enforce: a sentence carrying an invented marker is removed (counted in unsupportedSentencesRemoved), nothing
/// supported left = refusal, and an answer whose valid citations are ALL exclusion sections ("what this document does not cover")
/// is a refusal (model_declined), because a scope statement cannot answer a question. When retrieval finds nothing above the threshold the model is not called
/// at all. LLM and embedding failures surface as <see cref="LlmException"/> (llm_* codes).
/// </summary>
public sealed partial class RagAskService(KnowledgeSearchService search, StructuredLlm structured, PromptLibrary prompts)
{
    public const string Feature = "rag.ask";
    public const string NotCovered = "This is not covered in the project documents.";

    [GeneratedRegex(@"\[(?<source>[^\[\]§]+?)\s*§\s*(?<section>[^\[\]]+?)\s+p\.\s*(?<page>\d+)\s*\]")]
    private static partial Regex InlineCitation();

    public async Task<AskResponse> AskAsync(int projectId, string question, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var retrieval = await search.SearchAsync(projectId, question, null, ct);
        var retrieved = retrieval.Chunks.Select(c => new AskRetrieved(c.Source, c.Section, c.Page, c.Score, c.Cosine, c.AboveThreshold, false, c.Flagged, c.IsExclusion)).ToList();
        if (retrieval.BelowThreshold)
        {
            return new AskResponse(retrieval.Query, false, NotCovered, [], RefusalReasons.BelowThreshold, 0, 0, retrieved, null, null, null, (int)watch.ElapsedMilliseconds);
        }

        var prompt = prompts.Get("rag.ask.system");
        var allowed = retrieval.Relevant.Select(c => new AskCitation(c.Source, c.Section, c.Page)).Distinct().ToList();
        // A (source, section, page) that has at least one non-exclusion chunk can support an answer; the others are scope statements only.
        var supporting = retrieval.Relevant.Where(c => !c.IsExclusion).Select(c => new AskCitation(c.Source, c.Section, c.Page)).ToHashSet();
        var blockId = Guid.NewGuid().ToString("N")[..8];
        var data = JsonSerializer.Serialize(
            retrieval.Relevant.Select(c => new { source = c.Source, section = c.Section, page = c.Page, text = c.Text }),
            new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
            .Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal);

        var result = await structured.GenerateAsync<AskModelOutput>(
            new LlmRequest
            {
                Feature = Feature,
                PromptVersion = prompt.Version,
                SystemInstruction = prompt.Text,
                Messages = [LlmMessage.User($"Question: {retrieval.Query}\n\nSources:\n<<<DOC_DATA id={blockId}>>>\n{data}\n<<<END_DOC_DATA id={blockId}>>>")],
                Temperature = 0,
                Thinking = LlmThinking.Minimal,
            },
            output => output.Answered && InlineCitation().Matches(output.Answer).Count == 0
                ? "an answer needs at least one inline citation in the form [source §section p.N], copied from the sources."
                : null,
            ct);

        var grounded = Ground(result.Value, allowed);
        var (answer, citations, dropped) = (grounded.Answer, grounded.Citations, grounded.Dropped);
        var onlyExclusions = citations.Count > 0 && !citations.Any(supporting.Contains);
        var usage = result.Usage;
        var last = result.Last;
        var answered = result.Value.Answered && citations.Count > 0 && !string.IsNullOrWhiteSpace(answer) && !onlyExclusions;
        string? reason = null;
        if (!answered)
        {
            reason = onlyExclusions ? RefusalReasons.ModelDeclined
                : result.Value.Answered ? RefusalReasons.NoValidCitation
                : RefusalReasons.ModelDeclined;
            answer = result.Value.Answered || string.IsNullOrWhiteSpace(answer) ? NotCovered : answer;
            citations = [];
        }

        return new AskResponse(
            retrieval.Query,
            answered,
            answer,
            citations,
            reason,
            dropped,
            grounded.UnsupportedSentencesRemoved,
            retrieved.Select(r => r with { UsedForAnswer = answered && citations.Any(c => c.Source == r.Source && c.Section == r.Section && c.Page == r.Page) }).ToList(),
            last.Model,
            prompt.Version,
            new AskUsage(usage.PromptTokens, usage.CandidateTokens, usage.ThoughtTokens, usage.TotalTokens, result.Calls.Count, result.EstimatedCostUsd),
            (int)watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Keeps only citations that name a retrieved passage (source, section and page must all match, ignoring case and a trailing ".md").
    /// A sentence that carries an invented inline marker is removed whole (a claim whose "proof" does not exist is not shown), also when
    /// it carries a valid marker next to it. Valid inline markers are added to the list when the model forgot them. This checks that the
    /// passage was RETRIEVED; it does not prove the passage entails the sentence (see the class remarks).
    /// </summary>
    public static GroundedAnswer Ground(AskModelOutput output, IReadOnlyList<AskCitation> allowed)
    {
        var dropped = 0;
        var removed = 0;
        var valid = new List<AskCitation>();

        AskCitation? Match(string source, string section, int page) =>
            allowed.FirstOrDefault(a => Same(a.Source.Replace(".md", "", StringComparison.OrdinalIgnoreCase), source.Trim().Replace(".md", "", StringComparison.OrdinalIgnoreCase))
                && Same(a.Section, section.Trim())
                && a.Page == page);

        foreach (var citation in output.Citations ?? [])
        {
            if (Match(citation.Source ?? "", citation.Section ?? "", citation.Page) is { } hit)
            {
                if (!valid.Contains(hit))
                {
                    valid.Add(hit);
                }
            }
            else
            {
                dropped++;
            }
        }

        var kept = new List<string>();
        foreach (var sentence in SplitSentences(output.Answer ?? ""))
        {
            var invented = false;
            var found = new List<AskCitation>();
            var text = InlineCitation().Replace(sentence, m =>
            {
                var page = int.Parse(m.Groups["page"].Value, CultureInfo.InvariantCulture);
                if (Match(m.Groups["source"].Value, m.Groups["section"].Value, page) is { } hit)
                {
                    found.Add(hit);

                    // Normalise the marker to the canonical spelling.
                    return Citation.Format(hit.Source, hit.Section, hit.Page);
                }

                dropped++;
                invented = true;
                return "";
            });

            if (invented)
            {
                removed++;
                continue;
            }

            foreach (var hit in found.Where(h => !valid.Contains(h)))
            {
                valid.Add(hit);
            }

            kept.Add(text);
        }

        var answer = string.Join(' ', kept);
        answer = Regex.Replace(answer, @"[ \t]{2,}", " ").Replace(" .", ".", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal).Trim();
        if (valid.Count > 0 && answer.Length > 0 && !InlineCitation().IsMatch(answer))
        {
            // The model cited in the list but not in the text: put the real ones at the end.
            answer = $"{answer} {string.Join(' ', valid.Select(v => Citation.Format(v.Source, v.Section, v.Page)))}".Trim();
        }

        if (answer.Length == 0)
        {
            // Nothing supported is left: no list-only citation may stand in for text that was removed.
            valid.Clear();
        }

        return new GroundedAnswer(answer, valid, dropped, removed);
    }

    /// <summary>
    /// Splits on sentence ends outside citation markers ("p.2" and "p. 2" are not ends). A segment that is only markers (the model wrote
    /// "... 10 s. [doc §sec p.2]") belongs to the sentence before it.
    /// </summary>
    internal static List<string> SplitSentences(string text)
    {
        const char Mask = '\u0001';
        var masked = InlineCitation().Replace(text, m => new string(Mask, m.Length));
        var parts = new List<(int Start, int End)>();
        var start = 0;
        for (var i = 0; i < masked.Length; i++)
        {
            if (masked[i] is '.' or '!' or '?' && (i + 1 == masked.Length || char.IsWhiteSpace(masked[i + 1])))
            {
                parts.Add((start, i + 1));
                start = i + 1;
            }
        }

        if (start < masked.Length)
        {
            parts.Add((start, masked.Length));
        }

        var result = new List<string>();
        foreach (var (from, to) in parts)
        {
            var raw = text[from..to];
            var onlyMarkers = masked[from..to].Replace(Mask.ToString(), "", StringComparison.Ordinal).Trim(' ', '\t', '\r', '\n', '.', ',', ';').Length == 0;
            if (onlyMarkers && result.Count > 0)
            {
                result[^1] = result[^1] + " " + raw.Trim();
            }
            else if (raw.Trim().Length > 0)
            {
                result.Add(raw.Trim());
            }
        }

        return result;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
