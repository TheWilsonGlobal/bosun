using System.Text.Json;
using System.Text.RegularExpressions;
using Bosun.Application.Llm;
using Bosun.Evals.Fixtures;
using Bosun.Extraction;

namespace Bosun.Evals.Offline;

/// <summary>
/// A deterministic stand-in for the model, used by the default test suite and by <c>--offline</c> evals (no network, no spend). On a text
/// request it reads the rebuilt table out of the DOC_DATA block exactly as a careful human would (columns mapped by their titles); on an
/// image request it answers from a supplied function (the tests give it the ground truth). It is NOT a model result: it proves the
/// plumbing around the model (reconstruction, prompts, normalisation, grounding, rules, confidence), never the model's accuracy.
/// </summary>
public sealed partial class TableParsingLlm(Func<int, IReadOnlyList<ReadRow>>? imageRows = null) : ILlmClient
{
    private int calls;

    public string Provider => "offline";

    public string DefaultModel => "offline-table-parser";

    public int DefaultMaxOutputTokens => 16384;

    public int MaxOutputTokensCap => 32768;

    public int Calls => calls;

    /// <summary>Images requested so far, by page (so tests can see what the vision path was given): size in bytes, PNG pixel size, feature.</summary>
    public List<(int Page, int Bytes, int Width, int Height, string Feature)> ImageRequests { get; } = [];

    /// <summary>Every request received, in order (system prompt, messages, schema).</summary>
    public List<LlmRequest> Requests { get; } = [];

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        lock (Requests)
        {
            Requests.Add(request);
        }

        var parts = request.Messages[^1].Parts;
        var text = string.Concat(parts.OfType<TextPart>().Select(p => p.Text));
        var page = int.Parse(PageNumber().Match(text).Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);

        IReadOnlyList<ReadRow> rows;
        if (parts.OfType<ImagePart>().FirstOrDefault() is { } image)
        {
            lock (ImageRequests)
            {
                var width = image.Data.Length > 24 ? (image.Data[16] << 24) | (image.Data[17] << 16) | (image.Data[18] << 8) | image.Data[19] : 0;
                var height = image.Data.Length > 24 ? (image.Data[20] << 24) | (image.Data[21] << 16) | (image.Data[22] << 8) | image.Data[23] : 0;
                ImageRequests.Add((page, image.Data.Length, width, height, request.Feature));
            }

            rows = imageRows?.Invoke(page) ?? [];
        }
        else
        {
            rows = Parse(text);
        }

        var json = JsonSerializer.Serialize(new ReadReply(rows), StructuredJson.Options);
        var tokens = Math.Max(1, text.Length / 4);
        return Task.FromResult(new LlmResponse
        {
            Model = DefaultModel,
            Parts = [new TextPart(json)],
            FinishReason = LlmFinishReason.Stop,
            FinishReasonRaw = "STOP",
            Usage = new LlmUsage(tokens, json.Length / 4, 0, tokens + (json.Length / 4)),
            Latency = TimeSpan.FromMilliseconds(1),
        });
    }

    [GeneratedRegex(@"Page (?<n>\d+) of", RegexOptions.CultureInvariant)]
    private static partial Regex PageNumber();

    [GeneratedRegex(@"^(?<label>p\d+:L\d+)(?: \[merged above: [^\]]*\])?: (?<cells>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DataLine();

    [GeneratedRegex(@"^COLUMNS[^:]*: (?<cols>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnsLine();

    [GeneratedRegex(@"^\s*(?<lo>-?[\d.,]+)\s*(?:…|\.\.\.|-)\s*(?<hi>-?[\d.,]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    private static string? FieldOfTitle(string title)
    {
        var t = title.Trim().ToLowerInvariant();
        return t switch
        {
            "tag" or "tag no." or "tag no" => Fields.TagName,
            "description" or "service" => Fields.Description,
            "type" or "signal" or "i/o" => Fields.SignalType,
            "unit" or "units" or "eng. unit" => Fields.Unit,
            "range low" or "low" or "min" => Fields.RangeLow,
            "range high" or "high" or "max" => Fields.RangeHigh,
            "range" or "span" => "range",
            "hh" or "hi-hi" => Fields.AlarmHh,
            "h" or "hi" => Fields.AlarmH,
            "l" or "lo" => Fields.AlarmL,
            "ll" or "lo-lo" => Fields.AlarmLl,
            _ when t.StartsWith("delay", StringComparison.Ordinal) => Fields.DelayS,
            _ => null,
        };
    }

    /// <summary>Reads the data lines of a prompt (as built by the table reconstruction) into rows.</summary>
    public static IReadOnlyList<ReadRow> Parse(string prompt)
    {
        var columns = new List<string?>();
        var rows = new List<ReadRow>();
        foreach (var raw in prompt.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (ColumnsLine().Match(line) is { Success: true } header)
            {
                columns = header.Groups["cols"].Value.Split(" | ")
                    .Select(c => FieldOfTitle(c[(c.IndexOf('=') + 1)..]))
                    .ToList();
                continue;
            }

            if (DataLine().Match(line) is not { Success: true } data)
            {
                continue;
            }

            var cells = data.Groups["cells"].Value.Split(" | ");
            var values = new Dictionary<string, string?>();
            for (var i = 0; i < Math.Min(cells.Length, columns.Count); i++)
            {
                if (columns[i] is not { } field)
                {
                    continue;
                }

                var cell = cells[i].Trim();
                if (field == "range")
                {
                    if (Span().Match(cell) is { Success: true } span)
                    {
                        values[Fields.RangeLow] = span.Groups["lo"].Value;
                        values[Fields.RangeHigh] = span.Groups["hi"].Value;
                    }

                    continue;
                }

                values[field] = cell.Length == 0 ? null : cell.Replace('¦', '|');
            }

            if (values.GetValueOrDefault(Fields.TagName) is not { } tag)
            {
                continue;
            }

            rows.Add(new ReadRow(
                tag, values.GetValueOrDefault(Fields.Description), values.GetValueOrDefault(Fields.SignalType), values.GetValueOrDefault(Fields.Unit),
                values.GetValueOrDefault(Fields.RangeLow), values.GetValueOrDefault(Fields.RangeHigh), values.GetValueOrDefault(Fields.AlarmHh),
                values.GetValueOrDefault(Fields.AlarmH), values.GetValueOrDefault(Fields.AlarmL), values.GetValueOrDefault(Fields.AlarmLl),
                values.GetValueOrDefault(Fields.DelayS), data.Groups["label"].Value, []));
        }

        return rows;
    }
}

/// <summary>Ground truth rows as the model would transcribe them from a page (numbers in the style the page prints them, canonical units).</summary>
public static class TruthReader
{
    public static IReadOnlyList<ReadRow> ReadRowsOf(IEnumerable<TruthRow> rows, NumStyle style = NumStyle.Dot) =>
        [.. rows.Select((r, i) => ReadRowOf(r, $"row {i + 1}", style))];

    public static ReadRow ReadRowOf(TruthRow r, string sourceRef, NumStyle style = NumStyle.Dot)
    {
        string? Text(double? value) => value is null ? null : FixtureLayouts.Fmt(value, style);
        return new ReadRow(
            r.TagName, r.Description, r.SignalType, r.Unit, Text(r.RangeLow), Text(r.RangeHigh), Text(r.AlarmHh), Text(r.AlarmH), Text(r.AlarmL), Text(r.AlarmLl),
            Text(r.DelayS), sourceRef, []);
    }
}
