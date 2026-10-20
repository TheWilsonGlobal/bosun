using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Extraction;

public sealed record Cell(string Text, double X0, double X1);

/// <summary>Words that share a baseline, grouped into cells (a gap wider than a space starts a new cell).</summary>
public sealed record VisualRow(double Y0, double Y1, IReadOnlyList<Cell> Cells)
{
    public double YCenter => (Y0 + Y1) / 2;
}

public enum TableRowKind
{
    /// <summary>Column titles (also a group title spanning columns).</summary>
    Header,

    /// <summary>A row of the table: one tag.</summary>
    Data,

    /// <summary>Any other line (a title, a section caption, a note).</summary>
    Text,
}

/// <param name="Index">1-based position among the lines of the page.</param>
/// <param name="Label">"p3:L12": the handle the model echoes back as source_ref.</param>
/// <param name="Columns">Text per table column (for Text lines: one entry with the whole line).</param>
/// <param name="FilledColumns">1-based columns whose text was carried down from a merged cell above.</param>
public sealed record TableRow(int Index, string Label, TableRowKind Kind, IReadOnlyList<string> Columns, IReadOnlyList<int> FilledColumns, double X0, double Y0, double X1, double Y1)
{
    public string JoinedText => string.Join(" | ", Columns);
}

/// <summary>The table of one text-layer page as clean candidate text (plan s6.3 step 2).</summary>
public sealed record ReconstructedPage(
    int Page,
    IReadOnlyList<TableRow> Rows,
    int ColumnCount,
    IReadOnlyList<string> HeaderColumns,
    bool HeaderBorrowed,
    string PromptText,
    IReadOnlyList<string> NumericTokens)
{
    public IEnumerable<TableRow> DataRows => Rows.Where(r => r.Kind == TableRowKind.Data);
}

/// <summary>The column titles of the document, taken from the first page that has them, for pages that lack their own.</summary>
public sealed record DocumentHeader(int Page, IReadOnlyList<string> Columns);

