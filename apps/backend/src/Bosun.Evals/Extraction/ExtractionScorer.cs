using Bosun.Evals.Fixtures;
using Bosun.Extraction;

namespace Bosun.Evals.Extraction;

public readonly record struct Counts(int Tp, int Fp, int Fn)
{
    public double Precision => Tp + Fp == 0 ? 1 : (double)Tp / (Tp + Fp);

    public double Recall => Tp + Fn == 0 ? 1 : (double)Tp / (Tp + Fn);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public static Counts operator +(Counts a, Counts b) => new(a.Tp + b.Tp, a.Fp + b.Fp, a.Fn + b.Fn);
}

/// <summary>How one extracted row compares with the ground truth.</summary>
/// <param name="Wrong">Fields whose extracted value differs from the truth (or which the truth leaves empty).</param>
/// <param name="Missing">Fields the truth has a value for and the extraction does not.</param>
public sealed record RowOutcome(string Tag, int Page, string Kind, bool Extra, IReadOnlyList<string> Wrong, IReadOnlyList<string> Missing, string Band, IReadOnlyList<string> Flags)
{
    public bool Correct => !Extra && Wrong.Count == 0 && Missing.Count == 0;
}

public sealed class ExtractionScore
{
    public Counts Total { get; set; }

    public Dictionary<string, Counts> PerField { get; } = [];

    public List<RowOutcome> Rows { get; } = [];

    /// <summary>Tags of the ground truth no extracted row carries at all.</summary>
    public List<string> MissedRows { get; } = [];

    public int TruthRows { get; set; }
}

/// <summary>
/// Field-level precision / recall / F1 of an extraction against the ground truth, and the calibration of its bands. A field is a
/// true positive when the extracted value equals the truth; a wrong value counts as one false positive and one false negative; a
/// missing value as a false negative; a value the truth leaves empty as a false positive. Rows are matched by tag name.
/// </summary>
public static class ExtractionScorer
{
    public static ExtractionScore Score(IReadOnlyList<TruthRow> truth, IReadOnlyList<ExtractedRowRecord> extracted)
    {
        var score = new ExtractionScore { TruthRows = truth.Count };
        var pool = extracted.GroupBy(r => r.State.Text(Fields.TagName) ?? "", StringComparer.Ordinal).ToDictionary(g => g.Key, g => new Queue<ExtractedRowRecord>(g), StringComparer.Ordinal);
        var perField = Fields.All.ToDictionary(f => f, _ => new Counts(0, 0, 0));

        foreach (var row in truth)
        {
            if (!pool.TryGetValue(row.TagName, out var queue) || queue.Count == 0)
            {
                score.MissedRows.Add(row.TagName);
                foreach (var field in Fields.All.Where(f => TruthValue(row, f) is not null))
                {
                    perField[field] += new Counts(0, 0, 1);
                }

                continue;
            }

            var got = queue.Dequeue();
            var wrong = new List<string>();
            var missing = new List<string>();
            foreach (var field in Fields.All)
            {
                var expected = TruthValue(row, field);
                var actual = got.State.Values.GetValueOrDefault(field);
                if (expected is null && actual is null)
                {
                    continue;
                }

                if (expected is not null && Same(expected, actual))
                {
                    perField[field] += new Counts(1, 0, 0);
                }
                else if (expected is null)
                {
                    perField[field] += new Counts(0, 1, 0);
                    wrong.Add(field);
                }
                else if (actual is null)
                {
                    perField[field] += new Counts(0, 0, 1);
                    missing.Add(field);
                }
                else
                {
                    perField[field] += new Counts(0, 1, 1);
                    wrong.Add(field);
                }
            }

            score.Rows.Add(new RowOutcome(row.TagName, row.Page, row.Kind, false, wrong, missing, got.State.Band, got.State.Flags));
        }

        foreach (var queue in pool.Values)
        {
            foreach (var extra in queue)
            {
                foreach (var field in Fields.All.Where(f => extra.State.Values.GetValueOrDefault(f) is not null))
                {
                    perField[field] += new Counts(0, 1, 0);
                }

                score.Rows.Add(new RowOutcome(extra.State.Text(Fields.TagName) ?? "?", extra.Page, extra.Path == "vision" ? "scanned" : "text", true, [], [], extra.State.Band, extra.State.Flags));
            }
        }

        foreach (var (field, counts) in perField)
        {
            score.PerField[field] = counts;
            score.Total += counts;
        }

        return score;
    }

    /// <summary>The truth value of a field as the comparable object (string or double), null when the document leaves it empty.</summary>
    public static object? TruthValue(TruthRow row, string field) => field switch
    {
        Fields.TagName => row.TagName,
        Fields.Description => row.Description,
        Fields.SignalType => row.SignalType,
        Fields.Unit => row.Unit,
        Fields.RangeLow => row.RangeLow,
        Fields.RangeHigh => row.RangeHigh,
        Fields.AlarmHh => row.AlarmHh,
        Fields.AlarmH => row.AlarmH,
        Fields.AlarmL => row.AlarmL,
        Fields.AlarmLl => row.AlarmLl,
        Fields.DelayS => row.DelayS,
        _ => null,
    };

    private static bool Same(object expected, object? actual) => (expected, actual) switch
    {
        (double e, double a) => Math.Abs(e - a) < 1e-6,
        (string e, string a) => string.Equals(System.Text.RegularExpressions.Regex.Replace(e, @"\s+", " ").Trim(), System.Text.RegularExpressions.Regex.Replace(a, @"\s+", " ").Trim(), StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// Calibration: of the rows that contain at least one error (a wrong or missing field, or a row that should not exist), the share
    /// banded review or must-fix, i.e. the errors a person is sent to look at. 1.0 when there are no errors. Rows the extraction did not
    /// return at all cannot carry a band and are reported separately (<see cref="ExtractionScore.MissedRows"/>).
    /// </summary>
    public static (double Calibration, int ErrorRows, int Caught, int AutoErrors) Calibration(IEnumerable<RowOutcome> rows)
    {
        var errors = rows.Where(r => !r.Correct).ToList();
        var caught = errors.Count(r => r.Band != Bands.Auto);
        return (errors.Count == 0 ? 1.0 : (double)caught / errors.Count, errors.Count, caught, errors.Count - caught);
    }
}
