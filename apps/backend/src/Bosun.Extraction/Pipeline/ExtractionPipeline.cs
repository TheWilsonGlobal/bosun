using System.Text.RegularExpressions;
using Bosun.Application.Llm;
using Bosun.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bosun.Extraction;

/// <summary>The outcome of one page: what triage decided and what happened next. Serialised into <c>extractions.pages_json</c>.</summary>
public sealed class PageReport
{
    public int Page { get; set; }

    /// <summary>"text", "vision" or "skip".</summary>
    public string Path { get; set; } = "skip";
    public List<string> Reasons { get; set; } = [];
    public int CharCount { get; set; }
    public int WordCount { get; set; }
    public double GarbageRatio { get; set; }
    public int HorizontalLines { get; set; }
    public int VerticalLines { get; set; }
    public int TableLikeRows { get; set; }
    public double ImageAreaRatio { get; set; }

    /// <summary>"pending", "done", "failed" or "skipped".</summary>
    public string Status { get; set; } = "pending";
    public int Rows { get; set; }

    /// <summary>Text pages: table rows the rebuild found, and the ones the model returned no row for (labels).</summary>
    public int ReconstructedRows { get; set; }
    public List<string> UnmatchedRows { get; set; } = [];
    public bool HeaderBorrowed { get; set; }
    public bool DualRead { get; set; }

    /// <summary>Tags the second read found that the first read did not return.</summary>
    public List<string> DualReadOnly { get; set; } = [];

    /// <summary>Instruction-like phrases found in the page text (pattern names only).</summary>
    public List<string> InjectionHits { get; set; } = [];
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>A row with where it came from (persisted next to the <see cref="RowState"/>).</summary>
public sealed class ExtractedRowRecord
{
    public required RowState State { get; init; }
    public int Page { get; init; }

    /// <summary>"text" or "vision".</summary>
    public required string Path { get; init; }
    public string? SourceRef { get; set; }
    public string? Snippet { get; set; }