/// <summary>
/// Words + positions -> rows (shared baseline) -> cells (gap wider than a space) -> columns (vertical gutters that no data row
/// crosses) -> candidate table text. Handles a repeated header on every page, a page without its own header, a group header that
/// spans several columns, wrapped description lines, and cells merged across rows (detected from the drawn table borders, never
/// guessed from blank cells alone).
/// </summary>
public static partial class TableReconstructor
{
    [GeneratedRegex(@"[A-Za-z0-9]{2,6}-[A-Za-z0-9]{2,8}-[A-Za-z]{1,4}[0-9]{1,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex TagLike();

    [GeneratedRegex(@"^[+-]?[0-9][0-9.,]*$|^[+-]?[.,][0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    // ---------- rows and cells ----------

    public static IReadOnlyList<VisualRow> GroupRows(PageContent page)
    {
        if (page.Words.Count == 0)
        {
            return [];
        }

        var height = Median(page.Words.Select(w => w.Height).Where(h => h > 0).DefaultIfEmpty(10).ToList());
        var tolerance = Math.Max(1.5, 0.5 * height);
        var cellGap = Math.Max(3.0, 0.6 * height);

        var ordered = page.Words.OrderByDescending(w => w.YCenter).ThenBy(w => w.X0).ToList();
        var groups = new List<List<PageWord>>();
        var centers = new List<double>();
        foreach (var word in ordered)
        {
            var index = groups.Count - 1;
            if (index >= 0 && Math.Abs(word.YCenter - centers[index]) <= tolerance)
            {
                groups[index].Add(word);
                centers[index] = groups[index].Average(w => w.YCenter);
            }
            else
            {
                groups.Add([word]);
                centers.Add(word.YCenter);
            }
        }

        var rows = new List<VisualRow>(groups.Count);
        foreach (var group in groups)
        {
            var cells = new List<Cell>();
            var current = new List<PageWord>();
            // the gap that separates two cells scales with the text of the row itself (a 12 pt title is not a table)
            var rowGap = Math.Max(cellGap, 0.6 * group.Max(w => w.Height));
            foreach (var word in group.OrderBy(w => w.X0))
            {
                if (current.Count > 0 && word.X0 - current[^1].X1 > rowGap)
                {
                    cells.Add(ToCell(current));
                    current = [];
                }

                current.Add(word);
            }

            if (current.Count > 0)
            {
                cells.Add(ToCell(current));
            }

            rows.Add(new VisualRow(group.Min(w => w.Y0), group.Max(w => w.Y1), cells));
        }

        return rows;
    }

    private static Cell ToCell(List<PageWord> words) =>
        new(string.Join(' ', words.Select(w => w.Text)), words.Min(w => w.X0), words.Max(w => w.X1));

    /// <summary>The page as plain reading-order text, one line per visual row.</summary>
    public static string PlainText(PageContent page) =>
        string.Join('\n', GroupRows(page).Select(r => string.Join("  ", r.Cells.Select(c => c.Text))));

    /// <summary>Rows with at least three cells: the quick "this page holds a table" signal used by triage.</summary>
    public static int TableLikeRowCount(PageContent page) => GroupRows(page).Count(r => r.Cells.Count >= 3);

    // ---------- classification ----------

    /// <summary>
    /// A table row has several cells and either a tag-like name or several numbers. One long cell that happens to contain a tag-like
    /// word (a note, a title, a hostile sentence) is text, not a row: it must not become the first "data row" and push the real header
    /// out of the header position.
    /// </summary>
    public static bool IsDataRow(VisualRow row) =>
        row.Cells.Count >= 2 && (row.Cells.Any(c => TagLike().IsMatch(c.Text)) || row.Cells.Count(c => NumberLike().IsMatch(c.Text.Trim())) >= 3);

    private static bool IsNumber(string text) => NumberLike().IsMatch(text.Trim());

    // ---------- reconstruction ----------

    public static ReconstructedPage Reconstruct(PageContent page, DocumentHeader? documentHeader = null)
    {
        var visual = GroupRows(page);
        var firstData = visual.ToList().FindIndex(IsDataRow);
        var dataCount = visual.Count(IsDataRow);

        // Rows that define the columns: header and data rows that have (nearly) the full cell count.
        var fullCount = dataCount == 0 ? 0 : (int)Math.Ceiling(Median(visual.Where(IsDataRow).Select(r => (double)r.Cells.Count).ToList()) * 0.6);
        var projectionRows = visual
            .Where((r, i) => dataCount > 0 && r.Cells.Count >= Math.Max(2, fullCount) && (IsDataRow(r) || i < firstData || HeaderLike(r, visual)))
            .ToList();
        var columns = ColumnsFrom(projectionRows);

        var rows = new List<TableRow>();
        var headerColumns = new List<string>();
        var numericTokens = new List<string>();
        var previousData = (TableRow?)null;
        var pitch = RowPitch(visual);
        var useRowLines = UsesRowLines(page, visual, columns);

        for (var i = 0; i < visual.Count; i++)
        {
            var row = visual[i];
            var index = rows.Count + 1;
            var label = $"p{page.Number}:L{index}";
            var x0 = row.Cells.Min(c => c.X0);
            var x1 = row.Cells.Max(c => c.X1);

            if (IsDataRow(row) && columns.Count > 0)
            {
                var texts = AssignToColumns(row, columns, out _);
                var filled = new List<int>();
                if (useRowLines && previousData is not null && ReferenceEquals(rows.Count == 0 ? null : rows[^1], previousData))
                {
                    var previous = visual[i - 1];
                    for (var c = 0; c < columns.Count; c++)
                    {
                        if (texts[c].Length == 0 && previousData.Columns[c].Length > 0
                            && !SeparatorBetween(page, previous, row, (columns[c].X0 + columns[c].X1) / 2))
                        {
                            texts[c] = previousData.Columns[c];
                            filled.Add(c + 1);
                        }
                    }
                }

                var data = new TableRow(index, label, TableRowKind.Data, texts, filled, x0, row.Y0, x1, row.Y1);
                rows.Add(data);
                previousData = data;
                numericTokens.AddRange(row.Cells.Select(c => c.Text.Trim()).Where(IsNumber));
                continue;
            }

            if (IsDataRow(row))
            {
                // A table whose columns could not be told apart (a single data row): keep the cells as they are.
                rows.Add(new TableRow(index, label, TableRowKind.Data, row.Cells.Select(c => c.Text).ToList(), [], x0, row.Y0, x1, row.Y1));
                previousData = rows[^1];
                continue;
            }

            if (previousData is not null && ReferenceEquals(rows[^1], previousData) && row.Cells.Count == 1 && columns.Count > 0
                && visual[i - 1].YCenter - row.YCenter <= 1.4 * pitch && TryContinue(previousData, row, columns, out var merged))
            {
                // A wrapped line of a description: it belongs to the row above.
                rows[rows.IndexOf(previousData)] = merged!;
                previousData = merged;
                continue;
            }

            var isHeader = i < Math.Max(firstData, 0) && row.Cells.Count >= 2 && columns.Count > 0
                           || firstData >= 0 && i > firstData && row.Cells.Count >= 2 && HeaderLike(row, visual);
            if (isHeader)
            {
                var texts = AssignToColumns(row, columns, out var spans);
                var header = new TableRow(index, label, TableRowKind.Header, texts, [], x0, row.Y0, x1, row.Y1);
                rows.Add(header);
                if (spans == 0 && headerColumns.Count == 0)
                {
                    headerColumns = texts.ToList();
                }
                else if (spans == 0 && texts.Count(t => t.Length > 0) >= headerColumns.Count(t => t.Length > 0))
                {
                    headerColumns = texts.ToList(); // the last full header row (the column titles) wins over a group title above it
                }

                continue;
            }

            rows.Add(new TableRow(index, label, TableRowKind.Text, [string.Join("  ", row.Cells.Select(c => c.Text))], [], x0, row.Y0, x1, row.Y1));
        }

        var borrowed = false;
        if (headerColumns.All(string.IsNullOrEmpty) && documentHeader is not null && documentHeader.Columns.Count == columns.Count && columns.Count > 0)
        {
            headerColumns = documentHeader.Columns.ToList();
            borrowed = true;
        }

        return new ReconstructedPage(page.Number, rows, columns.Count, headerColumns, borrowed, BuildPrompt(page, rows, headerColumns, borrowed, documentHeader), numericTokens);
    }

    /// <summary>A non-data row with several cells that shares most of its words with a header row above the first data row: a repeated header.</summary>
    private static bool HeaderLike(VisualRow row, IReadOnlyList<VisualRow> all)
    {
        if (row.Cells.Count < 2 || row.Cells.Any(c => IsNumber(c.Text)))
        {
            return false;
        }

        var firstData = all.ToList().FindIndex(IsDataRow);
        var rowTokens = Tokens(row);
        if (rowTokens.Count == 0)
        {
            return false;
        }

        return all.Take(Math.Max(firstData, 0))
            .Where(r => !ReferenceEquals(r, row) && r.Cells.Count >= 2 && !r.Cells.Any(c => IsNumber(c.Text)))
            .Any(h => rowTokens.Intersect(Tokens(h)).Count() >= 0.5 * rowTokens.Count);
    }

    private static HashSet<string> Tokens(VisualRow row) =>
        row.Cells.SelectMany(c => c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    private static double RowPitch(IReadOnlyList<VisualRow> rows)
    {
        var gaps = new List<double>();
        for (var i = 1; i < rows.Count; i++)
        {
            gaps.Add(rows[i - 1].YCenter - rows[i].YCenter);
        }

        return gaps.Count == 0 ? 12 : Median(gaps.Where(g => g > 0).DefaultIfEmpty(12).ToList());
    }

    // ---------- columns ----------

    private sealed record Column(double X0, double X1);

    private static List<Column> ColumnsFrom(IReadOnlyList<VisualRow> rows)
    {
        var intervals = rows.SelectMany(r => r.Cells).Select(c => (c.X0, c.X1)).OrderBy(i => i.X0).ToList();
        var columns = new List<Column>();
        foreach (var (x0, x1) in intervals)
        {
            if (columns.Count > 0 && x0 <= columns[^1].X1 + 0.5)
            {
                columns[^1] = columns[^1] with { X1 = Math.Max(columns[^1].X1, x1) };
            }
            else
            {
                columns.Add(new Column(x0, x1));
            }
        }

        return columns;
    }

    /// <summary>Puts every cell of a row into the column it overlaps most; a cell that reaches into further columns is a spanning cell.</summary>
    private static string[] AssignToColumns(VisualRow row, IReadOnlyList<Column> columns, out int spans)
    {
        spans = 0;
        var texts = new string[columns.Count];
        Array.Fill(texts, string.Empty);
        foreach (var cell in row.Cells)
        {
            var overlaps = columns
                .Select((c, i) => (Index: i, Overlap: Math.Min(cell.X1, c.X1) - Math.Max(cell.X0, c.X0)))
                .Where(o => o.Overlap > 0)
                .ToList();
            if (overlaps.Count == 0)
            {
                continue;
            }

            if (overlaps.Count(o => o.Overlap >= 0.25 * (cell.X1 - cell.X0)) >= 2)
            {
                spans++;
            }

            var best = overlaps.OrderByDescending(o => o.Overlap).First().Index;
            texts[best] = texts[best].Length == 0 ? cell.Text : texts[best] + " " + cell.Text;
        }

        return texts;
    }

    /// <summary>
    /// A single cell right under a data row, starting at the left edge of a column other than the first and not a number, is the
    /// wrapped second line of that row's text. (A note under the table starts at the left margin, in the first column.)
    /// </summary>
    private static bool TryContinue(TableRow previous, VisualRow row, IReadOnlyList<Column> columns, out TableRow? merged)
    {
        merged = null;
        var cell = row.Cells[0];
        var column = columns.Select((c, i) => (Index: i, Overlap: Math.Min(cell.X1, c.X1) - Math.Max(cell.X0, c.X0))).OrderByDescending(o => o.Overlap).First();
        if (column.Overlap <= 0 || column.Index == 0 || previous.Columns[column.Index].Length == 0 || IsNumber(cell.Text)
            || Math.Abs(cell.X0 - columns[column.Index].X0) > 2.5)
        {
            return false;
        }

        var texts = previous.Columns.ToArray();
        texts[column.Index] = texts[column.Index] + " " + cell.Text;
        merged = previous with { Columns = texts, Y0 = Math.Min(previous.Y0, row.Y0) };
        return true;
    }

    // ---------- merged cells (from the drawn borders) ----------

    /// <summary>True when the table draws a border line under most of its rows (so a missing line is meaningful).</summary>
    private static bool UsesRowLines(PageContent page, IReadOnlyList<VisualRow> visual, IReadOnlyList<Column> columns)
    {
        if (page.HorizontalLines.Count < 3 || columns.Count == 0)
        {
            return false;
        }

        var data = visual.Where(IsDataRow).ToList();
        if (data.Count < 3)
        {
            return false;
        }

        var tableLeft = columns[0].X0;
        var tableRight = columns[^1].X1;
        var boundaries = 0;
        var withLine = 0;
        for (var i = 1; i < data.Count; i++)
        {
            boundaries++;
            // the border under a row may be drawn in pieces (merged cells leave gaps): count how much of the table width it covers
            var covered = page.HorizontalLines
                .Where(l => LineBetween(l, data[i - 1], data[i]))
                .Sum(l => Math.Max(0, Math.Min(l.X1, tableRight) - Math.Max(l.X0, tableLeft)));
            if (covered >= 0.5 * (tableRight - tableLeft))
            {
                withLine++;
            }
        }

        return withLine >= 0.5 * boundaries;
    }

    private static bool LineBetween(PageLine line, VisualRow upper, VisualRow lower)
    {
        var margin = 1.0;
        return line.Y0 <= upper.Y0 + margin && line.Y0 >= lower.Y1 - margin;
    }

    private static bool SeparatorBetween(PageContent page, VisualRow upper, VisualRow lower, double columnCenter) =>
        page.HorizontalLines.Any(l => LineBetween(l, upper, lower) && l.X0 <= columnCenter && l.X1 >= columnCenter);

    // ---------- prompt text ----------

    private static string BuildPrompt(PageContent page, IReadOnlyList<TableRow> rows, IReadOnlyList<string> headerColumns, bool borrowed, DocumentHeader? documentHeader)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"PAGE {page.Number} (text layer, table rebuilt from word positions; cells separated by ' | ', an empty cell is empty)\n");
        if (headerColumns.Any(h => h.Length > 0))
        {
            var numbered = string.Join(" | ", headerColumns.Select((h, i) => $"{i + 1}={(h.Length == 0 ? "(untitled)" : Clean(h))}"));
            sb.Append(borrowed
                ? string.Create(CultureInfo.InvariantCulture, $"COLUMNS (this page has no header of its own; titles taken from page {documentHeader!.Page}): {numbered}\n")
                : $"COLUMNS: {numbered}\n");
        }

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case TableRowKind.Header:
                    sb.Append($"{row.Label} [HEADER] {string.Join(" | ", row.Columns.Select(Clean))}\n");
                    break;
                case TableRowKind.Text:
                    sb.Append($"{row.Label} [TEXT] {Clean(row.Columns[0])}\n");
                    break;
                default:
                    // the marker goes before the cells so that an empty last cell can never be confused with it
                    sb.Append(row.Label);
                    if (row.FilledColumns.Count > 0)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $" [merged above: column {string.Join(",", row.FilledColumns)}]");
                    }

                    sb.Append($": {string.Join(" | ", row.Columns.Select(Clean))}\n");
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Keeps the cell separator unambiguous and neutralises the data-block delimiters inside document text.</summary>
    public static string Clean(string text) =>
        text.Replace("|", "¦", StringComparison.Ordinal).Replace("<<<", "‹‹‹", StringComparison.Ordinal).Replace(">>>", "›››", StringComparison.Ordinal);

    // ---------- helpers ----------

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The column titles of a reconstructed page when it has a full header; used to carry titles to pages without one.</summary>
    public static DocumentHeader? HeaderOf(ReconstructedPage page) =>
        page.HeaderColumns.Count(h => h.Length > 0) >= 3 && !page.HeaderBorrowed ? new DocumentHeader(page.Page, page.HeaderColumns) : null;
}
