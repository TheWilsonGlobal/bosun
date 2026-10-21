using System.Text.Json;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain;
using Bosun.Domain.Entities;
using Bosun.Evals.Fixtures;
using Bosun.Evals.Offline;
using Bosun.Extraction;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bosun.Extraction.Tests;

/// <summary>Regression tests for the reviewer findings on phases 3-5 (resource limits, concurrency, spend cap, second-order injection, grounding).</summary>
public sealed class ReviewFixTests
{
    private static readonly IReadOnlySet<string> SeedTags = new HashSet<string> { "ME-EXH-TT01", "ME-EXH-TT02", "ME-EXH-TT03" };

    private static async Task<AppException> Refused(Func<Task> action) => (await action.Should().ThrowAsync<AppException>()).Which;

    private static Dictionary<string, JsonElement> Json(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(f.Value));

    private sealed class EditingLlm(ILlmClient inner, Func<ReadReply, ReadReply> edit) : ILlmClient
    {
        public string Provider => inner.Provider;

        public string DefaultModel => inner.DefaultModel;

        public int DefaultMaxOutputTokens => inner.DefaultMaxOutputTokens;

        public int MaxOutputTokensCap => inner.MaxOutputTokensCap;

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            var response = await inner.GenerateAsync(request, cancellationToken);
            var reply = JsonSerializer.Deserialize<ReadReply>(response.Text, StructuredJson.Options)!;
            return response with { Parts = [new TextPart(JsonSerializer.Serialize(edit(reply), StructuredJson.Options))] };
        }
    }

    private static ExtractionPipeline Pipeline(ILlmClient llm, ExtractionOptions? options = null, IPageRenderer? renderer = null)
    {
        var o = Options.Create(options ?? new ExtractionOptions { DualReadSamplePages = 0, DualReadLowScoreShare = 2 });
        var reader = new RowReader(new StructuredLlm(llm), llm, new ExtractionPrompts(), o);
        return new ExtractionPipeline(reader, renderer ?? new PdfiumPageRenderer(), o, NullLogger<ExtractionPipeline>.Instance);
    }

    private static ExtractionHost HostFor(string fixture, Dictionary<string, string?>? config = null, Func<ILlmClient, ILlmClient>? wrap = null)
    {
        ILlmClient llm = new TableParsingLlm(page => TruthReader.ReadRowsOf(Fx.Truth(fixture).Rows.Where(r => r.Page == page)));
        return new ExtractionHost(wrap is null ? llm : wrap(llm), config);
    }

    // ---------- finding 1: a hostile number cannot fail the extraction ----------

    [Fact]
    public async Task A_cell_with_an_astronomical_exponent_marks_that_row_must_fix_and_the_extraction_still_completes()
    {
        var llm = new EditingLlm(new TableParsingLlm(), reply => new ReadReply([.. reply.Rows.Select((r, i) => i == 0 ? r with { AlarmH = "1e99999999999" } : r)]));

        var result = await Pipeline(llm).RunAsync(Fx.Pdf("a-clean-table"), SeedTags);

        result.Status.Should().Be(ExtractionStatus.Completed);
        result.Rows.Should().HaveCount(50);
        var bad = result.Rows.First(r => r.State.Raw[Fields.AlarmH] == "1e99999999999");
        bad.State.Field(Fields.AlarmH).ParseProblem.Should().Contain("out of range");
        bad.State.Band.Should().Be(Bands.MustFix);
        result.Rows.Where(r => r.State.Raw[Fields.AlarmH] != "1e99999999999").Should().OnlyContain(r => r.State.Band == Bands.Auto);
    }

    [Fact]
    public void A_row_that_cannot_be_processed_is_kept_raw_flagged_and_stays_must_fix_through_assessment()
    {
        var read = new ReadRow("ME-EXH-TT01", "Exhaust", "AI", "°C", "0", "600", null, "95", null, null, null, "p1:L1", []);

        var row = RowFactory.Unprocessable(read, "text", 1);
        RowAssessor.Assess(row, AssessContext.Alone());

        row.Flags.Should().Contain(RowFlags.ProcessingFailed);
        row.Raw[Fields.TagName].Should().Be("ME-EXH-TT01");
        row.Band.Should().Be(Bands.MustFix);
        row.Score.Should().Be(0);
    }

    [Fact]
    public async Task A_range_in_an_alarm_cell_is_not_read_as_its_first_number()
    {
        var llm = new EditingLlm(new TableParsingLlm(), reply => new ReadReply([.. reply.Rows.Select((r, i) => i switch
        {
            0 => r with { AlarmH = "80-90" },
            1 => r with { AlarmH = "95 100" },
            _ => r,
        })]));

        var result = await Pipeline(llm).RunAsync(Fx.Pdf("a-clean-table"), SeedTags);

        var range = result.Rows.First(r => r.State.Raw[Fields.AlarmH] == "80-90");
        range.State.Values[Fields.AlarmH].Should().BeNull("a range is not a single number");
        range.State.Field(Fields.AlarmH).ParseProblem.Should().Contain("range");
        range.State.Band.Should().Be(Bands.MustFix);

        var grouped = result.Rows.First(r => r.State.Raw[Fields.AlarmH] == "95 100");
        grouped.State.Field(Fields.AlarmH).Guess.Should().BeTrue();
        grouped.State.Band.Should().NotBe(Bands.Auto, "95 100 may be one number or two");
    }

    // ---------- finding 4: PDF resource limits ----------

    private static byte[] PdfWithPage(double width, double height, Action<MiniPdfPage>? draw = null)
    {
        var pdf = new MiniPdf();
        var page = pdf.AddPage(width, height);
        page.Text(20, 40, "ME-EXH-TT01 Exhaust gas temperature");
        draw?.Invoke(page);
        return pdf.Build();
    }

    [Fact]
    public async Task An_upload_with_an_enormous_media_box_is_refused_with_page_too_large_and_nothing_is_stored()
    {
        using var host = new ExtractionHost();
        var pdf = PdfWithPage(20_000, 20_000);

        var refused = await Refused(async () =>
        {
            await using var scope = host.Scope();
            using var stream = new MemoryStream(pdf);
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, stream, "poster.pdf", ExtractionHost.Engineer, CancellationToken.None);
        });

        refused.Code.Should().Be("page_too_large");
        refused.Kind.Should().Be(ErrorKind.Unprocessable);
        refused.Extensions.Should().ContainKey("page");
        (Directory.Exists(host.StoreDir) ? Directory.GetFiles(host.StoreDir) : []).Should().BeEmpty();
        (await host.WithDbAsync(db => db.SourceDocuments.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_page_at_the_limit_is_accepted_and_the_limit_is_configurable()
    {
        using var host = new ExtractionHost(config: new() { ["Extraction:MaxPageDimensionPoints"] = "600" });

        var refused = await Refused(async () =>
        {
            await using var scope = host.Scope();
            using var stream = new MemoryStream(PdfWithPage(842, 595));
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, stream, "a4.pdf", ExtractionHost.Engineer, CancellationToken.None);
        });

        refused.Code.Should().Be("page_too_large", "842 points is above the configured 600");
    }

    [Fact]
    public async Task Opening_the_pdf_is_bounded_by_a_timeout()
    {
        using var host = new ExtractionHost(config: new() { ["Extraction:InspectTimeoutSeconds"] = "0.0001" });

        var refused = await Refused(async () =>
        {
            await using var scope = host.Scope();
            using var stream = new MemoryStream(Fx.Pdf("b-multipage-merged"));
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, stream, "slow.pdf", ExtractionHost.Engineer, CancellationToken.None);
        });

        refused.Code.Should().Be("pdf_too_complex");
    }

    [Fact]
    public void The_render_resolution_is_lowered_until_the_bitmap_fits_the_pixel_budget()
    {
        // A4 at 200 dpi is about 3.9 million pixels: unchanged.
        PdfiumPageRenderer.EffectiveDpi(595, 842, 200, 36_000_000).Should().Be(200);

        // A 40 x 40 inch page at 200 dpi would be 64 million pixels: lowered so width * height <= 36 million.
        var dpi = PdfiumPageRenderer.EffectiveDpi(2880, 2880, 200, 36_000_000)!.Value;
        dpi.Should().BeLessThan(200);
        ((long)(2880 / 72.0 * dpi) * (long)(2880 / 72.0 * dpi)).Should().BeLessThanOrEqualTo(36_000_000);

        // Not even the lowest legible resolution fits: refused, never floored at a "minimum" that ignores the budget.
        PdfiumPageRenderer.EffectiveDpi(30_000, 30_000, 200, 36_000_000).Should().BeNull();
    }

    [Fact]
    public void A_rendered_page_never_has_more_pixels_than_the_budget()
    {
        var bytes = PdfWithPage(2000, 2000);
        var renderer = new PdfiumPageRenderer(maxPixels: 1_000_000);

        var png = renderer.RenderPng(bytes, 1, 200);

        var width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        var height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        ((long)width * height).Should().BeLessThanOrEqualTo(1_000_000);
        width.Should().BeGreaterThan(100, "still legible");
    }

    [Fact]
    public void A_page_too_large_to_render_legibly_throws_a_specific_exception()
    {
        var bytes = PdfWithPage(30_000, 30_000);

        var act = () => new PdfiumPageRenderer().RenderPng(bytes, 1, 200);

        act.Should().Throw<PageTooLargeException>().WithMessage("*too large to render*");
    }

    private sealed class ThrowingRenderer(int badPage) : IPageRenderer
    {
        private readonly PdfiumPageRenderer inner = new();

        public byte[] RenderPng(byte[] pdf, int page, int dpi, double[]? crop = null) =>
            page == badPage ? throw new PageTooLargeException($"Page {page} is too large to render legibly.") : inner.RenderPng(pdf, page, dpi, crop);
    }

    [Fact]
    public async Task One_page_that_cannot_be_rendered_fails_that_page_only()
    {
        var truth = Fx.Truth("d-scanned");
        var llm = new TableParsingLlm(page => TruthReader.ReadRowsOf(truth.Rows.Where(r => r.Page == page)));
        var firstVisionPage = Fx.Pages("d-scanned").Count > 1 ? 1 : 1;

        var result = await Pipeline(llm, renderer: new ThrowingRenderer(firstVisionPage)).RunAsync(Fx.Pdf("d-scanned"), SeedTags);

        var failed = result.Pages.Where(p => p.Status == "failed").ToList();
        failed.Should().ContainSingle().Which.ErrorCode.Should().Be("page_too_large");
        failed[0].Page.Should().Be(firstVisionPage);
        result.Status.Should().Be(ExtractionStatus.Partial);
        result.Rows.Should().NotBeEmpty("the other pages were read");
    }

    [Fact]
    public void A_page_with_far_more_words_than_the_limit_is_capped_and_says_so()
    {
        var bytes = PdfWithPage(595, 842, page =>
        {
            for (var i = 0; i < 3000; i++)
            {
                page.Text(10 + (i % 50) * 11, 20 + (i / 50) * 4, "w" + i, size: 3);
            }
        });

        var capped = PdfPages.Read(bytes, new ExtractionOptions { MaxWordsPerPage = 500 });
        var uncapped = PdfPages.Read(bytes);

        capped[0].Words.Should().HaveCount(500);
        capped[0].Capped.Should().BeTrue();
        uncapped[0].Words.Count.Should().BeGreaterThan(2500);
        uncapped[0].Capped.Should().BeFalse();
    }

    [Fact]
    public async Task The_page_report_shows_that_a_pages_content_was_capped()
    {
        var bytes = PdfWithPage(595, 842, page =>
        {
            for (var i = 0; i < 1500; i++)
            {
                page.Text(10 + (i % 50) * 11, 20 + (i / 50) * 4, "w" + i, size: 3);
            }
        });
        var options = new ExtractionOptions { MaxWordsPerPage = 200, DualReadSamplePages = 0, DualReadLowScoreShare = 2 };

        var result = await Pipeline(new TableParsingLlm(), options).RunAsync(bytes, SeedTags);

        result.Pages[0].Reasons.Should().Contain("page_content_capped");
    }

    // ---------- finding 13: spend cap ----------

    [Fact]
    public async Task An_extraction_that_spends_its_call_budget_ends_partial_with_a_clear_code_and_stops_calling_the_model()
    {
        var llm = new TableParsingLlm();
        var options = new ExtractionOptions { MaxConcurrentPages = 1, DualReadSamplePages = 0, DualReadLowScoreShare = 2, MaxLlmCallsPerExtraction = 1 };

        var result = await Pipeline(llm, options).RunAsync(Fx.Pdf("b-multipage-merged"), SeedTags);

        result.Status.Should().Be(ExtractionStatus.Partial);
        result.ErrorCode.Should().Be("extraction_budget_exceeded");
        result.ErrorMessage.Should().Contain("budget");
        llm.Calls.Should().Be(1);
        result.Rows.Should().NotBeEmpty("the page read before the budget ran out is kept");
        result.Pages.Count(p => p.Status == "done").Should().Be(1);
        result.Pages.Where(p => p.Status == "failed").Should().OnlyContain(p => p.ErrorCode == "extraction_budget_exceeded");
    }

    [Fact]
    public async Task The_token_budget_stops_the_extraction_the_same_way()
    {
        var llm = new TableParsingLlm();
        var options = new ExtractionOptions { MaxConcurrentPages = 1, DualReadSamplePages = 0, DualReadLowScoreShare = 2, MaxLlmCallsPerExtraction = 0, MaxTokensPerExtraction = 1 };

        var result = await Pipeline(llm, options).RunAsync(Fx.Pdf("b-multipage-merged"), SeedTags);

        result.ErrorCode.Should().Be("extraction_budget_exceeded");
        llm.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_default_budget_is_generous_for_a_normal_document()
    {
        var result = await Pipeline(new TableParsingLlm(), new ExtractionOptions { DualReadSamplePages = 0, DualReadLowScoreShare = 2 }).RunAsync(Fx.Pdf("b-multipage-merged"), SeedTags);

        result.Status.Should().Be(ExtractionStatus.Completed);
        new ExtractionOptions().MaxLlmCallsPerExtraction.Should().Be(200);
    }

    // ---------- finding 13: delete a document ----------

    private sealed class BlockingLlm(ILlmClient inner) : ILlmClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Provider => inner.Provider;

        public string DefaultModel => inner.DefaultModel;

        public int DefaultMaxOutputTokens => inner.DefaultMaxOutputTokens;

        public int MaxOutputTokensCap => inner.MaxOutputTokensCap;

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.GenerateAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Removing_a_document_deletes_its_file_extractions_and_rows_and_leaves_the_change_sets()
    {
        using var host = HostFor("a-clean-table");
        var (ids, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var accepted = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, extraction.Rows.Take(3).Select(r => r.Id).ToList(), ExtractionHost.Engineer, CancellationToken.None));
        Directory.GetFiles(host.StoreDir).Should().ContainSingle();

        await host.WithServiceAsync(async s =>
        {
            await s.RemoveDocumentAsync(ids.DocumentId, CancellationToken.None);
            return 0;
        });

        Directory.GetFiles(host.StoreDir).Should().BeEmpty();
        (await host.WithDbAsync(db => db.SourceDocuments.CountAsync())).Should().Be(0);
        (await host.WithDbAsync(db => db.Extractions.CountAsync())).Should().Be(0);
        (await host.WithDbAsync(db => db.ExtractedRows.CountAsync())).Should().Be(0);
        (await host.WithDbAsync(db => db.ChangeSets.CountAsync(c => c.Id == accepted.ChangeSets[0].Id))).Should().Be(1, "the draft is the project's record");
        (await Refused(() => host.WithServiceAsync(s => s.GetAsync(extraction.Id, CancellationToken.None)))).Kind.Should().Be(ErrorKind.NotFound);
        (await Refused(() => host.WithServiceAsync(async s =>
        {
            await s.RemoveDocumentAsync(ids.DocumentId, CancellationToken.None);
            return 0;
        }))).Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_document_cannot_be_removed_while_an_extraction_of_it_is_running()
    {
        BlockingLlm? blocking = null;
        using var host = HostFor("a-clean-table", wrap: inner => blocking = new BlockingLlm(inner));
        await using var scope = host.Scope();
        using var stream = new MemoryStream(Fx.Pdf("a-clean-table"));
        var ids = await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, stream, "busy.pdf", ExtractionHost.Engineer, CancellationToken.None);
        await blocking!.Started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var refused = await Refused(() => host.WithServiceAsync(async s =>
        {
            await s.RemoveDocumentAsync(ids.DocumentId, CancellationToken.None);
            return 0;
        }));

        refused.Code.Should().Be("extraction_already_running");
        refused.Kind.Should().Be(ErrorKind.Conflict);
        Directory.GetFiles(host.StoreDir).Should().ContainSingle("nothing was removed");

        blocking.Release.SetResult();
        await host.Get<ExtractionRunManager>().Completion(ids.ExtractionId)!.WaitAsync(TimeSpan.FromSeconds(60));
        await host.WithServiceAsync(async s =>
        {
            await s.RemoveDocumentAsync(ids.DocumentId, CancellationToken.None);
            return 0;
        });
        Directory.GetFiles(host.StoreDir).Should().BeEmpty();
    }

    // ---------- finding 3: concurrency ----------

    [Fact]
    public async Task Two_simultaneous_accepts_of_the_same_rows_succeed_exactly_once_and_the_other_gets_409()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var ids = extraction.Rows.Take(10).Select(r => r.Id).ToList();

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Attempt(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, ids, ExtractionHost.Engineer, CancellationToken.None)))));

        results.Count(r => r.Ok).Should().Be(1);
        var loser = results.Single(r => !r.Ok);
        loser.Error.Should().BeOfType<AppException>("a lost race is a handled conflict, never a 500");
        ((AppException)loser.Error!).Code.Should().Be("rows_already_decided");
        ((AppException)loser.Error).Kind.Should().Be(ErrorKind.Conflict);
        (await host.WithDbAsync(db => db.ChangeSets.CountAsync(c => c.Source == ChangeSetSource.Extraction))).Should().Be(1, "only the winner created a change set");
        (await host.GetAsync(extraction.Id)).Summary.Accepted.Should().Be(10);
    }

    [Fact]
    public async Task Two_simultaneous_accepts_of_everything_never_end_in_an_unhandled_error()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Attempt(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None)))));

        results.Count(r => r.Ok).Should().Be(1);
        results.Where(r => !r.Ok).Should().OnlyContain(r => r.Error is AppException, "no DbUpdateConcurrencyException or SQLite busy error escapes");
        results.Where(r => !r.Ok).Select(r => ((AppException)r.Error!).Code).Should().OnlyContain(c => c == "no_rows_to_accept" || c == "rows_already_decided");
        (await host.WithDbAsync(db => db.ChangeSets.CountAsync(c => c.Source == ChangeSetSource.Extraction))).Should().Be(results.Single(r => r.Ok).Accepted!.ChangeSets.Count);
    }

    [Fact]
    public async Task A_row_edit_racing_an_accept_is_either_in_the_draft_or_refused_never_lost()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        for (var round = 0; round < 5; round++)
        {
            var row = extraction.Rows[round * 2];
            var other = extraction.Rows[round * 2 + 1];
            var description = $"Edited in round {round}";
            var accept = Attempt(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [row.Id, other.Id], ExtractionHost.Engineer, CancellationToken.None)));
            var patch = Attempt(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, row.Id, new PatchRowBody(Json((Fields.Description, description)), null), CancellationToken.None)));
            await Task.WhenAll(accept, patch);

            (await accept).Ok.Should().BeTrue("the accept does not fail because a row was edited");
            var patchResult = await patch;
            if (!patchResult.Ok)
            {
                patchResult.Error.Should().BeOfType<AppException>();
                ((AppException)patchResult.Error!).Code.Should().Be("row_already_accepted");
            }

            // Whatever the order, the change set holds exactly what the row said when it was accepted.
            var changeSetId = (await host.GetAsync(extraction.Id)).Rows.Single(r => r.Id == row.Id).ChangeSetId!.Value;
            await using var scope = host.Scope();
            var dto = await scope.ServiceProvider.GetRequiredService<ChangeSetService>().GetAsync(changeSetId, CancellationToken.None);
            var created = dto.Ops.OfType<CreateTagOp>().Single(o => o.TagName == row.Fields[Fields.TagName].Value!.ToString());
            created.Description.Should().Be(patchResult.Ok ? description : row.Fields[Fields.Description].Value!.ToString());
        }
    }

    [Fact]
    public async Task Two_simultaneous_extract_again_calls_queue_one_run_and_refuse_the_other()
    {
        using var host = HostFor("a-clean-table");
        var (ids, _) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Attempt(() => host.WithServiceAsync(s => s.ExtractAgainAsync(ids.DocumentId, ExtractionHost.Engineer, CancellationToken.None)))));

        results.Count(r => r.Ok).Should().Be(1);
        ((AppException)results.Single(r => !r.Ok).Error!).Code.Should().Be("extraction_already_running");
        var started = results.Single(r => r.Ok).Uploaded!;
        await host.Get<ExtractionRunManager>().Completion(started.ExtractionId)!.WaitAsync(TimeSpan.FromSeconds(60));
        (await host.WithDbAsync(db => db.Extractions.CountAsync(e => e.SourceDocumentId == ids.DocumentId))).Should().Be(2, "the first run and exactly one more");
    }

    private sealed record Attempted(bool Ok, Exception? Error, AcceptResultDto? Accepted = null, UploadResultDto? Uploaded = null);

    private static async Task<Attempted> Attempt(Func<Task<AcceptResultDto>> action)
    {
        try
        {
            return new Attempted(true, null, await action());
        }
        catch (Exception ex)
        {
            return new Attempted(false, ex);
        }
    }

    private static async Task<Attempted> Attempt(Func<Task<UploadResultDto>> action)
    {
        try
        {
            return new Attempted(true, null, Uploaded: await action());
        }
        catch (Exception ex)
        {
            return new Attempted(false, ex);
        }
    }

    private static async Task<Attempted> Attempt(Func<Task<PatchRowResultDto>> action)
    {
        try
        {
            await action();
            return new Attempted(true, null);
        }
        catch (Exception ex)
        {
            return new Attempted(false, ex);
        }
    }

    // ---------- finding 5: suspected_injection rows need an explicit override ----------

    [Fact]
    public async Task A_row_flagged_as_injection_is_not_accepted_without_an_explicit_override_by_id()
    {
        using var host = HostFor("f-injection");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("f-injection"));
        var flagged = extraction.Rows.Where(r => r.Flags.Contains("suspected_injection") && r.Band != "must_fix").ToList();
        flagged.Should().NotBeEmpty("the fixture carries a hostile description");
        var clean = extraction.Rows.First(r => !r.Flags.Contains("suspected_injection") && r.Band != "must_fix");

        // choosing it explicitly is not enough
        var refused = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [flagged[0].Id], ExtractionHost.Engineer, CancellationToken.None)));
        refused.Code.Should().Be("flagged_rows_need_override");
        refused.Kind.Should().Be(ErrorKind.Unprocessable);
        refused.Extensions["rowIds"].Should().BeEquivalentTo(new List<int> { flagged[0].Id });

        // an override for some OTHER row does not cover it
        var wrong = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [flagged[0].Id], ExtractionHost.Engineer, CancellationToken.None, [clean.Id])));
        wrong.Code.Should().Be("flagged_rows_need_override");

        // default accept (all open rows) is refused as well while a flagged row is among them
        var all = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, extraction.Rows.Where(r => r.Band != "must_fix").Select(r => r.Id).ToList(), ExtractionHost.Engineer, CancellationToken.None)));
        all.Code.Should().Be("flagged_rows_need_override");
        (await host.WithDbAsync(db => db.ChangeSets.CountAsync(c => c.Source == ChangeSetSource.Extraction))).Should().Be(0, "nothing was created by the refused attempts");

        // with the id listed it goes through, and only then
        var accepted = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [flagged[0].Id], ExtractionHost.Engineer, CancellationToken.None, [flagged[0].Id]));
        accepted.AcceptedRowIds.Should().Equal(flagged[0].Id);
    }

    [Fact]
    public async Task Unflagged_rows_are_accepted_without_any_override()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var accepted = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None));

        accepted.AcceptedRowIds.Should().HaveCount(50);
    }

    // ---------- finding 10: grounding on number boundaries ----------

    private static GroundingIndex IndexOf(params string[] words) =>
        GroundingIndex.Build(Synthetic.Page([.. words.Select((w, i) => new PageWord(w, 10 + i * 40, 100, 10 + i * 40 + 30, 108))]));

    [Theory]
    [InlineData("0")]
    [InlineData("5")]
    [InlineData("95")]
    [InlineData("12")]
    [InlineData("1.2")]
    public void A_number_is_not_supported_by_digits_inside_other_numbers(string value)
    {
        var index = IndexOf("ME-EXH-TT01", "1500", "950", "125", "31.25");

        index.Supports(Fields.AlarmH, value).Should().BeFalse($"'{value}' is not a number on this page");
    }

    [Theory]
    [InlineData("1500")]
    [InlineData("950")]
    [InlineData("31.25")]
    public void A_whole_number_on_the_page_supports_the_value(string value) =>
        IndexOf("1500", "950", "31.25").Supports(Fields.AlarmH, value).Should().BeTrue();

    [Fact]
    public void Numbers_next_to_units_dashes_and_brackets_are_still_found()
    {
        var index = IndexOf("95°C", "(0-600)", "-20", "4-20mA", "5%");

        index.Supports(Fields.AlarmH, "95").Should().BeTrue("95°C");
        index.Supports(Fields.RangeLow, "0").Should().BeTrue("a combined range 0-600");
        index.Supports(Fields.RangeHigh, "600").Should().BeTrue("a combined range 0-600");
        index.Supports(Fields.AlarmL, "-20").Should().BeTrue();
        index.Supports(Fields.AlarmL, "20").Should().BeTrue("4-20mA holds a 20 after a range dash");
        index.Supports(Fields.AlarmLl, "5").Should().BeTrue("5%");
    }

    [Fact]
    public void A_sign_is_part_of_the_number_and_a_tag_name_is_not_a_number()
    {
        var index = IndexOf("-5", "ME-EXH-TT95");

        index.Supports(Fields.AlarmH, "5").Should().BeFalse("the page says -5");
        index.Supports(Fields.AlarmH, "95").Should().BeFalse("95 occurs only inside a tag name");
        index.Supports(Fields.AlarmH, "-5").Should().BeTrue();
        index.Supports(Fields.TagName, "ME-EXH-TT95").Should().BeTrue("non-numeric fields keep the substring rule");
    }
}
