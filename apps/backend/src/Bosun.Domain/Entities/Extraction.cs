namespace Bosun.Domain.Entities;

/// <summary>Lifecycle of one extraction run (plan s6.3). Completed, Partial and Failed are terminal.</summary>
public enum ExtractionStatus
{
    Queued = 0,
    Running = 1,

    /// <summary>Every page was read.</summary>
    Completed = 2,

    /// <summary>Some pages could not be read (the provider failed for them); the rows of the other pages are usable.</summary>
    Partial = 3,
    Failed = 4,
}

/// <summary>What happened to an extracted row in the review: still open, turned into a change set, or dismissed by the engineer.</summary>
public enum ExtractedRowState
{
    Pending = 0,
    Accepted = 1,
    Excluded = 2,
}

/// <summary>
/// An uploaded customer document (a PDF). The bytes live in the document store under <see cref="StoredName"/>, a name the
/// server generated: the client file name is a label for the UI and is never used as a path.
/// </summary>
public sealed class SourceDocument : VersionedEntity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }

    /// <summary>The client file name, sanitised, for display only.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Generated file name inside the document store ("{guid}.pdf").</summary>
    public string StoredName { get; set; } = "";
    public long SizeBytes { get; set; }
    public int PageCount { get; set; }

    /// <summary>Lowercase hex SHA-256 of the bytes.</summary>
    public string Sha256 { get; set; } = "";
    public string UploadedBy { get; set; } = "";
    public DateTime UploadedAt { get; set; }
    public List<Extraction> Extractions { get; set; } = [];
}

/// <summary>One run of the extraction pipeline over a document: status, per-page triage report, token spend, and its rows.</summary>
public sealed class Extraction : VersionedEntity
{
    public int ProjectId { get; set; }
    public int SourceDocumentId { get; set; }
    public SourceDocument? SourceDocument { get; set; }
    public ExtractionStatus Status { get; set; }

    /// <summary>"triage", "reading", "dual_read", "validating" while running; null otherwise.</summary>
    public string? Phase { get; set; }
    public int PagesTotal { get; set; }
    public int PagesDone { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Model { get; set; }

    /// <summary>"{prompt}@{hash}" of the extraction prompt file.</summary>
    public string? PromptVersion { get; set; }

    /// <summary>Stable error code (llm_rate_limited ..., server_restarted, pdf_unreadable) for failed and partial runs.</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>JSON array: one entry per page with the triage decision and the page outcome.</summary>
    public string PagesJson { get; set; } = "[]";
    public int LlmCalls { get; set; }
    public int PromptTokens { get; set; }
    public int CandidateTokens { get; set; }
    public int ThoughtTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>ESTIMATE in USD from the price table.</summary>
    public double CostUsd { get; set; }
    public List<ExtractedRow> Rows { get; set; } = [];
}

/// <summary>
/// One tag row read from the document, with per-field confidence. The JSON columns hold the values, the verbatim strings
/// and the per-field assessment (shapes defined by Bosun.Extraction).
/// </summary>
public sealed class ExtractedRow : VersionedEntity
{
    public int ExtractionId { get; set; }
    public Extraction? Extraction { get; set; }

    /// <summary>Position in document order (starts at 1).</summary>
    public int Seq { get; set; }
    public int Page { get; set; }

    /// <summary>"text" or "vision": which path read this row.</summary>
    public string SourcePath { get; set; } = "text";

    /// <summary>Where on the page the row was found (for example "p3:r12"); a label, never a path.</summary>
    public string? SourceRef { get; set; }

    /// <summary>The row as the document printed it (reconstructed text), for the review grid.</summary>
    public string? SourceSnippet { get; set; }

    /// <summary>JSON [x0,y0,x1,y1] as fractions of the page (origin top-left) when the position of the row is known.</summary>
    public string? BboxJson { get; set; }

    /// <summary>JSON object: normalised values by field key.</summary>
    public string ValuesJson { get; set; } = "{}";

    /// <summary>JSON object: the strings exactly as the model read them, by field key.</summary>
    public string RawJson { get; set; } = "{}";

    /// <summary>JSON object: per-field confidence, band, source, flags and violations.</summary>
    public string FieldsJson { get; set; } = "{}";

    /// <summary>JSON array of row-level flags (suspected_injection, exact_duplicate ...).</summary>
    public string FlagsJson { get; set; } = "[]";

    /// <summary>JSON array of rule violations of the row (code, severity, field, message).</summary>
    public string ViolationsJson { get; set; } = "[]";

    /// <summary>The lowest field confidence of the row.</summary>
    public double Score { get; set; }

    /// <summary>"auto", "review" or "must_fix".</summary>
    public string Band { get; set; } = "review";
    public bool HumanCorrected { get; set; }
    public ExtractedRowState State { get; set; }

    /// <summary>The draft change set this row went into (set once accepted).</summary>
    public int? ChangeSetId { get; set; }
}
