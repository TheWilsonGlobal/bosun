using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain;
using Bosun.Evals.Fixtures;
using Bosun.Evals.Offline;
using Bosun.Extraction;
using Bosun.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bosun.Api.IntegrationTests;

/// <summary>The real API with extraction on the SCRIPTED table reader (no network, no spend), on a real SQLite file and a real document store.</summary>
public sealed class ExtractionApp(Dictionary<string, string?>? extra = null) : TestApp("Development", Merge(extra))
{
    public static readonly string FixtureDirectory = Path.Combine(RepoRoot.Resolve(), "evals", "extraction");

    public TableParsingLlm Llm { get; } = new(page => []);

    private static Dictionary<string, string?> Merge(Dictionary<string, string?>? extra)
    {
        var settings = new Dictionary<string, string?> { ["Extraction:DualReadSamplePages"] = "0", ["Extraction:DualReadLowScoreShare"] = "2" };
        foreach (var (key, value) in extra ?? [])
        {
            settings[key] = value;
        }

        return settings;
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<ILlmClient>();
        services.AddSingleton<ILlmClient>(Llm);
    }

    public static byte[] Pdf(string id) => File.ReadAllBytes(Path.Combine(FixtureDirectory, id + ".pdf"));

    public static FixtureTruth Truth(string id) => FixtureTruth.Load(Path.Combine(FixtureDirectory, id + ".truth.json"));
}

public sealed class ExtractionApiTests : IDisposable
{
    private readonly ExtractionApp app = new();

    public void Dispose() => app.Dispose();

