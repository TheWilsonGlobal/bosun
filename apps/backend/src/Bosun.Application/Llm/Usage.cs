using System.Globalization;
using System.Text;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bosun.Application.Llm;

/// <summary>Persists one row per provider call. Implementations must never throw: losing a log row must not fail the call.</summary>
public interface ILlmCallLog
{
    Task RecordAsync(LlmCall call, CancellationToken cancellationToken);
}

/// <summary>Aggregated usage for one feature and model.</summary>
public sealed record LlmUsageRow(
    string Feature,
    string Model,
    string Kind,
    int Calls,
    int Failures,
    long PromptTokens,
    long CandidateTokens,
    long ThoughtTokens,
    long TotalTokens,
    double AvgLatencyMs,
    int MaxLatencyMs,
    double CostUsd);

public sealed record LlmUsageSummary(DateTime? From, DateTime? To, LlmUsageRow Totals, IReadOnlyList<LlmUsageRow> Rows)
{
    public static LlmUsageSummary Of(DateTime? from, DateTime? to, IReadOnlyList<LlmUsageRow> rows) =>
        new(from, to, LlmUsageReport.Sum(rows), rows);
}

/// <summary>Read side of the llm_calls table: GET /api/ai/usage and the <c>--llm-usage</c> command.</summary>
public sealed class LlmUsageQueries(IBosunDbContext db)
{
    public async Task<LlmUsageSummary> SummariseAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        var calls = db.LlmCalls.AsNoTracking();
        if (from is { } f)
        {
            calls = calls.Where(c => c.Timestamp >= f);
        }

        if (to is { } t)
        {
            calls = calls.Where(c => c.Timestamp < t);
        }

        var grouped = await calls
            .GroupBy(c => new { c.Feature, c.Model, c.Kind })
            .Select(g => new
            {
                g.Key.Feature,
                g.Key.Model,
                g.Key.Kind,
                Calls = g.Count(),
                Failures = g.Count(c => !c.Succeeded),
                PromptTokens = g.Sum(c => (long)c.PromptTokens),
                CandidateTokens = g.Sum(c => (long)c.CandidateTokens),
                ThoughtTokens = g.Sum(c => (long)c.ThoughtTokens),
                TotalTokens = g.Sum(c => (long)c.TotalTokens),
                AvgLatencyMs = g.Average(c => (double)c.LatencyMs),
                MaxLatencyMs = g.Max(c => c.LatencyMs),
                CostUsd = g.Sum(c => c.CostUsd),
            })
            .OrderBy(r => r.Feature)
            .ThenBy(r => r.Model)
            .ToListAsync(ct);

        var rows = grouped
            .Select(r => new LlmUsageRow(r.Feature, r.Model, r.Kind, r.Calls, r.Failures, r.PromptTokens, r.CandidateTokens, r.ThoughtTokens, r.TotalTokens, r.AvgLatencyMs, r.MaxLatencyMs, r.CostUsd))
            .ToList();
        return LlmUsageSummary.Of(from, to, rows);
    }
}

/// <summary>Plain-text token/cost table. Used by <c>--llm-usage</c>, and by the Phase 4+ eval runner to end a run.</summary>
public static class LlmUsageReport
{
    public static LlmUsageRow Sum(IReadOnlyList<LlmUsageRow> rows)
    {
        var calls = rows.Sum(r => r.Calls);
        return new LlmUsageRow(
            "TOTAL",
            "",
            "",
            calls,
            rows.Sum(r => r.Failures),
            rows.Sum(r => r.PromptTokens),
            rows.Sum(r => r.CandidateTokens),
            rows.Sum(r => r.ThoughtTokens),
            rows.Sum(r => r.TotalTokens),
            calls == 0 ? 0 : rows.Sum(r => r.AvgLatencyMs * r.Calls) / calls,
            rows.Count == 0 ? 0 : rows.Max(r => r.MaxLatencyMs),
            rows.Sum(r => r.CostUsd));
    }

    public static string Format(IReadOnlyList<LlmUsageRow> rows)
    {
        string[] headers = ["feature", "model", "calls", "fail", "tok in", "tok out", "tok think", "avg ms", "max ms", "est. USD"];
        var lines = new List<string[]>();
        foreach (var r in rows.Append(Sum(rows)))
        {
            lines.Add(
            [
                r.Feature,
                r.Model,
                r.Calls.ToString(CultureInfo.InvariantCulture),
                r.Failures.ToString(CultureInfo.InvariantCulture),
                r.PromptTokens.ToString(CultureInfo.InvariantCulture),
                r.CandidateTokens.ToString(CultureInfo.InvariantCulture),
                r.ThoughtTokens.ToString(CultureInfo.InvariantCulture),
                r.AvgLatencyMs.ToString("F0", CultureInfo.InvariantCulture),
                r.MaxLatencyMs.ToString(CultureInfo.InvariantCulture),
                r.CostUsd.ToString("F6", CultureInfo.InvariantCulture),
            ]);
        }

        var widths = headers.Select((h, i) => Math.Max(h.Length, lines.Max(l => l[i].Length))).ToArray();
        var sb = new StringBuilder();
        void Row(string[] cells)
        {
            for (var i = 0; i < cells.Length; i++)
            {
                // text columns left-aligned, numbers right-aligned
                sb.Append(i < 2 ? cells[i].PadRight(widths[i]) : cells[i].PadLeft(widths[i])).Append(i < cells.Length - 1 ? "  " : "");
            }

            sb.AppendLine();
        }

        Row(headers);
        sb.AppendLine(new string('-', widths.Sum() + (2 * (widths.Length - 1))));
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == lines.Count - 1)
            {
                sb.AppendLine(new string('-', widths.Sum() + (2 * (widths.Length - 1))));
            }

            Row(lines[i]);
        }

        sb.AppendLine("Costs are ESTIMATES from llm-pricing.json; the free tier bills nothing.");
        return sb.ToString();
    }
}

/// <summary>
/// In-memory usage accumulator for runs that have no database handy (an eval run, a script): feed it responses, print the
/// same table the usage endpoint shows.
/// </summary>
public sealed class LlmUsageTally
{
    private readonly object gate = new();
    private readonly Dictionary<(string Feature, string Model), List<(LlmResponse Response, bool Ok)>> calls = [];

    public void Add(string feature, LlmResponse response)
    {
        lock (gate)
        {
            var key = (feature, response.Model);
            if (!calls.TryGetValue(key, out var list))
            {
                calls[key] = list = [];
            }

            list.Add((response, true));
        }
    }

    public IReadOnlyList<LlmUsageRow> Rows()
    {
        lock (gate)
        {
            return calls
                .OrderBy(kv => kv.Key.Feature, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Model, StringComparer.Ordinal)
                .Select(kv => new LlmUsageRow(
                    kv.Key.Feature,
                    kv.Key.Model,
                    "llm",
                    kv.Value.Count,
                    0,
                    kv.Value.Sum(c => (long)c.Response.Usage.PromptTokens),
                    kv.Value.Sum(c => (long)c.Response.Usage.CandidateTokens),
                    kv.Value.Sum(c => (long)c.Response.Usage.ThoughtTokens),
                    kv.Value.Sum(c => (long)c.Response.Usage.TotalTokens),
                    kv.Value.Average(c => c.Response.Latency.TotalMilliseconds),
                    (int)kv.Value.Max(c => c.Response.Latency.TotalMilliseconds),
                    kv.Value.Sum(c => c.Response.EstimatedCostUsd ?? 0)))
                .ToList();
        }
    }

    public string Format() => LlmUsageReport.Format(Rows());
}
