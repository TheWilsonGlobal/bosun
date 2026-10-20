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

namespace Bosun.Extraction.Tests;

/// <summary>Upload, background run, persistence, human edit and accept against a real SQLite file and a real document store.</summary>
public sealed class ServiceTests
{
    private static async Task<AppException> Refused(Func<Task> action)
    {
        var assertion = await action.Should().ThrowAsync<AppException>();
        return assertion.Which;
    }

    private static ExtractionHost HostFor(string fixture, Dictionary<string, string?>? config = null) =>
        new(new TableParsingLlm(page => TruthReader.ReadRowsOf(Fx.Truth(fixture).Rows.Where(r => r.Page == page))), config);

    // ---------- upload + run + persistence ----------

    [Fact]
    public async Task An_upload_is_stored_under_a_generated_name_and_extracted_in_the_background()
    {
        using var host = HostFor("a-clean-table");
        var pdf = Fx.Pdf("a-clean-table");

        var (ids, extraction) = await host.UploadAndRunAsync(pdf, "My IO list (final).pdf");

        extraction.Status.Should().Be(ExtractionStatus.Completed);
        extraction.Document.DisplayName.Should().Be("My IO list (final).pdf");
        extraction.Document.PageCount.Should().Be(2);
        extraction.Document.SizeBytes.Should().Be(pdf.Length);
        extraction.Rows.Should().HaveCount(50);
        extraction.Progress.PagesDone.Should().Be(2);
        extraction.Progress.PagesTotal.Should().Be(2);
        extraction.Progress.Phase.Should().BeNull();
        extraction.Model.Should().Be("offline-table-parser");
        extraction.PromptVersion.Should().StartWith("extract.system@");
        extraction.Tokens.LlmCalls.Should().Be(2);
        extraction.Pages.Should().HaveCount(2).And.OnlyContain(p => p.Path == "text" && p.Status == "done");
        extraction.Bands.AutoAtLeast.Should().Be(0.9);
        extraction.Bands.ReviewAtLeast.Should().Be(0.6);
        extraction.Summary.Should().BeEquivalentTo(new ExtractionSummaryDto(50, 50, 0, 0, 0, 0, 0, 0, 0));
        ids.DocumentId.Should().BeGreaterThan(0);

        var stored = Directory.GetFiles(host.StoreDir);
        stored.Should().ContainSingle();
        Path.GetFileName(stored[0]).Should().MatchRegex(@"^[0-9a-f]{32}\.pdf$");
        File.ReadAllBytes(stored[0]).Should().Equal(pdf);

        var document = await host.WithDbAsync(db => db.SourceDocuments.AsNoTracking().SingleAsync());
        document.StoredName.Should().Be(Path.GetFileName(stored[0]));
        document.Sha256.Should().HaveLength(64);
        document.UploadedBy.Should().Be("alice");
    }

    [Fact]
    public async Task Rows_carry_the_source_page_snippet_position_and_per_field_assessment()
    {
        using var host = HostFor("a-clean-table");

        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var row = extraction.Rows[0];
        row.Seq.Should().Be(1);
        row.Page.Should().Be(1);
        row.SourcePath.Should().Be("text");
        row.SourceRef.Should().Be("p1:L4");
        row.Snippet.Should().StartWith("ME-SCR1-TT01 | SCR catalyst bed 1 temperature | AI | °C | 0 | 500");
        row.Bbox.Should().HaveCount(4).And.OnlyContain(v => v >= 0 && v <= 1);
        row.Bbox![0].Should().BeLessThan(row.Bbox[2]);
        row.Bbox[1].Should().BeLessThan(row.Bbox[3]);
        row.State.Should().Be(ExtractedRowState.Pending);
        row.Band.Should().Be("auto");
        row.Fields.Keys.Should().Equal(Fields.All);
        var tag = row.Fields[Fields.TagName];
        tag.Value.Should().Be("ME-SCR1-TT01");
        tag.Raw.Should().Be("ME-SCR1-TT01");
        tag.Confidence.Should().Be(0.95);
        tag.Band.Should().Be("auto");
        tag.Source.Should().Be("text");
        row.Fields[Fields.RangeHigh].Value.Should().Be(500.0);
        row.Fields[Fields.AlarmL].Value.Should().BeNull();
        row.Fields[Fields.AlarmL].Confidence.Should().BeNull();
    }

