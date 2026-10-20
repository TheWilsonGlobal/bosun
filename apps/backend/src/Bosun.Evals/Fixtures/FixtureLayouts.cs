using System.Globalization;

namespace Bosun.Evals.Fixtures;

public enum NumStyle
{
    /// <summary>1200.5</summary>
    Dot,

    /// <summary>1,200.5</summary>
    DotThousands,

    /// <summary>1200,5</summary>
    Comma,

    /// <summary>1.200,5</summary>
    CommaThousands,
}

public sealed record ColSpec(string Title, double Width, bool Right = false);

public sealed class TableOptions
{
    public string Title { get; init; } = "";

    public string? Subtitle { get; init; }

    public string? Footer { get; init; }

    public string? Note { get; init; }

    public double Font { get; init; } = 7.5;

    public double RowHeight { get; init; } = 13;

    public bool RowLines { get; init; } = true;

    public bool VerticalLines { get; init; } = true;

    public bool Header { get; init; } = true;

    public double Left { get; init; } = 36;

    /// <summary>Group titles drawn above the column titles: (label, first column, last column), 0-based inclusive.</summary>
    public IReadOnlyList<(string Label, int From, int To)> Groups { get; init; } = [];

    /// <summary>True when the separator under row i is left out in column c (the cell continues in the next row: a merged cell).</summary>
    public Func<int, int, bool>? MergedBelow { get; init; }
}

/// <summary>Number, unit and signal-type spellings, and the table drawing shared by every text-layer fixture.</summary>
public static class FixtureLayouts
{
    // ---------- how things are printed ----------

    public static string Fmt(double? value, NumStyle style)
    {
        if (value is not { } v)
        {
            return "";
        }

        var text = Math.Abs(v).ToString("0.##", CultureInfo.InvariantCulture);
        var parts = text.Split('.');
        var integer = parts[0];
        var thousands = style is NumStyle.DotThousands or NumStyle.CommaThousands;
        if (thousands && integer.Length > 3)
        {
            var separator = style == NumStyle.DotThousands ? "," : ".";
            var groups = new List<string>();
            for (var end = integer.Length; end > 0; end -= 3)
            {
                groups.Insert(0, integer[Math.Max(0, end - 3)..end]);
            }

            integer = string.Join(separator, groups);
        }

        var decimalSeparator = style is NumStyle.Comma or NumStyle.CommaThousands ? "," : ".";
        return (v < 0 ? "-" : "") + integer + (parts.Length > 1 ? decimalSeparator + parts[1] : "");
    }

    /// <summary>The unit as one of several spellings a customer's export might use; the canonical unit is what the ground truth says.</summary>
    public static string PrintUnit(string? canonical, string variants, Random rng)
    {
        if (canonical is null)
        {
            return "";
        }

        string[] options = variants switch
        {
            "eu" => canonical switch
            {
                "°C" => ["degC", "degC", "°C"],
                "bar" => ["bar(g)", "bar", "barg"],
                "m3/h" => ["m³/h", "m3/h"],
                "rpm" => ["1/min", "rpm", "RPM"],
                "kW" => ["kW", "KW"],
                "L/h" => ["l/h", "L/h"],
                "mbar" => ["mbar", "mBar"],
                _ => [canonical],
            },
            "us" => canonical switch
            {
                "°C" => ["°C", "°C", "deg C"],
                "bar" => ["bar g", "bar", "bar"],
                "m3/h" => ["m3/hr", "m3/h"],
                "t/h" => ["t/hr", "t/h"],
                "kg/h" => ["kg/hr", "kg/h"],
                _ => [canonical],
            },
            "compact" => canonical switch
            {
                "°C" => ["C", "°C", "°C"],
                "%" => ["%", "pct"],
                "ppm" => ["ppm", "PPM"],
                _ => [canonical],
            },
            _ => [canonical],
        };
        return options[rng.Next(options.Length)];
    }