    private static MultipartFormDataContent Form(byte[] bytes, string fileName = "list.pdf", string partName = "file", string contentType = "application/pdf")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { content, partName, fileName } };
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(int DocumentId, int ExtractionId)> UploadAsync(HttpClient client, byte[] pdf, string fileName = "list.pdf")
    {
        var response = await client.PostAsync("/api/projects/1/documents", Form(pdf, fileName));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await JsonOf(response);
        var extractionId = body.GetProperty("extractionId").GetInt32();
        response.Headers.Location!.ToString().Should().Be($"/api/extractions/{extractionId}");
        return (body.GetProperty("documentId").GetInt32(), extractionId);
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, int extractionId)
    {
        for (var i = 0; i < 1200; i++)
        {
            var body = await JsonOf(await client.GetAsync($"/api/extractions/{extractionId}"));
            var status = body.GetProperty("status").GetString();
            if (status is not ("queued" or "running"))
            {
                return body;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The extraction did not finish.");
    }

    // ---------- auth ----------

    [Fact]
    public async Task Uploading_editing_and_accepting_need_a_session_but_reading_follows_the_configurator_rule()
    {
        using var anonymous = app.CreateClient();

        (await anonymous.PostAsync("/api/projects/1/documents", Form(ExtractionApp.Pdf("f-injection")))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PatchAsync("/api/extractions/1/rows/1", JsonContent.Create(new { excluded = true }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostJsonAsync("/api/extractions/1/accept")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/projects/1/documents")).StatusCode.Should().Be(HttpStatusCode.OK, "reads are open in Development");

        using var production = new CustomApp("Production", new Dictionary<string, string?> { ["Auth:AdminPassword"] = "a-real-production-secret" });
        using var client = production.CreateClient();
        (await client.GetAsync("/api/projects/1/documents")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/extractions/1")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/extractions/1/pages/1/image")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Every_extraction_route_is_listed_and_the_write_routes_require_authorization_and_none_can_apply_or_approve()
    {
        var routes = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.Contains("/extractions", StringComparison.Ordinal) || e.RoutePattern.RawText.EndsWith("/documents", StringComparison.Ordinal))
            .ToList();

        routes.Select(r => $"{string.Join('|', r.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods))} {r.RoutePattern.RawText}").Should().BeEquivalentTo(
        [
            "GET /api/projects/{projectId:int}/documents",
            "POST /api/projects/{projectId:int}/documents",
            "POST /api/documents/{documentId:int}/extractions",
            "GET /api/extractions/{id:int}",
            "GET /api/extractions/{id:int}/pages/{page:int}/image",
            "PATCH /api/extractions/{id:int}/rows/{rowId:int}",
            "POST /api/extractions/{id:int}/accept",
        ]);
        routes.Should().OnlyContain(r => !System.Text.RegularExpressions.Regex.IsMatch(r.RoutePattern.RawText!, "apply|approve|reject", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        foreach (var write in routes.Where(r => r.Metadata.OfType<HttpMethodMetadata>().Any(m => m.HttpMethods.Any(h => h is "POST" or "PATCH" or "PUT" or "DELETE"))))
        {
            write.Metadata.OfType<IAuthorizeData>().Should().NotBeEmpty($"{write.RoutePattern.RawText} writes");
        }
    }

    // ---------- upload ----------

    [Fact]
    public async Task A_pdf_upload_is_accepted_with_202_and_polling_shows_the_extraction_through_to_completion()
    {
        using var client = await app.LoginAsync();

        var (documentId, extractionId) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"), "IO list.pdf");
        var done = await WaitAsync(client, extractionId);

        done.GetProperty("status").GetString().Should().Be("completed");
        done.GetProperty("id").GetInt32().Should().Be(extractionId);
        done.GetProperty("documentId").GetInt32().Should().Be(documentId);
        done.GetProperty("progress").GetProperty("pagesDone").GetInt32().Should().Be(2);
        done.GetProperty("document").GetProperty("displayName").GetString().Should().Be("IO list.pdf");
        done.GetProperty("document").GetProperty("pageCount").GetInt32().Should().Be(2);
        done.GetProperty("bands").GetProperty("autoAtLeast").GetDouble().Should().Be(0.9);
        done.GetProperty("bands").GetProperty("reviewAtLeast").GetDouble().Should().Be(0.6);
        done.GetProperty("summary").GetProperty("rows").GetInt32().Should().Be(50);
        done.GetProperty("summary").GetProperty("auto").GetInt32().Should().Be(50);
        done.GetProperty("pages")[0].GetProperty("path").GetString().Should().Be("text");
        done.GetProperty("pages")[0].GetProperty("reasons").EnumerateArray().Select(e => e.GetString()).Should().Contain("table_lines");
        var row = done.GetProperty("rows")[0];
        row.GetProperty("state").GetString().Should().Be("pending");
        row.GetProperty("band").GetString().Should().Be("auto");
        row.GetProperty("sourceRef").GetString().Should().Be("p1:L4");
        row.GetProperty("snippet").GetString().Should().StartWith("ME-SCR1-TT01");
        row.GetProperty("bbox").GetArrayLength().Should().Be(4);
        var tag = row.GetProperty("fields").GetProperty("tagName");
        tag.GetProperty("value").GetString().Should().Be("ME-SCR1-TT01");
        tag.GetProperty("raw").GetString().Should().Be("ME-SCR1-TT01");
        tag.GetProperty("confidence").GetDouble().Should().Be(0.95);
        tag.GetProperty("band").GetString().Should().Be("auto");
        tag.GetProperty("source").GetString().Should().Be("text");
        row.GetProperty("fields").EnumerateObject().Select(p => p.Name).Should().Equal(Fields.All);

        var documents = await JsonOf(await client.GetAsync("/api/projects/1/documents"));
        documents.GetArrayLength().Should().Be(1);
        documents[0].GetProperty("latestExtraction").GetProperty("status").GetString().Should().Be("completed");
        documents[0].GetProperty("latestExtraction").GetProperty("summary").GetProperty("rows").GetInt32().Should().Be(50);
    }

    [Fact]
    public async Task Upload_hardening_over_http_non_pdf_missing_part_wrong_type_and_oversize()
    {
        using var client = await app.LoginAsync();

        var zip = await client.PostAsync("/api/projects/1/documents", Form("PK\u0003\u0004 zip bytes"u8.ToArray(), "evil.pdf"));
        (await zip.ReadProblemAsync(415)).Code.Should().Be("not_a_pdf");

        var wrongPart = await client.PostAsync("/api/projects/1/documents", Form(ExtractionApp.Pdf("f-injection"), "x.pdf", "document"));
        (await wrongPart.ReadProblemAsync(400)).Code.Should().Be("no_file");

        var json = await client.PostAsync("/api/projects/1/documents", JsonContent.Create(new { file = "x" }));
        ((int)json.StatusCode).Should().BeOneOf(400, 415);

        var truncated = await client.PostAsync("/api/projects/1/documents", Form("%PDF-1.4\n1 0 obj"u8.ToArray(), "cut.pdf"));
        (await truncated.ReadProblemAsync(422)).Code.Should().Be("pdf_unreadable");

        var unknownProject = await client.PostAsync("/api/projects/999/documents", Form(ExtractionApp.Pdf("f-injection")));
        (await unknownProject.ReadProblemAsync(404)).Code.Should().Be("not_found");

        using var small = new ExtractionApp(new() { ["Extraction:MaxUploadBytes"] = "5000" });
        using var smallClient = await small.LoginAsync();
        var oversize = await smallClient.PostAsync("/api/projects/1/documents", Form(ExtractionApp.Pdf("a-clean-table")));
        (await oversize.ReadProblemAsync(413)).Code.Should().Be("document_too_large");

        (await JsonOf(await client.GetAsync("/api/projects/1/documents"))).GetArrayLength().Should().Be(0, "nothing refused was kept");
    }

    [Theory]
    [InlineData("NUL.pdf")]
    [InlineData("CON.pdf")]
    [InlineData("aux")]
    [InlineData("COM1.pdf")]
    [InlineData("..\\..\\boom.pdf")]
    [InlineData("C:\\temp\\x.pdf")]
    public async Task Windows_device_names_and_traversal_in_the_uploaded_file_name_do_not_break_the_upload(string fileName)
    {
        using var client = await app.LoginAsync();
        var pdf = ExtractionApp.Pdf("f-injection");

        var (_, extractionId) = await UploadAsync(client, pdf, fileName);
        var done = await WaitAsync(client, extractionId);

        done.GetProperty("status").GetString().Should().Be("completed");
        done.GetProperty("rows").GetArrayLength().Should().Be(22);
        done.GetProperty("document").GetProperty("sizeBytes").GetInt64().Should().Be(pdf.Length, "the bytes were stored, not swallowed by a device");
        var label = done.GetProperty("document").GetProperty("displayName").GetString()!;
        label.Should().NotBeEmpty().And.NotContainAny("\\", "/", ":");
    }

    // ---------- the review loop ----------

    [Fact]
    public async Task Patch_corrects_a_row_over_http_and_returns_the_row_the_summary_and_the_other_rows_that_changed()
    {
        using var client = await app.LoginAsync();
        var (_, id) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        var extraction = await WaitAsync(client, id);
        var rowId = extraction.GetProperty("rows")[5].GetProperty("id").GetInt32();

        var response = await client.PatchAsync($"/api/extractions/{id}/rows/{rowId}", JsonContent.Create(new { fields = new { description = "Corrected by the engineer", rangeHigh = "1000" } }, options: BosunJson.Options));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await JsonOf(response);
        body.GetProperty("row").GetProperty("humanCorrected").GetBoolean().Should().BeTrue();
        body.GetProperty("row").GetProperty("fields").GetProperty("description").GetProperty("value").GetString().Should().Be("Corrected by the engineer");
        body.GetProperty("row").GetProperty("fields").GetProperty("description").GetProperty("source").GetString().Should().Be("human");
        body.GetProperty("row").GetProperty("fields").GetProperty("rangeHigh").GetProperty("value").GetDouble().Should().Be(1000);
        body.GetProperty("summary").GetProperty("humanCorrected").GetInt32().Should().Be(1);
        body.GetProperty("alsoChanged").GetArrayLength().Should().Be(0);

        var again = await JsonOf(await client.GetAsync($"/api/extractions/{id}"));
        again.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetInt32() == rowId).GetProperty("humanCorrected").GetBoolean().Should().BeTrue();

        var bad = await client.PatchAsync($"/api/extractions/{id}/rows/{rowId}", JsonContent.Create(new { fields = new { rangeHigh = "1,200" } }, options: BosunJson.Options));
        var (code, problem) = await bad.ReadProblemAsync(400);
        code.Should().Be("invalid_field");
        problem.GetProperty("field").GetString().Should().Be("rangeHigh");
        (await (await client.PatchAsync($"/api/extractions/{id}/rows/{rowId}", JsonContent.Create(new { }, options: BosunJson.Options))).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PatchAsync($"/api/extractions/{id}/rows/999999", JsonContent.Create(new { excluded = true }, options: BosunJson.Options))).ReadProblemAsync(404)).Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Accept_over_http_creates_a_draft_that_a_human_can_validate_approve_and_apply_and_must_fix_rows_block_it_with_a_422()
    {
        using var client = await app.LoginAsync();
        var (_, bad) = await UploadAsync(client, ExtractionApp.Pdf("e-customer-io-list"));
        var flagged = await WaitAsync(client, bad);
        flagged.GetProperty("summary").GetProperty("mustFix").GetInt32().Should().Be(5, "7 designed defects minus the 2 on the scanned page, which the scripted reader returns empty here");

        var blocked = await client.PostJsonAsync($"/api/extractions/{bad}/accept");
        var (code, problem) = await blocked.ReadProblemAsync(422);
        code.Should().Be("unresolved_must_fix");
        problem.GetProperty("rowIds").GetArrayLength().Should().Be(5);
        problem.GetProperty("count").GetInt32().Should().Be(5);

        var (_, good) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        await WaitAsync(client, good);
        var before = (await client.TagsAsync("?pageSize=1")).Total;

        var accepted = await client.PostJsonAsync($"/api/extractions/{good}/accept", new { });
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
        var body = await JsonOf(accepted);
        body.GetProperty("acceptedRowIds").GetArrayLength().Should().Be(50);
        var changeSetId = body.GetProperty("changeSets")[0].GetProperty("id").GetInt32();
        accepted.Headers.Location!.ToString().Should().Be($"/api/change-sets/{changeSetId}");

        var changeSet = await (await client.GetAsync($"/api/change-sets/{changeSetId}")).ReadAsync<ChangeSetDto>();
        changeSet.Source.Should().Be(ChangeSetSource.Extraction);
        changeSet.Status.Should().Be(ChangeSetStatus.Draft);
        changeSet.ApprovedBy.Should().BeNull();
        (await client.TagsAsync("?pageSize=1")).Total.Should().Be(before, "accepting did not touch the tags");

        (await (await client.PostJsonAsync($"/api/change-sets/{changeSetId}/validate")).ReadAsync<ValidationResultDto>()).Valid.Should().BeTrue();
        await (await client.PostJsonAsync($"/api/change-sets/{changeSetId}/approve")).ReadAsync<ChangeSetDto>();
        var applied = await (await client.PostJsonAsync($"/api/change-sets/{changeSetId}/apply")).ReadAsync<ChangeSetDto>();
        applied.Status.Should().Be(ChangeSetStatus.Applied);
        (await client.TagsAsync("?pageSize=1")).Total.Should().Be(before + 50);

        var again = await client.PostJsonAsync($"/api/extractions/{good}/accept");
        (await again.ReadProblemAsync(422)).Code.Should().Be("no_rows_to_accept");
    }

    [Fact]
    public async Task The_page_image_endpoint_serves_a_png_and_crops_to_a_row_and_refuses_pages_that_do_not_exist()
    {
        using var client = await app.LoginAsync();
        var (_, id) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        var extraction = await WaitAsync(client, id);
        var rowId = extraction.GetProperty("rows")[3].GetProperty("id").GetInt32();

        var full = await client.GetAsync($"/api/extractions/{id}/pages/1/image?dpi=80");
        var crop = await client.GetAsync($"/api/extractions/{id}/pages/1/image?dpi=80&rowId={rowId}");

        full.StatusCode.Should().Be(HttpStatusCode.OK);
        full.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        full.Headers.CacheControl!.Private.Should().BeTrue();
        var fullBytes = await full.Content.ReadAsByteArrayAsync();
        var cropBytes = await crop.Content.ReadAsByteArrayAsync();
        fullBytes.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        Height(cropBytes).Should().BeLessThan(Height(fullBytes) / 3);
        (await (await client.GetAsync($"/api/extractions/{id}/pages/9/image")).ReadProblemAsync(404)).Code.Should().Be("page_out_of_range");
        (await (await client.GetAsync("/api/extractions/424242/pages/1/image")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.GetAsync("/api/extractions/424242")).ReadProblemAsync(404)).Code.Should().Be("not_found");
    }

    private static int Height(byte[] png) => (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];

    [Fact]
    public async Task A_provider_failure_shows_up_on_the_extraction_with_its_specific_code()
    {
        using var failing = new FailingApp();
        using var client = await failing.LoginAsync();

        var (_, id) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        var done = await WaitAsync(client, id);

        done.GetProperty("status").GetString().Should().Be("failed");
        done.GetProperty("errorCode").GetString().Should().Be("llm_rate_limited");
        done.GetProperty("errorMessage").GetString().Should().Contain("quota");
        done.GetProperty("pages").EnumerateArray().Should().OnlyContain(p => p.GetProperty("status").GetString() == "failed");
    }

    [Fact]
    public async Task Read_again_over_http_starts_a_new_extraction_of_the_stored_document()
    {
        using var client = await app.LoginAsync();
        var (documentId, first) = await UploadAsync(client, ExtractionApp.Pdf("f-injection"));
        await WaitAsync(client, first);

        var response = await client.PostJsonAsync($"/api/documents/{documentId}/extractions");

        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await JsonOf(response);
        var second = body.GetProperty("extractionId").GetInt32();
        second.Should().NotBe(first);
        body.GetProperty("documentId").GetInt32().Should().Be(documentId);
        response.Headers.Location!.ToString().Should().Be($"/api/extractions/{second}");
        (await WaitAsync(client, second)).GetProperty("status").GetString().Should().Be("completed");

        (await (await client.PostJsonAsync("/api/documents/424242/extractions")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        using var anonymous = app.CreateClient();
        (await anonymous.PostAsync($"/api/documents/{documentId}/extractions", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class FailingApp() : TestApp("Development", new Dictionary<string, string?> { ["Extraction:DualReadSamplePages"] = "0" })
    {
        protected override void ConfigureTestServices(IServiceCollection services)
        {
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(new RateLimited());
        }

        private sealed class RateLimited : ILlmClient
        {
            public string Provider => "test";

            public string DefaultModel => "test";

            public int DefaultMaxOutputTokens => 8192;

            public int MaxOutputTokensCap => 32768;

            public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken) =>
                throw new LlmException(LlmErrorCodes.RateLimited, "Gemini quota exceeded (HTTP 429). Retry in about 58532 s.", 429, TimeSpan.FromSeconds(58532));
        }
    }
}
