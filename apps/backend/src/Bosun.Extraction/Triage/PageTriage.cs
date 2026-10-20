namespace Bosun.Extraction;

/// <summary>Which reader a page goes to (plan s6.3 step 1).</summary>
public enum PagePath
{
    /// <summary>The text layer is usable: words + positions are rebuilt into a table and sent as text (cheaper).</summary>
    Text,

    /// <summary>No or unusable text layer (a scan, a broken font mapping): the rendered page image goes to the vision model.</summary>
    Vision,

    /// <summary>Nothing to read: a blank page, or a text page without any table content.</summary>
    Skip,
}

/// <param name="HasTableLines">Drawn horizontal or vertical borders, the classic table signal.</param>
/// <param name="TableLikeRows">Visual rows with at least three cells: a table without borders still shows up here.</param>
public sealed record PageTriageResult(
    int Page,
    PagePath Path,
    IReadOnlyList<string> Reasons,
    int CharCount,
    int WordCount,
    double GarbageRatio,
    int HorizontalLines,
    int VerticalLines,
    bool HasTableLines,
    int TableLikeRows,
    double ImageAreaRatio);

public sealed class TriageOptions
{
    /// <summary>Fewer non-blank characters than this = no usable text layer.</summary>
    public int MinTextChars { get; set; } = 50;

    /// <summary>More suspect characters (replacement, private-use, control) than this share = a broken text layer.</summary>
    public double MaxGarbageRatio { get; set; } = 0.2;

    /// <summary>A page that is mostly an image and carries at most this many characters is treated as a scan with a stray text overlay.</summary>
    public int ImagePageMaxChars { get; set; } = 400;

    public double ImageDominantRatio { get; set; } = 0.5;

    /// <summary>Borders: at least this many lines of one direction count as a ruled table.</summary>
    public int MinTableLines { get; set; } = 3;

    /// <summary>Rows with three or more cells needed to call an unruled text page a table.</summary>
    public int MinTableLikeRows { get; set; } = 3;
}

/// <summary>
/// Deterministic per-page triage: text-layer character count + table-line heuristic + garbage-text detection decide TEXT or
/// VISION (or SKIP). No model is involved, so the decision is reproducible and unit-tested on fixtures.
/// </summary>
public static class PageTriage
{
    public static PageTriageResult Decide(PageContent page, TriageOptions? options = null)
    {
        options ??= new TriageOptions();
        var hLines = page.HorizontalLines.Count;
        var vLines = page.VerticalLines.Count;
        var hasLines = hLines >= options.MinTableLines || vLines >= options.MinTableLines;
        var rows = TableReconstructor.GroupRows(page);
        var tableLike = rows.Count(r => r.Cells.Count >= 3);
        var dataRows = rows.Count(TableReconstructor.IsDataRow);

        PageTriageResult Result(PagePath path, params string[] reasons) =>
            new(page.Number, path, reasons, page.CharCount, page.Words.Count, Math.Round(page.GarbageRatio, 4), hLines, vLines, hasLines, tableLike, Math.Round(page.ImageAreaRatio, 4));

        if (page.CharCount < options.MinTextChars)
        {
            // Nothing drawn at all is a blank page; a page with images (or ruling) but no text is a scan.
            return page.ImageCount == 0 && !hasLines && page.CharCount < 10
                ? Result(PagePath.Skip, "blank_page")
                : Result(PagePath.Vision, "no_text_layer");
        }

        if (page.GarbageRatio > options.MaxGarbageRatio)
        {
            return Result(PagePath.Vision, "garbage_text_layer");
        }

        if (page.ImageAreaRatio >= options.ImageDominantRatio && page.CharCount <= options.ImagePageMaxChars)
        {
            return Result(PagePath.Vision, "image_page_with_little_text");
        }

        if (tableLike >= options.MinTableLikeRows)
        {
            return hasLines ? Result(PagePath.Text, "text_layer", "table_lines") : Result(PagePath.Text, "text_layer", "table_structure");
        }

        // Ruled page with a few tag rows (a short table): still a table.
        return hasLines && dataRows >= 1
            ? Result(PagePath.Text, "text_layer", "table_lines")
            : Result(PagePath.Skip, "no_table_content");
    }
}