    [Fact]
    public async Task The_document_list_shows_the_latest_extraction_with_its_counts()
    {
        using var host = HostFor("a-clean-table");
        await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"), "first.pdf");
        await host.UploadAndRunAsync(Fx.Pdf("f-injection"), "second.pdf");

        var documents = await host.WithServiceAsync(s => s.ListDocumentsAsync(1, CancellationToken.None));

        documents.Select(d => d.DisplayName).Should().Equal("second.pdf", "first.pdf");
        documents[1].LatestExtraction!.Status.Should().Be(ExtractionStatus.Completed);
        documents[1].LatestExtraction!.Summary.Rows.Should().Be(50);
        documents[0].LatestExtraction!.Summary.Rows.Should().Be(22);
    }

    [Fact]
    public async Task An_unreadable_provider_result_is_recorded_on_the_extraction_with_the_specific_code()
    {
        var failing = new FailingClient(new LlmException(LlmErrorCodes.AuthFailed, "The API key was rejected (HTTP 400).", 400));
        using var host = new ExtractionHost(failing);

        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        extraction.Status.Should().Be(ExtractionStatus.Failed);
        extraction.ErrorCode.Should().Be("llm_auth_failed");
        extraction.ErrorMessage.Should().Contain("API key");
        extraction.Rows.Should().BeEmpty();
        extraction.Pages.Should().OnlyContain(p => p.Status == "failed");
        extraction.FinishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_partial_extraction_keeps_the_rows_of_the_pages_that_worked_and_can_still_be_edited_and_accepted()
    {
        var truth = Fx.Truth("b-multipage-merged");
        var inner = new TableParsingLlm();
        using var host = new ExtractionHost(new FailingClient(inner, page: 2));

        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("b-multipage-merged"));

        extraction.Status.Should().Be(ExtractionStatus.Partial);
        extraction.ErrorCode.Should().Be("llm_unavailable");
        extraction.Rows.Should().HaveCount(truth.Rows.Count - 30);
        extraction.Pages[1].Status.Should().Be("failed");
        extraction.Pages[1].ErrorCode.Should().Be("llm_unavailable");

        var accepted = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, extraction.Rows.Take(5).Select(r => r.Id).ToList(), ExtractionHost.Engineer, CancellationToken.None));
        accepted.ChangeSets.Should().NotBeEmpty();
    }

    private sealed class FailingClient(ILlmClient inner, int page = 0) : ILlmClient
    {
        private readonly LlmException? always;

        public FailingClient(LlmException always)
            : this(new TableParsingLlm())
        {
            this.always = always;
        }

        public string Provider => inner.Provider;

        public string DefaultModel => inner.DefaultModel;

        public int DefaultMaxOutputTokens => inner.DefaultMaxOutputTokens;

        public int MaxOutputTokensCap => inner.MaxOutputTokensCap;

        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            if (always is not null)
            {
                throw always;
            }

            var text = string.Concat(request.Messages[0].Parts.OfType<TextPart>().Select(p => p.Text));
            if (text.StartsWith($"Page {page} of", StringComparison.Ordinal))
            {
                throw new LlmException(LlmErrorCodes.Unavailable, "The provider is down.", 503);
            }

            return inner.GenerateAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task A_document_can_be_read_again_without_uploading_it_twice_and_not_while_it_is_being_read()
    {
        using var host = HostFor("a-clean-table");
        var (ids, first) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var again = await host.WithServiceAsync(s => s.ExtractAgainAsync(ids.DocumentId, ExtractionHost.Engineer, CancellationToken.None));
        await host.Get<ExtractionRunManager>().Completion(again.ExtractionId)!.WaitAsync(TimeSpan.FromSeconds(60));
        var second = await host.GetAsync(again.ExtractionId);

        again.DocumentId.Should().Be(ids.DocumentId);
        again.ExtractionId.Should().NotBe(ids.ExtractionId);
        second.Status.Should().Be(ExtractionStatus.Completed);
        second.Rows.Should().HaveCount(50);
        (await host.GetAsync(first.Id)).Rows.Should().HaveCount(50, "the earlier extraction and its rows are untouched");
        Directory.GetFiles(host.StoreDir).Should().ContainSingle("the file is stored once");
        var documents = await host.WithServiceAsync(s => s.ListDocumentsAsync(1, CancellationToken.None));
        documents.Should().ContainSingle().Which.LatestExtraction!.Id.Should().Be(again.ExtractionId);

        await host.WithDbAsync(async db =>
        {
            var latest = await db.Extractions.SingleAsync(e => e.Id == again.ExtractionId);
            latest.Status = ExtractionStatus.Running;
            await db.SaveChangesAsync();
            return 0;
        });
        var busy = await Refused(() => host.WithServiceAsync(s => s.ExtractAgainAsync(ids.DocumentId, ExtractionHost.Engineer, CancellationToken.None)));
        busy.Code.Should().Be("extraction_already_running");
        busy.Kind.Should().Be(ErrorKind.Conflict);
        (await Refused(() => host.WithServiceAsync(s => s.ExtractAgainAsync(99_999, ExtractionHost.Engineer, CancellationToken.None)))).Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Extractions_cut_off_by_a_restart_are_marked_failed_on_recovery()
    {
        using var host = new ExtractionHost();
        int id;
        await using (var scope = host.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Bosun.Infrastructure.Persistence.BosunDbContext>();
            var document = new SourceDocument { ProjectId = 1, DisplayName = "x.pdf", StoredName = new string('a', 32) + ".pdf", Sha256 = new string('0', 64), UploadedBy = "alice", UploadedAt = DateTime.UtcNow, PageCount = 1 };
            var running = new Bosun.Domain.Entities.Extraction { ProjectId = 1, SourceDocument = document, Status = ExtractionStatus.Running, CreatedBy = "alice", CreatedAt = DateTime.UtcNow };
            db.Extractions.Add(running);
            await db.SaveChangesAsync();
            id = running.Id;
        }

        await host.Get<ExtractionRunManager>().RecoverOrphansAsync(CancellationToken.None);

        var extraction = await host.GetAsync(id);
        extraction.Status.Should().Be(ExtractionStatus.Failed);
        extraction.ErrorCode.Should().Be("server_restarted");
    }

    // ---------- upload hardening ----------

    [Fact]
    public async Task A_file_that_is_not_a_pdf_is_refused_by_its_bytes_whatever_it_is_called()
    {
        using var host = new ExtractionHost();
        foreach (var bytes in new[] { "PK\u0003\u0004 a zip"u8.ToArray(), "hello world"u8.ToArray(), "<html></html>"u8.ToArray(), [0x7F, (byte)'E', (byte)'L', (byte)'F'], " %PDF-1.4"u8.ToArray() })
        {
            var refused = await Refused(() => host.UploadAndRunAsync(bytes, "totally-a.pdf"));

            refused.Code.Should().Be("not_a_pdf");
            refused.Kind.Should().Be(ErrorKind.UnsupportedMedia);
        }

        (await Refused(() => host.UploadAndRunAsync([], "empty.pdf"))).Code.Should().Be("no_file");
        Directory.Exists(host.StoreDir).Should().BeFalse("nothing was stored for a refused file");
        (await host.WithDbAsync(db => db.SourceDocuments.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_file_that_starts_like_a_pdf_but_is_not_one_is_refused_and_not_kept()
    {
        using var host = new ExtractionHost();

        var refused = await Refused(() => host.UploadAndRunAsync("%PDF-1.7\nthis is garbage, not a document"u8.ToArray()));

        refused.Code.Should().Be("pdf_unreadable");
        refused.Kind.Should().Be(ErrorKind.Unprocessable);
        (Directory.Exists(host.StoreDir) ? Directory.GetFiles(host.StoreDir) : []).Should().BeEmpty();
        (await host.WithDbAsync(db => db.Extractions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task An_oversize_file_is_refused_while_it_is_read_not_after()
    {
        using var host = new ExtractionHost(null, new() { ["Extraction:MaxUploadBytes"] = "20000" });
        var pdf = Fx.Pdf("a-clean-table");
        pdf.Length.Should().BeGreaterThan(20_000);

        var refused = await Refused(() => host.UploadAndRunAsync(pdf));

        refused.Code.Should().Be("document_too_large");
        refused.Kind.Should().Be(ErrorKind.PayloadTooLarge);
        refused.Extensions["maxBytes"].Should().Be(20000L);
        (await host.WithDbAsync(db => db.SourceDocuments.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task The_declared_size_is_not_trusted_a_stream_that_lies_about_its_length_is_still_capped()
    {
        using var host = new ExtractionHost(null, new() { ["Extraction:MaxUploadBytes"] = "20000" });

        var refused = await Refused(async () =>
        {
            await using var scope = host.Scope();
            await scope.ServiceProvider.GetRequiredService<ExtractionService>().UploadAsync(1, new EndlessStream(), "big.pdf", ExtractionHost.Engineer, CancellationToken.None);
        });

        refused.Code.Should().Be("document_too_large");
    }

    private sealed class EndlessStream : Stream
    {
        private long position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 10; // a lie

        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            "%PDF-1.4"u8.CopyTo(buffer.AsSpan(offset, Math.Min(count, 8)));
            position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("NUL.pdf")]
    [InlineData("CON.pdf")]
    [InlineData("con")]
    [InlineData("aux.pdf")]
    [InlineData("PRN.PDF")]
    [InlineData("COM1.pdf")]
    [InlineData("LPT1.pdf")]
    [InlineData("nul.")]
    [InlineData("..\\..\\..\\evil.pdf")]
    [InlineData("../../../etc/passwd")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts.pdf")]
    [InlineData("a:b*c?d<e>f|g.pdf")]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Windows_device_names_and_path_tricks_in_the_client_file_name_cannot_break_or_escape_the_upload(string clientName)
    {
        using var host = HostFor("f-injection");
        var pdf = Fx.Pdf("f-injection");

        var (_, extraction) = await host.UploadAndRunAsync(pdf, clientName);

        extraction.Status.Should().Be(ExtractionStatus.Completed);
        var stored = Directory.GetFiles(host.StoreDir);
        stored.Should().ContainSingle("the bytes were written exactly once, under a generated name");
        Path.GetFileName(stored[0]).Should().MatchRegex(@"^[0-9a-f]{32}\.pdf$");
        new FileInfo(stored[0]).Length.Should().Be(pdf.Length, "a device name would have swallowed the bytes");
        extraction.Document.DisplayName.Should().NotBeEmpty().And.NotContainAny("\\", "/", ":", "*", "?", "<", ">", "|", "\"");
        Directory.GetDirectories(host.StoreDir).Should().BeEmpty();
    }

    [Fact]
    public void Display_names_are_sanitised_bounded_and_never_empty()
    {
        ExtractionService.SafeDisplayName("..\\..\\x.pdf").Should().Be("x.pdf");
        ExtractionService.SafeDisplayName("a/b/c.pdf").Should().Be("c.pdf");
        ExtractionService.SafeDisplayName("NUL.pdf").Should().Be("NUL.pdf", "it is only a label: the stored name is generated");
        ExtractionService.SafeDisplayName("bad\u0001name\u0007.pdf").Should().Be("badname.pdf");
        ExtractionService.SafeDisplayName(null).Should().Be("document.pdf");
        ExtractionService.SafeDisplayName("   ").Should().Be("document.pdf");
        ExtractionService.SafeDisplayName(new string('x', 500) + ".pdf").Length.Should().BeLessThanOrEqualTo(120);
        ExtractionService.SafeDisplayName(new string('x', 500) + ".pdf").Should().EndWith(".pdf");
    }

    [Fact]
    public async Task A_stored_name_that_this_class_did_not_generate_never_reaches_the_file_system()
    {
        using var host = new ExtractionHost();
        var store = host.Get<IDocumentStore>();

        foreach (var bad in new[] { "..\\x.pdf", "../x.pdf", "NUL", "con.pdf", new string('a', 32), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.pdf", "a.pdf" })
        {
            var read = async () => await store.ReadAsync(bad, CancellationToken.None);
            await read.Should().ThrowAsync<ArgumentException>(bad);
        }
    }

    // ---------- the page image ----------

    [Fact]
    public async Task The_page_image_is_a_png_of_the_stored_page_and_a_row_image_is_a_crop_of_it()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var full = await host.WithServiceAsync(s => s.GetPageImageAsync(extraction.Id, 1, 100, null, CancellationToken.None));
        var crop = await host.WithServiceAsync(s => s.GetPageImageAsync(extraction.Id, 1, 100, extraction.Rows[10].Id, CancellationToken.None));

        full.Png.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        Width(full.Png).Should().BeInRange(1160, 1172, "A4 landscape at 100 dpi");
        full.Cropped.Should().BeFalse();
        crop.Cropped.Should().BeTrue();
        Height(crop.Png).Should().BeLessThan(Height(full.Png) / 3);
        Width(crop.Png).Should().Be(Width(full.Png));

        var outOfRange = await Refused(() => host.WithServiceAsync(s => s.GetPageImageAsync(extraction.Id, 3, null, null, CancellationToken.None)));
        outOfRange.Code.Should().Be("page_out_of_range");
        (await Refused(() => host.WithServiceAsync(s => s.GetPageImageAsync(extraction.Id, 0, null, null, CancellationToken.None)))).Code.Should().Be("page_out_of_range");
    }

    private static int Width(byte[] png) => (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];

    private static int Height(byte[] png) => (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

    // ---------- accept ----------

    [Fact]
    public async Task Accept_creates_a_DRAFT_change_set_with_extraction_provenance_and_never_approves_or_applies_it()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var tagsBefore = await host.TagCountAsync();

        var result = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None));

        result.ChangeSets.Should().ContainSingle();
        result.AcceptedRowIds.Should().HaveCount(50);
        var changeSet = await host.WithServiceAsync(_ => Task.FromResult(0)); // keep the scope helper honest
        _ = changeSet;
        await using var scope = host.Scope();
        var dto = await scope.ServiceProvider.GetRequiredService<ChangeSetService>().GetAsync(result.ChangeSets[0].Id, CancellationToken.None);
        dto.Source.Should().Be(ChangeSetSource.Extraction);
        dto.Status.Should().Be(ChangeSetStatus.Draft);
        dto.CreatedBy.Should().Be("alice");
        dto.ApprovedBy.Should().BeNull();
        dto.AppliedAt.Should().BeNull();
        dto.AgentRunId.Should().BeNull();
        dto.Model.Should().Be("offline-table-parser");
        dto.PromptVersion.Should().StartWith("extract.system@");
        dto.Title.Should().StartWith("Extraction").And.Contain("list.pdf");
        dto.Ops.OfType<CreateTagOp>().Should().HaveCount(50);
        dto.Ops.OfType<CreateTagOp>().Should().OnlyContain(o => o.SafetyClass == SafetyClass.None);
        dto.Ops.Should().OnlyContain(o => o is CreateTagOp || o is SetAlarmLimitOp);
        dto.Ops.OfType<SetAlarmLimitOp>().Should().OnlyContain(o => o.New != null && o.Old == null);

        (await host.TagCountAsync()).Should().Be(tagsBefore, "accepting writes a draft, not tags");
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync())).Should().Be(0);
        var after = await host.GetAsync(extraction.Id);
        after.Rows.Should().OnlyContain(r => r.State == ExtractedRowState.Accepted && r.ChangeSetId == result.ChangeSets[0].Id);
        after.ChangeSetIds.Should().Equal(result.ChangeSets[0].Id);
        after.Summary.Accepted.Should().Be(50);
    }

    [Fact]
    public async Task An_accepted_draft_goes_through_the_normal_validate_approve_apply_path_by_a_human()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var tagsBefore = await host.TagCountAsync();
        var accepted = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None));
        var id = accepted.ChangeSets[0].Id;

        await using var scope = host.Scope();
        var changeSets = scope.ServiceProvider.GetRequiredService<ChangeSetService>();
        (await changeSets.ValidateAsync(id, ExtractionHost.Engineer, CancellationToken.None)).Valid.Should().BeTrue();
        await changeSets.ApproveAsync(id, ExtractionHost.Engineer, CancellationToken.None);
        var applied = await changeSets.ApplyAsync(id, ExtractionHost.Engineer, CancellationToken.None);

        applied.Status.Should().Be(ChangeSetStatus.Applied);
        (await host.TagCountAsync()).Should().Be(tagsBefore + 50);
        var audit = await host.WithDbAsync(db => db.AuditEntries.AsNoTracking().ToListAsync());
        audit.Should().NotBeEmpty().And.OnlyContain(a => a.Actor == "alice" && a.ActorType == ActorType.User && a.ProposedBy == ActorType.User);
    }

    [Fact]
    public async Task Accept_is_refused_while_must_fix_rows_are_chosen_and_says_which()
    {
        using var host = HostFor("e-customer-io-list");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("e-customer-io-list"));
        extraction.Summary.MustFix.Should().Be(7);
        extraction.Summary.OpenMustFix.Should().Be(7);

        var all = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None)));
        var onlyBad = extraction.Rows.Where(r => r.Band == "must_fix").Select(r => r.Id).ToList();
        var some = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, onlyBad.Take(2).ToList(), ExtractionHost.Engineer, CancellationToken.None)));

        all.Code.Should().Be("unresolved_must_fix");
        all.Kind.Should().Be(ErrorKind.Unprocessable);
        ((IEnumerable<int>)all.Extensions["rowIds"]!).Should().BeEquivalentTo(onlyBad);
        all.Extensions["count"].Should().Be(7);
        some.Code.Should().Be("unresolved_must_fix");
        (await host.WithDbAsync(db => db.ChangeSets.CountAsync())).Should().Be(0, "nothing was created");
        (await host.GetAsync(extraction.Id)).Rows.Should().OnlyContain(r => r.State == ExtractedRowState.Pending);
    }

    [Fact]
    public async Task The_good_rows_can_be_accepted_while_the_bad_ones_wait_and_then_the_rest_after_the_fixes()
    {
        using var host = HostFor("e-customer-io-list");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("e-customer-io-list"));
        var good = extraction.Rows.Where(r => r.Band != "must_fix").Select(r => r.Id).ToList();

        var first = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, good, ExtractionHost.Engineer, CancellationToken.None));

        first.AcceptedRowIds.Should().HaveCount(83);
        var middle = await host.GetAsync(extraction.Id);
        middle.Summary.Accepted.Should().Be(83);
        middle.Summary.OpenMustFix.Should().Be(7);

        // excluding the seven bad rows leaves nothing open
        foreach (var row in middle.Rows.Where(r => r.State == ExtractedRowState.Pending))
        {
            await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, row.Id, new PatchRowBody(null, true), CancellationToken.None));
        }

        var again = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None)));
        again.Code.Should().Be("no_rows_to_accept");
    }

    [Fact]
    public async Task Chosen_rows_that_are_already_accepted_or_foreign_are_refused_with_their_ids()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var ids = extraction.Rows.Take(3).Select(r => r.Id).ToList();
        await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, ids, ExtractionHost.Engineer, CancellationToken.None));

        var decided = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, ids, ExtractionHost.Engineer, CancellationToken.None)));
        var foreign = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [999_999], ExtractionHost.Engineer, CancellationToken.None)));

        decided.Code.Should().Be("rows_already_decided");
        decided.Kind.Should().Be(ErrorKind.Conflict);
        foreign.Code.Should().Be("unknown_rows");
        (await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(424242, null, ExtractionHost.Engineer, CancellationToken.None)))).Code.Should().Be("not_found");
    }

    [Fact]
    public async Task A_big_extraction_is_split_into_change_sets_of_at_most_200_operations_without_splitting_a_row()
    {
        using var host = HostFor("b-multipage-merged");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("b-multipage-merged"));
        extraction.Summary.Auto.Should().Be(120);

        var result = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None));

        result.ChangeSets.Count.Should().BeGreaterThan(1);
        result.ChangeSets.Should().OnlyContain(c => c.OpCount <= 200);
        result.ChangeSets.SelectMany(c => c.RowIds).Should().BeEquivalentTo(extraction.Rows.Select(r => r.Id));
        result.ChangeSets.Select(c => c.Title).Should().OnlyContain(t => t.Contains("part ", StringComparison.Ordinal));
        await using var scope = host.Scope();
        var service = scope.ServiceProvider.GetRequiredService<ChangeSetService>();
        foreach (var part in result.ChangeSets)
        {
            var dto = await service.GetAsync(part.Id, CancellationToken.None);
            var tags = dto.Ops.OfType<CreateTagOp>().Select(o => o.TagName).ToHashSet();
            dto.Ops.Should().OnlyContain(o => tags.Contains(o.TagName), "every limit op sits in the change set of its own createTag");
            dto.Status.Should().Be(ChangeSetStatus.Draft);
            (await service.CheckAsync(part.Id, CancellationToken.None)).Valid.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Two_chosen_rows_with_the_same_tag_cannot_go_into_one_change_set()
    {
        var truth = Fx.Truth("a-clean-table");
        var llm = new TableParsingLlm();
        using var host = new ExtractionHost(llm);
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var first = extraction.Rows[0];
        var second = extraction.Rows[1];
        await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, second.Id, new PatchRowBody(Json(("tagName", first.Fields[Fields.TagName].Value)), null), CancellationToken.None));
        // make the repeat identical so it is only a warning (review), not must-fix: copy every field
        foreach (var field in Fields.All.Where(f => f != Fields.TagName))
        {
            await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, second.Id, new PatchRowBody(Json((field, first.Fields[field].Value)), null), CancellationToken.None));
        }

        var refused = await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [first.Id, second.Id], ExtractionHost.Engineer, CancellationToken.None)));

        refused.Code.Should().Be("duplicate_tags_chosen");
        _ = truth;
    }

    // ---------- the human edit ----------

    private static Dictionary<string, JsonElement> Json(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(f.Value));

    [Fact]
    public async Task Fixing_a_must_fix_row_revalidates_rescores_and_marks_it_human_corrected()
    {
        using var host = HostFor("e-customer-io-list");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("e-customer-io-list"));
        var noRange = extraction.Rows.Single(r => r.Violations.Any(v => v.Code == "RANGE_MISSING"));
        noRange.Band.Should().Be("must_fix");

        var result = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, noRange.Id,
            new PatchRowBody(Json((Fields.RangeLow, 0), (Fields.RangeHigh, "1000")), null), CancellationToken.None));

        result.Row.HumanCorrected.Should().BeTrue();
        result.Row.Violations.Should().BeEmpty();
        result.Row.Band.Should().Be("auto");
        result.Row.Fields[Fields.RangeHigh].Value.Should().Be(1000.0);
        result.Row.Fields[Fields.RangeHigh].Source.Should().Be("human");
        result.Row.Fields[Fields.RangeHigh].Flags.Should().Contain("human_edited");
        result.Row.Fields[Fields.RangeHigh].Confidence.Should().Be(0.99, "a human value is full confidence, capped at the ceiling");
        result.Row.Fields[Fields.TagName].Source.Should().Be("text", "untouched fields keep their origin");
        result.Summary.MustFix.Should().Be(6);
        result.Summary.HumanCorrected.Should().Be(1);

        var stored = (await host.GetAsync(extraction.Id)).Rows.Single(r => r.Id == noRange.Id);
        stored.HumanCorrected.Should().BeTrue();
        stored.Version.Should().BeGreaterThan(noRange.Version);
    }

    [Fact]
    public async Task A_correction_that_still_breaks_a_rule_stays_must_fix()
    {
        using var host = HostFor("e-customer-io-list");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("e-customer-io-list"));
        var noRange = extraction.Rows.Single(r => r.Violations.Any(v => v.Code == "RANGE_MISSING"));

        var result = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, noRange.Id,
            new PatchRowBody(Json((Fields.RangeLow, 100), (Fields.RangeHigh, 50)), null), CancellationToken.None));

        result.Row.Violations.Should().Contain(v => v.Code == "RANGE_INVALID");
        result.Row.Band.Should().Be("must_fix");
        result.Row.HumanCorrected.Should().BeTrue();
    }

    [Fact]
    public async Task Renaming_a_row_that_collides_with_a_tag_of_the_project_clears_the_collision()
    {
        using var host = HostFor("e-customer-io-list");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("e-customer-io-list"));
        var collision = extraction.Rows.Single(r => r.Violations.Any(v => v.Code == "DUPLICATE_IN_PROJECT"));

        var result = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, collision.Id,
            new PatchRowBody(Json((Fields.TagName, "ME-EXH-TT90")), null), CancellationToken.None));

        result.Row.Violations.Should().BeEmpty();
        result.Row.Fields[Fields.TagName].Value.Should().Be("ME-EXH-TT90");
        result.Row.Band.Should().Be("auto");
    }

    [Fact]
    public async Task Editing_one_row_into_a_duplicate_flags_both_and_editing_it_back_clears_both()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var first = extraction.Rows[0];
        var second = extraction.Rows[1];
        var originalName = (string)second.Fields[Fields.TagName].Value!;

        var duplicated = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, second.Id,
            new PatchRowBody(Json((Fields.TagName, first.Fields[Fields.TagName].Value)), null), CancellationToken.None));

        duplicated.Row.Violations.Should().Contain(v => v.Code == "DUPLICATE_IN_DOCUMENT");
        duplicated.AlsoChanged.Should().ContainSingle(r => r.Id == first.Id).Which.Band.Should().Be("must_fix", "the other row with that tag is judged again too");

        var restored = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, second.Id,
            new PatchRowBody(Json((Fields.TagName, originalName)), null), CancellationToken.None));

        restored.Row.Violations.Should().BeEmpty();
        restored.AlsoChanged.Should().ContainSingle(r => r.Id == first.Id).Which.Band.Should().Be("auto");
        restored.Summary.MustFix.Should().Be(0);
    }

    [Fact]
    public async Task Removing_the_hostile_text_from_a_description_clears_the_injection_flag_on_the_row()
    {
        using var host = HostFor("f-injection");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("f-injection"));
        var hostile = extraction.Rows.Single(r => r.Flags.Contains("suspected_injection") && r.Fields[Fields.TagName].Value!.ToString() == Fx.Truth("f-injection").Expect.InjectionTags[0]);
        hostile.Band.Should().NotBe("auto");
        extraction.Summary.InjectionFlagged.Should().Be(2);

        var result = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, hostile.Id,
            new PatchRowBody(Json((Fields.Description, "Auxiliary sensor")), null), CancellationToken.None));

        result.Row.Flags.Should().NotContain("suspected_injection");
        result.Row.Flags.Should().Contain("page_injection_text", "the note on the page is still there");
        result.Summary.InjectionFlagged.Should().Be(1);
    }

    [Theory]
    [InlineData("noSuchField", "x")]
    [InlineData("rangeHigh", "1,200")]
    [InlineData("rangeHigh", "abc")]
    [InlineData("signalType", "spoon")]
    [InlineData("tagName", "")]
    [InlineData("description", 5)]
    public async Task A_bad_edit_is_refused_with_invalid_field_and_changes_nothing(string field, object value)
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var row = extraction.Rows[3];

        var refused = await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, row.Id, new PatchRowBody(Json((field, value)), null), CancellationToken.None)));

        refused.Code.Should().Be("invalid_field");
        refused.Kind.Should().Be(ErrorKind.Validation);
        (await host.GetAsync(extraction.Id)).Rows[3].Should().BeEquivalentTo(row);
    }

    [Fact]
    public async Task Edits_need_a_body_and_an_existing_row_and_a_finished_extraction_and_an_open_row()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));
        var row = extraction.Rows[0];

        (await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, row.Id, new PatchRowBody(null, null), CancellationToken.None)))).Code.Should().Be("invalid_request");
        (await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, 777_777, new PatchRowBody(null, true), CancellationToken.None)))).Code.Should().Be("not_found");
        (await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(777_777, row.Id, new PatchRowBody(null, true), CancellationToken.None)))).Code.Should().Be("not_found");

        await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, [row.Id], ExtractionHost.Engineer, CancellationToken.None));
        var accepted = await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, row.Id, new PatchRowBody(Json((Fields.Description, "x")), null), CancellationToken.None)));
        accepted.Code.Should().Be("row_already_accepted");
        accepted.Kind.Should().Be(ErrorKind.Conflict);

        await host.WithDbAsync(async db =>
        {
            var e = await db.Extractions.SingleAsync();
            e.Status = ExtractionStatus.Running;
            await db.SaveChangesAsync();
            return 0;
        });
        var running = await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, extraction.Rows[1].Id, new PatchRowBody(null, true), CancellationToken.None)));
        running.Code.Should().Be("extraction_not_ready");
        (await Refused(() => host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None)))).Code.Should().Be("extraction_not_ready");
    }

    [Fact]
    public async Task An_excluded_row_is_left_out_of_the_counts_the_duplicates_and_the_accept()
    {
        using var host = HostFor("a-clean-table");
        var (_, extraction) = await host.UploadAndRunAsync(Fx.Pdf("a-clean-table"));

        var excluded = await host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, extraction.Rows[0].Id, new PatchRowBody(null, true), CancellationToken.None));
        excluded.Row.State.Should().Be(ExtractedRowState.Excluded);
        excluded.Summary.Excluded.Should().Be(1);
        excluded.Summary.Auto.Should().Be(49);

        var result = await host.WithServiceAsync(s => s.AcceptAsync(extraction.Id, null, ExtractionHost.Engineer, CancellationToken.None));
        result.AcceptedRowIds.Should().HaveCount(49).And.NotContain(extraction.Rows[0].Id);

        var back = await Refused(() => host.WithServiceAsync(s => s.PatchRowAsync(extraction.Id, extraction.Rows[1].Id, new PatchRowBody(null, false), CancellationToken.None)));
        back.Code.Should().Be("row_already_accepted");
    }
}
