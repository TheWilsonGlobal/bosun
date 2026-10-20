namespace Bosun.Extraction;

/// <summary>
/// "Extraction" configuration section (appsettings or environment variables such as <c>Extraction__StoragePath</c>). No new .env keys:
/// the model is the one LLM_MODEL names.
/// </summary>
public sealed class ExtractionOptions
{
    /// <summary>
    /// Directory of the document store. Empty = a "documents" directory next to the SQLite file, so a database copy brings its own
    /// store and a test or eval copy never writes into the real one.
    /// </summary>
    public string? StoragePath { get; set; }

    /// <summary>Upload cap (plan: 20 MB). Enforced while the body is copied, not only from the declared length.</summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024;

    public int MaxPages { get; set; } = 80;

    /// <summary>Resolution of the page images sent to the vision model (plan s6.3: 200 dpi).</summary>
    public int RenderDpi { get; set; } = 200;

    /// <summary>Pages read by the model at the same time (plan: cap of 2).</summary>
    public int MaxConcurrentPages { get; set; } = 2;

    /// <summary>Text pages that are always read a second time with the vision model, to compare (the "sample" of plan s6.3 step 5).</summary>
    public int DualReadSamplePages { get; set; } = 1;

    /// <summary>A text page is also dual-read when at least this share of its rows is not auto-accepted ("low-score rows").</summary>
    public double DualReadLowScoreShare { get; set; } = 0.2;

    /// <summary>Upper bound of dual-read pages per document, whatever the reasons (cost cap).</summary>
    public int DualReadMaxPages { get; set; } = 3;

    /// <summary>Output budget of one page reply, reasoning tokens included (a dense page is 40+ rows of 13 members).</summary>
    public int MaxOutputTokens { get; set; } = 16384;

    public double? Temperature { get; set; }

    public TriageOptions Triage { get; set; } = new();

    public ScoreOptions Score { get; set; } = new();
}
