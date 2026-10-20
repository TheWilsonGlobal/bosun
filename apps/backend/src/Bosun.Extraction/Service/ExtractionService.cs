using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ExtractionEntity = Bosun.Domain.Entities.Extraction;

namespace Bosun.Extraction;

/// <summary>Error codes of the extraction endpoints (the problem+json <c>code</c> member).</summary>
public static class ExtractionErrorCodes
{
    public const string NotAPdf = "not_a_pdf";
    public const string DocumentTooLarge = "document_too_large";
    public const string NoFile = "no_file";
    public const string TooManyPages = "too_many_pages";
    public const string NotReady = "extraction_not_ready";
    public const string UnresolvedMustFix = "unresolved_must_fix";
    public const string NoRowsToAccept = "no_rows_to_accept";
    public const string UnknownRows = "unknown_rows";
    public const string RowsAlreadyDecided = "rows_already_decided";
    public const string RowAlreadyAccepted = "row_already_accepted";
    public const string InvalidField = "invalid_field";
    public const string PageOutOfRange = "page_out_of_range";
    public const string DuplicateTagsChosen = "duplicate_tags_chosen";
    public const string AlreadyRunning = "extraction_already_running";
}

/// <summary>A patched row plus the other rows whose assessment changed with it (duplicates are judged across rows).</summary>
public sealed record PatchRowResultDto(ExtractedRowDto Row, IReadOnlyList<ExtractedRowDto> AlsoChanged, ExtractionSummaryDto Summary);

