using Bosun.Evals.Fixtures;
using Bosun.Extraction;
using Bosun.Infrastructure.Configuration;

namespace Bosun.Extraction.Tests;

/// <summary>The committed fixtures (evals/extraction) and their ground truth.</summary>
public static class Fx
{
    public static string Directory { get; } = Path.Combine(RepoRoot.Resolve(), "evals", "extraction");

    public static string SafetyDirectory { get; } = Path.Combine(RepoRoot.Resolve(), "evals", "safety");

    public static byte[] Pdf(string id) => File.ReadAllBytes(Path.Combine(id.StartsWith("safety", StringComparison.Ordinal) ? SafetyDirectory : Directory, id + ".pdf"));

    public static FixtureTruth Truth(string id) => FixtureTruth.Load(Path.Combine(id.StartsWith("safety", StringComparison.Ordinal) ? SafetyDirectory : Directory, id + ".truth.json"));

    public static IReadOnlyList<PageContent> Pages(string id) => PdfPages.Read(Pdf(id));

    public static readonly string[] All = ["a-clean-table", "b-multipage-merged", "c-mixed-layouts", "d-scanned", "e-customer-io-list", "f-injection"];
}

/// <summary>Synthetic pages for tests that need exact control of what the text layer looks like.</summary>
public static class Synthetic
{
    public static PageContent Page(IReadOnlyList<PageWord> words, int chars = -1, double garbage = 0, int images = 0, double imageArea = 0, int hLines = 0, int vLines = 0) =>
        new(1, 842, 595, words,
            [.. Enumerable.Range(0, hLines).Select(i => new PageLine(36, 500 - (i * 13), 700, 500 - (i * 13)))],
            [.. Enumerable.Range(0, vLines).Select(i => new PageLine(36 + (i * 60), 100, 36 + (i * 60), 500))],
            chars >= 0 ? chars : words.Sum(w => w.Text.Length), images, imageArea, garbage);

    /// <summary>A row of words at a baseline, one word per cell at the given x positions.</summary>
    public static IEnumerable<PageWord> Row(double y, params (double X, string Text)[] cells) =>
        cells.Select(c => new PageWord(c.Text, c.X, y, c.X + (c.Text.Length * 4.2), y + 8));

    public static string Lorem(int words) => string.Join(' ', Enumerable.Repeat("lorem", words));
}