    /// <summary>[x0, y0, x1, y1] as fractions of the page, origin top-left; null when the position is not known.</summary>
    public double[]? Bbox { get; set; }
}

public sealed class PipelineResult
{
    public List<PageReport> Pages { get; } = [];
    public List<ExtractedRowRecord> Rows { get; } = [];
    public LlmUsage Usage { get; set; } = LlmUsage.Zero;
    public int Calls { get; set; }
    public double CostUsd { get; set; }
    public string? Model { get; set; }
    public ExtractionStatus Status { get; set; } = ExtractionStatus.Completed;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed record PipelineProgress(string Phase, int PagesDone, int PagesTotal);

/// <summary>
/// The document pipeline of plan s6.3, from PDF bytes to assessed rows: per-page triage, text reconstruction or page rendering, the
/// model read (two pages at a time), normalisation, dual-read of a sample and of low-score pages, rule validation and confidence.
/// It touches no database: callers persist the result, and the evals call it directly.
/// </summary>
public sealed class ExtractionPipeline(
    RowReader reader,
    IPageRenderer renderer,
    IOptions<ExtractionOptions> options,
    ILogger<ExtractionPipeline> logger)
{
    private readonly ExtractionOptions opts = options.Value;

    private sealed class PageWork
    {
        public required PageContent Content { get; init; }
        public required PageReport Report { get; init; }
        public required PagePath Path { get; init; }
        public ReconstructedPage? Table { get; set; }

        /// <summary>The number convention used to read this page (also used for its second read, so both reads parse "1.200" alike).</summary>
        public NumberLocale Locale { get; set; }
        public GroundingIndex? Grounding { get; set; }
        public ReadResult? Read { get; set; }
        public List<ExtractedRowRecord> Rows { get; } = [];
    }

    public async Task<PipelineResult> RunAsync(
        byte[] pdf,
        IReadOnlySet<string> projectTags,
        Func<PipelineProgress, Task>? progress = null,
        CancellationToken ct = default)
    {
        var result = new PipelineResult { Model = reader.Model };

        async Task Report(string phase, int done, int total)
        {
            if (progress is not null)
            {
                await progress(new PipelineProgress(phase, done, total));
            }
        }

        // ---------- 1. triage ----------
        await Report("triage", 0, 0);
        IReadOnlyList<PageContent> pages;
        try
        {
            pages = PdfPages.Read(pdf);
        }
        catch (PdfUnreadableException ex)
        {
            result.Status = ExtractionStatus.Failed;
            result.ErrorCode = ex.Code;
            result.ErrorMessage = ex.Message;
            return result;
        }

        if (pages.Count > opts.MaxPages)
        {
            result.Status = ExtractionStatus.Failed;
            result.ErrorCode = "too_many_pages";
            result.ErrorMessage = $"The document has {pages.Count} pages; the limit is {opts.MaxPages}.";
            return result;
        }

        var work = new List<PageWork>();
        DocumentHeader? header = null;
        foreach (var content in pages)
        {
            var triage = PageTriage.Decide(content, opts.Triage);
            var report = new PageReport
            {
                Page = content.Number,
                Path = triage.Path.ToString().ToLowerInvariant(),
                Reasons = [.. triage.Reasons],
                CharCount = triage.CharCount,
                WordCount = triage.WordCount,
                GarbageRatio = triage.GarbageRatio,
                HorizontalLines = triage.HorizontalLines,
                VerticalLines = triage.VerticalLines,
                TableLikeRows = triage.TableLikeRows,
                ImageAreaRatio = triage.ImageAreaRatio,
                Status = triage.Path == PagePath.Skip ? "skipped" : "pending",
            };
            var item = new PageWork { Content = content, Report = report, Path = triage.Path };
            if (triage.Path == PagePath.Text)
            {
                item.Table = TableReconstructor.Reconstruct(content, header);
                header ??= TableReconstructor.HeaderOf(item.Table);
                item.Grounding = GroundingIndex.Build(content);
                report.ReconstructedRows = item.Table.DataRows.Count();
                report.HeaderBorrowed = item.Table.HeaderBorrowed;
            }

            if (triage.Path != PagePath.Vision && content.CharCount > 0)
            {
                // The text of every page with a text layer is scanned, skipped pages included: a hostile note is worth knowing about.
                report.InjectionHits = InjectionScanner.Scan(content.Text).Select(h => h.Pattern).ToList();
            }

            work.Add(item);
            result.Pages.Add(report);
        }

        // ---------- 2. read ----------
        var readable = work.Where(w => w.Path != PagePath.Skip).ToList();
        var done = 0;
        await Report("reading", done, readable.Count);
        var gate = new SemaphoreSlim(Math.Max(1, opts.MaxConcurrentPages));
        var progressLock = new SemaphoreSlim(1, 1);
        var usageLock = new object();
        var stop = (LlmException?)null;

        async Task ReadPage(PageWork item)
        {
            await gate.WaitAsync(ct);
            try
            {
                if (stop is not null)
                {
                    Fail(item, stop, notAttempted: true);
                    return;
                }

                try
                {
                    item.Read = item.Path == PagePath.Text
                        ? await reader.ReadTextAsync(item.Content.Number, pages.Count, item.Table!.PromptText, ct)
                        : await reader.ReadImageAsync(item.Content.Number, pages.Count, renderer.RenderPng(pdf, item.Content.Number, opts.RenderDpi), RowReader.VisionFeature, ct);
                    lock (usageLock)
                    {
                        Add(result, item.Read);
                    }
                }
                catch (LlmException ex)
                {
                    logger.LogWarning("Reading page {Page} failed: {Code} {Message}", item.Content.Number, ex.Code, ex.Message);
                    Fail(item, ex, notAttempted: false);
                    if (ex.Code is LlmErrorCodes.AuthFailed or LlmErrorCodes.NotConfigured or LlmErrorCodes.BadRequest
                        || ex.Code == LlmErrorCodes.RateLimited && (ex.RetryAfter ?? TimeSpan.Zero) > TimeSpan.FromMinutes(5))
                    {
                        // Every further page would fail the same way (a bad key, a spent daily quota): stop spending time on them.
                        stop ??= ex;
                    }
                }
            }
            finally
            {
                gate.Release();
                await progressLock.WaitAsync(CancellationToken.None);
                try
                {
                    done++;
                    await Report("reading", done, readable.Count);
                }
                finally
                {
                    progressLock.Release();
                }
            }
        }

        await Task.WhenAll(readable.Select(ReadPage));
        ct.ThrowIfCancellationRequested();

        // ---------- 3. normalise ----------
        var documentLocale = Normaliser.DetectLocale(
            work.Where(w => w.Table is not null).SelectMany(w => w.Table!.NumericTokens)
                .Concat(work.Where(w => w.Read is not null && w.Path == PagePath.Vision).SelectMany(w => NumericRaw(w.Read!.Rows))));
        foreach (var item in readable.Where(w => w.Read is not null))
        {
            BuildRows(item, documentLocale);
        }

        // ---------- 4. dual-read ----------
        await Report("dual_read", done, readable.Count);
        var dualPages = new HashSet<int>();
        var sample = readable.Where(w => w.Path == PagePath.Text && w.Rows.Count > 0).Take(opts.DualReadSamplePages).ToList();
        foreach (var item in sample)
        {
            await DualReadAsync(item, pdf, pages.Count, result, ct);
            dualPages.Add(item.Content.Number);
        }

        // ---------- 5. validate + confidence ----------
        await Report("validating", done, readable.Count);
        var all = work.SelectMany(w => w.Rows).ToList();
        for (var i = 0; i < all.Count; i++)
        {
            all[i].State.Seq = i + 1;
        }

        AssessAll(all, projectTags);

        // low-score text pages get the second read too (capped), then everything on them is assessed again
        foreach (var item in readable.Where(w => w.Path == PagePath.Text && w.Rows.Count > 0 && !dualPages.Contains(w.Content.Number)))
        {
            if (dualPages.Count >= opts.DualReadMaxPages)
            {
                break;
            }

            var weak = item.Rows.Count(r => r.State.Band != Bands.Auto);
            if ((double)weak / item.Rows.Count < opts.DualReadLowScoreShare || weak == 0)
            {
                continue;
            }

            await DualReadAsync(item, pdf, pages.Count, result, ct);
            dualPages.Add(item.Content.Number);
            AssessAll(all, projectTags);
        }

        foreach (var item in work)
        {
            item.Report.Rows = item.Rows.Count;
            if (item.Report.Status == "pending")
            {
                item.Report.Status = "done";
            }
        }

        result.Rows.AddRange(all);
        Finish(result, readable.Count);
        return result;
    }

    // ---------- helpers: reading ----------

    private static void Fail(PageWork item, LlmException ex, bool notAttempted)
    {
        item.Report.Status = "failed";
        item.Report.ErrorCode = ex.Code;
        item.Report.ErrorMessage = notAttempted ? "Not attempted: an earlier page failed with the same provider error." : ex.Message;
    }

    private static void Add(PipelineResult result, ReadResult read)
    {
        result.Usage += read.Usage;
        result.Calls += read.Calls;
        result.CostUsd += read.CostUsd;
        result.Model = read.Model;
    }

    private static IEnumerable<string?> NumericRaw(IEnumerable<ReadRow> rows) =>
        rows.SelectMany(r => Fields.Numeric.Select(r.Get));

    private static void Finish(PipelineResult result, int readablePages)
    {
        var failed = result.Pages.Where(p => p.Status == "failed").OrderBy(p => p.Page).ToList();
        if (failed.Count > 0)
        {
            result.ErrorCode = failed[0].ErrorCode;
            result.ErrorMessage = failed.Count == readablePages
                ? failed[0].ErrorMessage
                : $"{failed.Count} of {readablePages} pages could not be read ({failed[0].ErrorCode}). The rows of the other pages are listed.";
            result.Status = result.Rows.Count > 0 ? ExtractionStatus.Partial : ExtractionStatus.Failed;
            return;
        }

        result.Status = ExtractionStatus.Completed;
        if (readablePages == 0)
        {
            result.ErrorCode = "no_table_content";
            result.ErrorMessage = "No page of the document holds a table (triage found no text-layer table and no scanned page).";
        }
    }

    // ---------- helpers: rows ----------

    private void BuildRows(PageWork item, NumberLocale documentLocale)
    {
        var source = item.Path == PagePath.Text ? "text" : "vision";
        var raw = item.Read!.Rows;
        var pageLocale = item.Table is { NumericTokens.Count: > 0 }
            ? Normaliser.DetectLocale(item.Table.NumericTokens)
            : Normaliser.DetectLocale(NumericRaw(raw));
        var locale = pageLocale != NumberLocale.Unknown ? pageLocale : documentLocale;
        item.Locale = locale;

        var matched = new HashSet<int>();
        foreach (var read in raw)
        {
            var state = RowFactory.Build(read, source, locale, item.Grounding, item.Content.Number);
            var record = new ExtractedRowRecord { State = state, Page = item.Content.Number, Path = source };
            if (item.Table is not null)
            {
                var line = FindLine(item.Table, read, state);
                if (line is not null)
                {
                    matched.Add(line.Index);
                    record.SourceRef = line.Label;
                    record.Snippet = Clip(line.JoinedText);
                    record.Bbox = Bbox(item.Content, line);
                }
                else
                {
                    record.SourceRef = $"p{item.Content.Number}";
                }

                if (state.Field(Fields.TagName).Grounded == false)
                {
                    state.Flags.Add(RowFlags.TagNotInSource);
                }

                if (item.Report.InjectionHits.Count > 0)
                {
                    state.Flags.Add(RowFlags.PageInjectionText);
                }
            }
            else
            {
                record.SourceRef = $"p{item.Content.Number}:{Label(read.SourceRef)}";
                record.Snippet = Clip(string.Join(" | ", Fields.All.Select(f => state.Raw.GetValueOrDefault(f)).Where(v => v is not null)));
            }

            item.Rows.Add(record);
        }

        if (item.Table is not null)
        {
            item.Report.UnmatchedRows = item.Table.DataRows.Where(r => !matched.Contains(r.Index)).Select(r => r.Label).ToList();
        }
    }

    private static string Label(string? sourceRef)
    {
        var cleaned = Regex.Replace(sourceRef ?? "", @"[^\p{L}\p{N} :._-]", string.Empty).Trim();
        return cleaned.Length == 0 ? "?" : cleaned.Length > 40 ? cleaned[..40] : cleaned;
    }

    private static string Clip(string text) => text.Length <= 900 ? text : text[..899] + "…";

    /// <summary>The reconstructed line a returned row came from: by the echoed label when it holds the tag, else by the tag itself.</summary>
    private static TableRow? FindLine(ReconstructedPage table, ReadRow read, RowState state)
    {
        var tag = GroundingIndex.Squash(state.Text(Fields.TagName) ?? read.TagName);
        var data = table.DataRows.ToList();
        var echoed = Regex.Match(read.SourceRef ?? "", @"L(?<n>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (echoed.Success && int.TryParse(echoed.Groups["n"].Value, out var n))
        {
            var byLabel = data.FirstOrDefault(r => r.Index == n);
            if (byLabel is not null && GroundingIndex.Squash(byLabel.JoinedText).Contains(tag, StringComparison.Ordinal))
            {
                return byLabel;
            }
        }

        return data.FirstOrDefault(r => GroundingIndex.Squash(r.JoinedText).Contains(tag, StringComparison.Ordinal));
    }

    private static double[] Bbox(PageContent page, TableRow line)
    {
        var w = Math.Max(1, page.Width);
        var h = Math.Max(1, page.Height);
        const double pad = 0.004;
        return
        [
            Math.Round(Math.Max(0, line.X0 / w - pad), 4),
            Math.Round(Math.Max(0, (h - line.Y1) / h - pad), 4),
            Math.Round(Math.Min(1, line.X1 / w + pad), 4),
            Math.Round(Math.Min(1, (h - line.Y0) / h + pad), 4),
        ];
    }

    private void AssessAll(List<ExtractedRowRecord> all, IReadOnlySet<string> projectTags)
    {
        var score = opts.Score;
        var byTag = all.Where(r => r.State.Text(Fields.TagName) is not null).GroupBy(r => r.State.Text(Fields.TagName)!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => r.State).ToList(), StringComparer.Ordinal);
        foreach (var record in all)
        {
            var name = record.State.Text(Fields.TagName);
            var others = name is not null && byTag.TryGetValue(name, out var same) ? same.Where(s => !ReferenceEquals(s, record.State)).ToList() : [];
            RowAssessor.Assess(record.State, new AssessContext(projectTags, others, score));
        }
    }

    // ---------- helpers: dual read ----------

    private async Task DualReadAsync(PageWork item, byte[] pdf, int pageCount, PipelineResult result, CancellationToken ct)
    {
        item.Report.DualRead = true;
        ReadResult second;
        try
        {
            second = await reader.ReadImageAsync(item.Content.Number, pageCount, renderer.RenderPng(pdf, item.Content.Number, opts.RenderDpi), RowReader.DualReadFeature, ct);
        }
        catch (LlmException ex)
        {
            // The second read is an extra check: if it cannot happen the first read stands, with no boost.
            logger.LogWarning("Dual read of page {Page} failed: {Code}", item.Content.Number, ex.Code);
            item.Report.DualRead = false;
            return;
        }

        Add(result, second);
        var visionRows = second.Rows.Select(r => RowFactory.Build(r, "vision", item.Locale, null, item.Content.Number)).ToList();
        var byTag = visionRows.Where(v => v.Text(Fields.TagName) is not null).GroupBy(v => v.Text(Fields.TagName)!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new Queue<RowState>(g), StringComparer.Ordinal);

        foreach (var record in item.Rows)
        {
            var name = record.State.Text(Fields.TagName);
            if (name is null || !byTag.TryGetValue(name, out var queue) || queue.Count == 0)
            {
                record.State.Flags.Add(RowFlags.DualReadMissing);
                continue;
            }

            var other = queue.Dequeue();
            foreach (var field in Fields.All)
            {
                var mine = record.State.Values.GetValueOrDefault(field);
                var theirs = other.Values.GetValueOrDefault(field);
                if (mine is null && theirs is null)
                {
                    continue;
                }

                record.State.Field(field).Dual = SameValue(mine, theirs) ? "agree" : "disagree";
            }
        }

        item.Report.DualReadOnly = byTag.Values.SelectMany(q => q).Select(r => r.Text(Fields.TagName)!).ToList();
    }

    private static bool SameValue(object? a, object? b) => (a, b) switch
    {
        (double x, double y) => Math.Abs(x - y) < 1e-9,
        (string x, string y) => string.Equals(Normalise(x), Normalise(y), StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim();
}

/// <summary>Turns one model row into a normalised <see cref="RowState"/> (before rules and scoring).</summary>
public static class RowFactory
{
    public static RowState Build(ReadRow read, string source, NumberLocale locale, GroundingIndex? grounding, int page)
    {
        var row = new RowState { Page = page };
        var unclear = read.UnclearKeys();
        foreach (var field in Fields.All)
        {
            var text = read.Get(field)?.Trim();
            row.Raw[field] = string.IsNullOrEmpty(text) ? null : text;
            var state = row.Field(field);
            state.Source = source;
            state.Unclear = !string.IsNullOrEmpty(text) && unclear.Contains(field);
            row.Values[field] = null;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            switch (field)
            {
                case Fields.TagName:
                    row.Values[field] = Normaliser.NormaliseTagName(text);
                    break;
                case Fields.Description:
                    row.Values[field] = Normaliser.NormaliseText(text);
                    break;
                case Fields.SignalType:
                    var (type, typeGuess, recognised) = Normaliser.NormaliseSignalType(text);
                    row.Values[field] = type?.ToString();
                    state.Guess = typeGuess;
                    if (!recognised)
                    {
                        state.ParseProblem = $"'{text}' is not a signal type Bosun knows (AI, AO, DI, DO or an input type such as 4-20mA).";
                    }

                    break;
                case Fields.Unit:
                    var unit = Normaliser.NormaliseUnit(text);
                    row.Values[field] = unit.Value;
                    state.Guess = unit.Guess;
                    break;
                default:
                    var number = Normaliser.ParseNumber(text, locale);
                    row.Values[field] = number.Value;
                    state.Guess = number.Guess;
                    if (number.Failed)
                    {
                        state.ParseProblem = number.Problem;
                    }

                    break;
            }

            if (grounding is not null && row.Values[field] is not null)
            {
                state.Grounded = grounding.Supports(field, text);
            }
        }

        return row;
    }
}