/// <summary>
/// The use cases around documents and extractions: upload, list, read, human edit, accept, page images. The pipeline itself is
/// <see cref="ExtractionPipeline"/>; the background run is <see cref="ExtractionRunner"/>. Accepting only ever creates DRAFT change
/// sets (source=extraction) through <see cref="ChangeSetService.CreateAsync"/>: nothing here approves or applies anything.
/// </summary>
public sealed class ExtractionService(
    IBosunDbContext db,
    WriteGate gate,
    ChangeSetService changeSets,
    IDocumentStore store,
    IPageRenderer renderer,
    ExtractionRunManager runs,
    IOptions<ExtractionOptions> options,
    TimeProvider clock)
{
    public const int MaxOpsPerChangeSet = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    private readonly ExtractionOptions opts = options.Value;

    // ---------- upload ----------

    public async Task<UploadResultDto> UploadAsync(int projectId, Stream content, string? clientFileName, UserContext user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var bytes = await ReadCappedAsync(content, ct);
        if (bytes.Length == 0)
        {
            throw new AppException(ErrorKind.Validation, ExtractionErrorCodes.NoFile, "The file is empty.");
        }

        // The magic bytes decide, not the file name or the Content-Type the client claimed.
        if (bytes.Length < PdfMagic.Length || !bytes.AsSpan(0, PdfMagic.Length).SequenceEqual(PdfMagic))
        {
            throw new AppException(ErrorKind.UnsupportedMedia, ExtractionErrorCodes.NotAPdf, "Only PDF files are accepted (the file does not start with %PDF-).");
        }

        PdfInfo info;
        try
        {
            info = PdfPages.Inspect(bytes);
        }
        catch (PdfUnreadableException ex)
        {
            throw new AppException(ErrorKind.Unprocessable, ex.Code, ex.Message);
        }

        if (info.PageCount is 0)
        {
            throw new AppException(ErrorKind.Unprocessable, PdfPages.CodeUnreadable, "The PDF has no pages.");
        }

        if (info.PageCount > opts.MaxPages)
        {
            throw new AppException(ErrorKind.Unprocessable, ExtractionErrorCodes.TooManyPages,
                $"The document has {info.PageCount} pages; the limit is {opts.MaxPages}.", new Dictionary<string, object?> { ["pages"] = info.PageCount, ["max"] = opts.MaxPages });
        }

        var stored = await store.SaveAsync(bytes, ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var document = new SourceDocument
            {
                ProjectId = projectId,
                DisplayName = SafeDisplayName(clientFileName),
                StoredName = stored,
                SizeBytes = bytes.Length,
                PageCount = info.PageCount,
                Sha256 = FileDocumentStore.Sha256Hex(bytes),
                UploadedBy = user.Name,
                UploadedAt = now,
            };
            var extraction = new ExtractionEntity
            {
                ProjectId = projectId,
                SourceDocument = document,
                Status = ExtractionStatus.Queued,
                PagesTotal = info.PageCount,
                CreatedBy = user.Name,
                CreatedAt = now,
            };
            using (await gate.EnterAsync(ct))
            {
                db.SourceDocuments.Add(document);
                db.Extractions.Add(extraction);
                await db.SaveChangesAsync(ct);
            }

            runs.Start(extraction.Id);
            return new UploadResultDto(document.Id, extraction.Id);
        }
        catch
        {
            store.Delete(stored);
            throw;
        }
    }

    /// <summary>
    /// Reads an already uploaded document again (a new extraction; the earlier one and its rows stay as they are). The way to retry
    /// after a provider failure without uploading the file a second time.
    /// </summary>
    public async Task<UploadResultDto> ExtractAgainAsync(int documentId, UserContext user, CancellationToken ct)
    {
        var document = await db.SourceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct) ?? throw AppException.NotFound("Document", documentId);
        var running = await db.Extractions.AsNoTracking().AnyAsync(e => e.SourceDocumentId == documentId && (e.Status == ExtractionStatus.Queued || e.Status == ExtractionStatus.Running), ct);
        if (running)
        {
            throw new AppException(ErrorKind.Conflict, ExtractionErrorCodes.AlreadyRunning, "This document is already being read. Wait for it to finish.");
        }

        var extraction = new ExtractionEntity
        {
            ProjectId = document.ProjectId,
            SourceDocumentId = document.Id,
            Status = ExtractionStatus.Queued,
            PagesTotal = document.PageCount,
            CreatedBy = user.Name,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        using (await gate.EnterAsync(ct))
        {
            db.Extractions.Add(extraction);
            await db.SaveChangesAsync(ct);
        }

        runs.Start(extraction.Id);
        return new UploadResultDto(document.Id, extraction.Id);
    }

    /// <summary>Copies the body into memory, refusing more than the cap. The declared length is not trusted: bytes are counted as they arrive.</summary>
    private async Task<byte[]> ReadCappedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > opts.MaxUploadBytes)
            {
                throw new AppException(ErrorKind.PayloadTooLarge, ExtractionErrorCodes.DocumentTooLarge,
                    $"The file is larger than {opts.MaxUploadBytes / (1024 * 1024)} MB.", new Dictionary<string, object?> { ["maxBytes"] = opts.MaxUploadBytes });
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A label for the UI only: the last path segment, no control or reserved characters, bounded length. The stored file name is
    /// generated, so a name such as NUL.pdf, CON.pdf or ..\..\x.pdf can neither break the upload nor reach the file system.
    /// </summary>
    public static string SafeDisplayName(string? clientFileName)
    {
        var name = clientFileName ?? "";
        name = name[(Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1)..];
        name = Regex.Replace(name, "[\\p{C}<>:\"|?*]", string.Empty).Trim().Trim('.');
        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            name = name[..(120 - Math.Min(extension.Length, 10))] + (extension.Length <= 10 ? extension : string.Empty);
        }

        return name.Length == 0 ? "document.pdf" : name;
    }

    // ---------- reads ----------

    public async Task<IReadOnlyList<DocumentDto>> ListDocumentsAsync(int projectId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
        {
            throw AppException.NotFound("Project", projectId);
        }

        var documents = await db.SourceDocuments.AsNoTracking().Where(d => d.ProjectId == projectId).OrderByDescending(d => d.Id).ToListAsync(ct);
        var ids = documents.Select(d => d.Id).ToList();
        var extractions = await db.Extractions.AsNoTracking().Where(e => ids.Contains(e.SourceDocumentId)).OrderByDescending(e => e.Id).ToListAsync(ct);
        var latest = extractions.GroupBy(e => e.SourceDocumentId).ToDictionary(g => g.Key, g => g.First());
        var latestIds = latest.Values.Select(e => e.Id).ToList();
        var rows = await db.ExtractedRows.AsNoTracking().Where(r => latestIds.Contains(r.ExtractionId)).ToListAsync(ct);
        var byExtraction = rows.GroupBy(r => r.ExtractionId).ToDictionary(g => g.Key, g => g.ToList());

        return documents.Select(d => ToDto(d, latest.TryGetValue(d.Id, out var e)
            ? new ExtractionBriefDto(e.Id, e.Status, e.CreatedAt, Summarise(byExtraction.GetValueOrDefault(e.Id) ?? []))
            : null)).ToList();
    }

    public async Task<ExtractionDto> GetAsync(int extractionId, CancellationToken ct)
    {
        var extraction = await db.Extractions.AsNoTracking().Include(e => e.SourceDocument).FirstOrDefaultAsync(e => e.Id == extractionId, ct)
            ?? throw AppException.NotFound("Extraction", extractionId);
        var rows = await db.ExtractedRows.AsNoTracking().Where(r => r.ExtractionId == extractionId).OrderBy(r => r.Seq).ToListAsync(ct);
        return ToDto(extraction, rows);
    }

    public async Task<PageImage> GetPageImageAsync(int extractionId, int page, int? dpi, int? rowId, CancellationToken ct)
    {
        var extraction = await db.Extractions.AsNoTracking().Include(e => e.SourceDocument).FirstOrDefaultAsync(e => e.Id == extractionId, ct)
            ?? throw AppException.NotFound("Extraction", extractionId);
        var document = extraction.SourceDocument!;
        if (page < 1 || page > document.PageCount)
        {
            throw new AppException(ErrorKind.NotFound, ExtractionErrorCodes.PageOutOfRange, $"The document has pages 1 to {document.PageCount}.");
        }

        double[]? crop = null;
        if (rowId is { } id)
        {
            var row = await db.ExtractedRows.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ExtractionId == extractionId, ct)
                ?? throw AppException.NotFound("Row", id);
            if (row.Page == page && row.BboxJson is not null && JsonSerializer.Deserialize<double[]>(row.BboxJson, Json) is { Length: 4 } box)
            {
                // Show some of the neighbouring rows for context.
                crop = [0, Math.Max(0, box[1] - 0.03), 1, Math.Min(1, box[3] + 0.03)];
            }
        }

        var bytes = await store.ReadAsync(document.StoredName, ct);
        var png = await Task.Run(() => renderer.RenderPng(bytes, page, Math.Clamp(dpi ?? 110, 50, 300), crop), ct);
        return new PageImage(png, crop is not null);
    }

    // ---------- human edit ----------

    public async Task<PatchRowResultDto> PatchRowAsync(int extractionId, int rowId, PatchRowBody body, CancellationToken ct)
    {
        if ((body.Fields is null || body.Fields.Count == 0) && body.Excluded is null)
        {
            throw AppException.Invalid("Nothing to change: send fields and/or excluded.");
        }

        using var _ = await gate.EnterAsync(ct);
        var extraction = await db.Extractions.FirstOrDefaultAsync(e => e.Id == extractionId, ct) ?? throw AppException.NotFound("Extraction", extractionId);
        EnsureReady(extraction);
        var rows = await db.ExtractedRows.Where(r => r.ExtractionId == extractionId).OrderBy(r => r.Seq).ToListAsync(ct);
        var target = rows.FirstOrDefault(r => r.Id == rowId) ?? throw AppException.NotFound("Row", rowId);
        if (target.State == ExtractedRowState.Accepted)
        {
            throw new AppException(ErrorKind.Conflict, ExtractionErrorCodes.RowAlreadyAccepted,
                "This row is already part of a change set. Edit the change set instead.", new Dictionary<string, object?> { ["changeSetId"] = target.ChangeSetId });
        }

        var states = rows.ToDictionary(r => r.Id, Load);
        var state = states[target.Id];
        var affectedTags = new HashSet<string>(StringComparer.Ordinal);
        AddTag(affectedTags, state);

        foreach (var (key, element) in body.Fields ?? [])
        {
            if (!Fields.IsKnown(key))
            {
                throw InvalidField(key, $"Unknown field '{key}'. Known fields: {string.Join(", ", Fields.All)}.");
            }

            var (value, raw) = ParseEdit(key, element);
            state.Values[key] = value;
            state.Raw[key] = raw;
            var field = state.Field(key);
            field.Source = "human";
            field.Grounded = null;
            field.Guess = false;
            field.Unclear = false;
            field.Dual = null;
            field.ParseProblem = null;
            if (key == Fields.TagName)
            {
                state.Flags.Remove(RowFlags.TagNotInSource);
            }

            target.HumanCorrected = true;
        }

        if (body.Excluded is { } excluded)
        {
            target.State = excluded ? ExtractedRowState.Excluded : ExtractedRowState.Pending;
        }

        AddTag(affectedTags, state);
        var projectTags = (await db.Tags.AsNoTracking().Where(t => t.ProjectId == extraction.ProjectId).Select(t => t.TagName).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var changed = Reassess(rows, states, target, affectedTags, projectTags);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AppException(ErrorKind.Conflict, "concurrent_modification", "Another change was saved at the same time. Reload and try again.");
        }

        var dtos = changed.Where(r => r.Id != target.Id).Select(ToDto).ToList();
        return new PatchRowResultDto(ToDto(target), dtos, Summarise(rows));
    }

    private static void AddTag(HashSet<string> tags, RowState state)
    {
        if (state.Text(Fields.TagName) is { } name)
        {
            tags.Add(name);
        }
    }

    /// <summary>Re-runs rules and confidence for the edited row and for every row that shares one of the affected tag names.</summary>
    private List<ExtractedRow> Reassess(List<ExtractedRow> rows, Dictionary<int, RowState> states, ExtractedRow target, HashSet<string> affectedTags, HashSet<string> projectTags)
    {
        var live = rows.Where(r => r.State != ExtractedRowState.Excluded).ToList();
        var byTag = live.Where(r => states[r.Id].Text(Fields.TagName) is not null)
            .GroupBy(r => states[r.Id].Text(Fields.TagName)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var toAssess = rows.Where(r => r.Id == target.Id || (states[r.Id].Text(Fields.TagName) is { } n && affectedTags.Contains(n))).Distinct().ToList();
        foreach (var row in toAssess)
        {
            var state = states[row.Id];
            var name = state.Text(Fields.TagName);
            var others = row.State == ExtractedRowState.Excluded || name is null || !byTag.TryGetValue(name, out var same)
                ? new List<RowState>()
                : same.Where(r => r.Id != row.Id).Select(r => states[r.Id]).ToList();
            RowAssessor.Assess(state, new AssessContext(projectTags, others, opts.Score));
            Store(row, state);
        }

        return toAssess;
    }

    private static (object? Value, string? Raw) ParseEdit(string field, JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return field == Fields.TagName ? throw InvalidField(field, "A row needs a tag name.") : (null, null);
        }

        string text;
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text = element.GetString() ?? "";
                break;
            case JsonValueKind.Number when field != Fields.Description && field != Fields.TagName && field != Fields.SignalType && field != Fields.Unit:
                text = element.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                break;
            default:
                throw InvalidField(field, $"'{field}' must be a string{(Fields.IsNumeric(field) ? " or a number" : "")} (or null to clear it).");
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return field == Fields.TagName ? throw InvalidField(field, "A row needs a tag name.") : (null, null);
        }

        switch (field)
        {
            case Fields.TagName:
                return (Normaliser.NormaliseTagName(text), text);
            case Fields.Description:
                return (Normaliser.NormaliseText(text), text);
            case Fields.SignalType:
                var (type, _, recognised) = Normaliser.NormaliseSignalType(text);
                return type is null || !recognised ? throw InvalidField(field, $"'{text}' is not a signal type (AI, AO, DI or DO).") : (type.Value.ToString(), text);
            case Fields.Unit:
                return (Normaliser.NormaliseUnit(text).Value, text);
            default:
                var number = Normaliser.ParseNumber(text);
                if (number.Failed)
                {
                    throw InvalidField(field, number.Problem ?? $"'{text}' is not a number.");
                }

                if (number.Guess)
                {
                    throw InvalidField(field, $"'{text}' is ambiguous (thousands or decimals?). Write it with a decimal point and no thousands separator.");
                }

                return (number.Value, text);
        }
    }

    private static AppException InvalidField(string field, string message) =>
        new(ErrorKind.Validation, ExtractionErrorCodes.InvalidField, message, new Dictionary<string, object?> { ["field"] = field });

    // ---------- accept ----------

    /// <summary>
    /// Turns the chosen rows (default: every open row) into DRAFT change sets of at most 200 operations each, never splitting a row.
    /// Refused while any chosen row is must-fix. Nothing is approved or applied.
    /// </summary>
    public async Task<AcceptResultDto> AcceptAsync(int extractionId, IReadOnlyList<int>? rowIds, UserContext user, CancellationToken ct)
    {
        var extraction = await db.Extractions.Include(e => e.SourceDocument).FirstOrDefaultAsync(e => e.Id == extractionId, ct)
            ?? throw AppException.NotFound("Extraction", extractionId);
        EnsureReady(extraction);
        var all = await db.ExtractedRows.Where(r => r.ExtractionId == extractionId).OrderBy(r => r.Seq).ToListAsync(ct);

        List<ExtractedRow> chosen;
        if (rowIds is { Count: > 0 })
        {
            var distinct = rowIds.Distinct().ToList();
            var unknown = distinct.Where(id => all.All(r => r.Id != id)).ToList();
            if (unknown.Count > 0)
            {
                throw new AppException(ErrorKind.Unprocessable, ExtractionErrorCodes.UnknownRows, "Some rows do not belong to this extraction.", new Dictionary<string, object?> { ["rowIds"] = unknown });
            }

            chosen = all.Where(r => distinct.Contains(r.Id)).ToList();
            var decided = chosen.Where(r => r.State != ExtractedRowState.Pending).Select(r => r.Id).ToList();
            if (decided.Count > 0)
            {
                throw new AppException(ErrorKind.Conflict, ExtractionErrorCodes.RowsAlreadyDecided, "Some of the chosen rows are already accepted or excluded.", new Dictionary<string, object?> { ["rowIds"] = decided });
            }
        }
        else
        {
            chosen = all.Where(r => r.State == ExtractedRowState.Pending).ToList();
        }

        if (chosen.Count == 0)
        {
            throw new AppException(ErrorKind.Unprocessable, ExtractionErrorCodes.NoRowsToAccept, "There are no open rows to accept.");
        }

        var blocked = chosen.Where(r => r.Band == Bands.MustFix).Select(r => r.Id).ToList();
        if (blocked.Count > 0)
        {
            throw new AppException(ErrorKind.Unprocessable, ExtractionErrorCodes.UnresolvedMustFix,
                $"{blocked.Count} row(s) are still must-fix (confidence below {Bands.ReviewThreshold:0.0#}). Correct them in the review, or exclude them, then accept again.",
                new Dictionary<string, object?> { ["rowIds"] = blocked, ["count"] = blocked.Count });
        }

        // Two chosen rows with one tag name would put the same createTag twice into the change set.
        var twice = chosen.Select(r => (r.Id, Name: Load(r).Text(Fields.TagName))).Where(x => x.Name is not null).GroupBy(x => x.Name!, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        if (twice.Count > 0)
        {
            throw new AppException(ErrorKind.Unprocessable, ExtractionErrorCodes.DuplicateTagsChosen,
                "Some chosen rows carry the same tag name. Exclude the repeats and accept again.",
                new Dictionary<string, object?> { ["tags"] = twice.Select(g => g.Key).ToList(), ["rowIds"] = twice.SelectMany(g => g.Select(x => x.Id)).ToList() });
        }

        // Group whole rows into change sets of at most 200 operations.
        var batches = new List<List<(ExtractedRow Row, IReadOnlyList<ChangeOp> Ops)>> { new() };
        var count = 0;
        foreach (var row in chosen)
        {
            var ops = RowOps.Build(Load(row));
            if (count + ops.Count > MaxOpsPerChangeSet && batches[^1].Count > 0)
            {
                batches.Add([]);
                count = 0;
            }

            batches[^1].Add((row, ops));
            count += ops.Count;
        }

        var created = new List<AcceptedChangeSetDto>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var part = batches.Count > 1 ? $" (part {i + 1}/{batches.Count})" : "";
            var title = Clip($"Extraction {extraction.Id}: {extraction.SourceDocument!.DisplayName}{part}", ChangeSetService.MaxTitleLength);
            var inputs = batch.SelectMany(b => b.Ops).Select(o => new OpInput(o, OldProvided: false)).ToList();
            var changeSet = await changeSets.CreateAsync(
                extraction.ProjectId,
                new CreateChangeSetRequest(title, ChangeSetSource.Extraction, inputs, extraction.Model, extraction.PromptVersion),
                user,
                ct);
            foreach (var (row, _) in batch)
            {
                row.State = ExtractedRowState.Accepted;
                row.ChangeSetId = changeSet.Id;
            }

            created.Add(new AcceptedChangeSetDto(changeSet.Id, changeSet.Title, changeSet.Ops.Count, batch.Select(b => b.Row.Id).ToList()));
        }

        using (await gate.EnterAsync(ct))
        {
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new AcceptResultDto(extraction.Id, created, chosen.Select(r => r.Id).ToList());
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static void EnsureReady(ExtractionEntity extraction)
    {
        if (extraction.Status is ExtractionStatus.Queued or ExtractionStatus.Running or ExtractionStatus.Failed)
        {
            throw new AppException(ErrorKind.Conflict, ExtractionErrorCodes.NotReady,
                $"The extraction is {extraction.Status.ToString().ToLowerInvariant()}; its rows can be edited and accepted once it has completed.",
                new Dictionary<string, object?> { ["status"] = extraction.Status });
        }
    }

    // ---------- mapping ----------

    public static RowState Load(ExtractedRow row)
    {
        var state = RowState.FromJson(row.ValuesJson, row.RawJson, row.FieldsJson, row.FlagsJson, row.ViolationsJson, row.Score, row.Band);
        state.Seq = row.Seq;
        state.Page = row.Page;
        return state;
    }

    public static void Store(ExtractedRow row, RowState state)
    {
        row.ValuesJson = state.ValuesToJson();
        row.RawJson = state.RawToJson();
        row.FieldsJson = state.FieldsToJson();
        row.FlagsJson = state.FlagsToJson();
        row.ViolationsJson = state.ViolationsToJson();
        row.Score = state.Score;
        row.Band = state.Band;
    }

    public static ExtractionSummaryDto Summarise(IReadOnlyCollection<ExtractedRow> rows)
    {
        var live = rows.Where(r => r.State != ExtractedRowState.Excluded).ToList();
        return new ExtractionSummaryDto(
            rows.Count,
            live.Count(r => r.Band == Bands.Auto),
            live.Count(r => r.Band == Bands.Review),
            live.Count(r => r.Band == Bands.MustFix),
            rows.Count(r => r.State == ExtractedRowState.Accepted),
            rows.Count(r => r.State == ExtractedRowState.Excluded),
            rows.Count(r => r.HumanCorrected),
            rows.Count(r => r.FlagsJson.Contains(RowFlags.SuspectedInjection, StringComparison.Ordinal)),
            rows.Count(r => r.State == ExtractedRowState.Pending && r.Band == Bands.MustFix));
    }

    private static DocumentDto ToDto(SourceDocument d, ExtractionBriefDto? latest) =>
        new(d.Id, d.ProjectId, d.DisplayName, d.SizeBytes, d.PageCount, d.UploadedBy, d.UploadedAt, latest);

    public static ExtractedRowDto ToDto(ExtractedRow row)
    {
        var state = Load(row);
        var fields = new Dictionary<string, FieldDto>(Fields.All.Count);
        foreach (var field in Fields.All)
        {
            var f = state.Field(field);
            fields[field] = new FieldDto(
                state.Values.GetValueOrDefault(field),
                state.Raw.GetValueOrDefault(field),
                f.Confidence,
                f.Band,
                f.Source,
                f.Flags,
                f.Violations);
        }

        return new ExtractedRowDto(
            row.Id, row.Seq, row.Page, row.SourcePath, row.SourceRef, row.SourceSnippet,
            row.BboxJson is null ? null : JsonSerializer.Deserialize<double[]>(row.BboxJson, Json),
            row.State, row.Band, row.Score, row.HumanCorrected, state.Flags, state.Violations, fields, row.ChangeSetId, row.Version);
    }

    private static ExtractionDto ToDto(ExtractionEntity e, List<ExtractedRow> rows)
    {
        var document = ToDto(e.SourceDocument!, null);
        return new ExtractionDto(
            e.Id, e.ProjectId, e.SourceDocumentId, document, e.Status,
            new ExtractionProgressDto(e.Phase, e.PagesDone, e.PagesTotal),
            e.CreatedAt, e.StartedAt, e.FinishedAt, e.Model, e.PromptVersion, e.ErrorCode, e.ErrorMessage,
            new ExtractionTokensDto(e.PromptTokens, e.CandidateTokens, e.ThoughtTokens, e.TotalTokens, e.LlmCalls, Math.Round(e.CostUsd, 6)),
            JsonSerializer.Deserialize<List<PageReport>>(e.PagesJson, Json) ?? [],
            Summarise(rows),
            rows.Select(ToDto).ToList(),
            rows.Where(r => r.ChangeSetId is not null).Select(r => r.ChangeSetId!.Value).Distinct().Order().ToList(),
            BandThresholdsDto.Current);
    }
}