    public static string PrintType(TruthRow row, string variants, Random rng)
    {
        var type = row.SignalType ?? "";
        if (variants == "eu")
        {
            return type switch
            {
                "AI" when row.TagName.Contains("-TT", StringComparison.Ordinal) && rng.Next(2) == 0 => "Pt100",
                "AI" => "4-20mA",
                "AO" => "4-20mA out",
                "DI" => "Digital in",
                "DO" => "Digital out",
                _ => type,
            };
        }

        if (variants == "us")
        {
            return type switch { "AI" => "AI", "AO" => "AO", "DI" => "DI", "DO" => "DO", _ => type };
        }

        return type switch { "AI" => "Analog in", "AO" => "Analog out", "DI" => "Digital in", "DO" => "Digital out", _ => type };
    }

    // ---------- the table ----------

    /// <summary>Draws one page of a table (group titles, column titles, rows, borders, footer) and returns the y of the bottom of the last row.</summary>
    public static double Table(IPageDrawing d, TableOptions o, IReadOnlyList<ColSpec> columns, IReadOnlyList<string[]> rows, int page, int pages)
    {
        var xs = new double[columns.Count + 1];
        xs[0] = o.Left;
        for (var c = 0; c < columns.Count; c++)
        {
            xs[c + 1] = xs[c] + columns[c].Width;
        }

        var h = o.RowHeight;
        d.Text(o.Left, d.Height - 32, o.Title, 12, bold: true);
        if (o.Subtitle is not null)
        {
            d.Text(o.Left, d.Height - 46, o.Subtitle, o.Font + 0.5);
        }

        var top = d.Height - 60;
        var y = top;
        var tableTop = top;

        if (o.Groups.Count > 0)
        {
            foreach (var (label, from, to) in o.Groups)
            {
                d.TextCentered((xs[from] + xs[to + 1]) / 2, y - h + 3.6, label, o.Font, bold: true);
                d.Line(xs[from] + 1, y - h, xs[to + 1] - 1, y - h, 0.5);
            }

            y -= h;
        }

        if (o.Header)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                if (columns[c].Right)
                {
                    d.TextRight(xs[c + 1] - 3, y - h + 3.6, columns[c].Title, o.Font, bold: true);
                }
                else
                {
                    d.Text(xs[c] + 3, y - h + 3.6, columns[c].Title, o.Font, bold: true);
                }
            }

            y -= h;
            d.Line(xs[0], y, xs[^1], y, 0.9);
        }

        var firstRowTop = y;
        for (var i = 0; i < rows.Count; i++)
        {
            var rowTop = firstRowTop - (i * h);
            for (var c = 0; c < columns.Count; c++)
            {
                var text = rows[i][c];
                if (text.Length == 0)
                {
                    continue;
                }

                if (columns[c].Right)
                {
                    d.TextRight(xs[c + 1] - 3, rowTop - h + 3.6, text, o.Font);
                }
                else
                {
                    d.Text(xs[c] + 3, rowTop - h + 3.6, text, o.Font);
                }
            }

            if (o.RowLines)
            {
                var bottom = rowTop - h;
                if (o.MergedBelow is null)
                {
                    d.Line(xs[0], bottom, xs[^1], bottom, 0.3);
                }
                else
                {
                    for (var c = 0; c < columns.Count; c++)
                    {
                        if (i == rows.Count - 1 || !o.MergedBelow(i, c))
                        {
                            d.Line(xs[c], bottom, xs[c + 1], bottom, 0.3);
                        }
                    }
                }
            }
        }

        var tableBottom = firstRowTop - (rows.Count * h);
        if (o.VerticalLines)
        {
            foreach (var x in xs)
            {
                d.Line(x, tableTop - (o.Groups.Count > 0 ? h : 0) - (o.Header ? h : 0), x, tableBottom, 0.3);
            }

            d.Line(xs[0], tableTop - (o.Groups.Count > 0 ? h : 0), xs[^1], tableTop - (o.Groups.Count > 0 ? h : 0), 0.6);
        }

        if (o.Note is not null)
        {
            d.Text(o.Left, tableBottom - 16, o.Note, o.Font);
        }

        d.Text(o.Left, 24, o.Footer ?? "MV Example - I/O list IO-001 Rev B", 7);
        d.TextRight(xs[^1], 24, $"Page {page} of {pages}", 7);
        return tableBottom;
    }
}
