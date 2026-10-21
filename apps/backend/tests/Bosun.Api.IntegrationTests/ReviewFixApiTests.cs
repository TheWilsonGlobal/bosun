using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bosun.Evals.Fixtures;

namespace Bosun.Api.IntegrationTests;

/// <summary>HTTP contract of the review fixes: page_too_large, flagged_rows_need_override + allowFlagged, DELETE /api/documents/{id}, double accept.</summary>
public sealed class ReviewFixApiTests : IDisposable
{
    private readonly ExtractionApp app = new();

    public void Dispose() => app.Dispose();

    private static MultipartFormDataContent Form(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        return new MultipartFormDataContent { { content, "file", "list.pdf" } };
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<(int DocumentId, int ExtractionId)> UploadAsync(HttpClient client, byte[] pdf)
    {
        var response = await client.PostAsync("/api/projects/1/documents", Form(pdf));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var body = await JsonOf(response);
        return (body.GetProperty("documentId").GetInt32(), body.GetProperty("extractionId").GetInt32());
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, int extractionId)
    {
        for (var i = 0; i < 1200; i++)
        {
            var body = await JsonOf(await client.GetAsync($"/api/extractions/{extractionId}"));
            if (body.GetProperty("status").GetString() is not ("queued" or "running"))
            {
                return body;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The extraction did not finish.");
    }

    [Fact]
    public async Task An_oversized_page_is_422_page_too_large_with_the_page_number()
    {
        using var client = await app.LoginAsync();
        var pdf = new MiniPdf();
        pdf.AddPage(30_000, 30_000).Text(20, 40, "ME-EXH-TT01");

        var response = await client.PostAsync("/api/projects/1/documents", Form(pdf.Build()));

        var (code, problem) = await response.ReadProblemAsync(422);
        code.Should().Be("page_too_large");
        problem.GetProperty("page").GetInt32().Should().Be(1);
        problem.GetProperty("detail").GetString().Should().Contain("points");
    }

    [Fact]
    public async Task Accepting_flagged_rows_needs_allowFlagged_with_their_ids()
    {
        using var client = await app.LoginAsync();
        var (_, id) = await UploadAsync(client, ExtractionApp.Pdf("f-injection"));
        var extraction = await WaitAsync(client, id);
        var rows = extraction.GetProperty("rows").EnumerateArray().ToList();
        var flagged = rows.First(r => r.GetProperty("flags").EnumerateArray().Any(f => f.GetString() == "suspected_injection") && r.GetProperty("band").GetString() != "must_fix");
        var flaggedId = flagged.GetProperty("id").GetInt32();

        var refused = await client.PostJsonAsync($"/api/extractions/{id}/accept", new { rowIds = new[] { flaggedId } });
        var (code, problem) = await refused.ReadProblemAsync(422);
        code.Should().Be("flagged_rows_need_override");
        problem.GetProperty("rowIds").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(flaggedId);

        var accepted = await client.PostJsonAsync($"/api/extractions/{id}/accept", new { rowIds = new[] { flaggedId }, allowFlagged = new[] { flaggedId } });
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
        (await JsonOf(accepted)).GetProperty("acceptedRowIds").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(flaggedId);
    }

    [Fact]
    public async Task Two_simultaneous_accepts_over_http_give_one_201_and_one_409_and_no_500()
    {
        using var client = await app.LoginAsync();
        var (_, id) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        var extraction = await WaitAsync(client, id);
        var rowIds = extraction.GetProperty("rows").EnumerateArray().Take(8).Select(r => r.GetProperty("id").GetInt32()).ToArray();

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.PostJsonAsync($"/api/extractions/{id}/accept", new { rowIds })));

        responses.Select(r => r.StatusCode).Order().Should().Equal(HttpStatusCode.Created, HttpStatusCode.Conflict);
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        (await loser.ReadProblemAsync(409)).Code.Should().Be("rows_already_decided");
    }

    [Fact]
    public async Task Deleting_a_document_needs_a_session_removes_everything_and_404s_afterwards()
    {
        using var client = await app.LoginAsync();
        var (documentId, extractionId) = await UploadAsync(client, ExtractionApp.Pdf("a-clean-table"));
        await WaitAsync(client, extractionId);

        using (var anonymous = app.CreateClient())
        {
            (await anonymous.DeleteAsync($"/api/documents/{documentId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var deleted = await client.DeleteAsync($"/api/documents/{documentId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync($"/api/extractions/{extractionId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var documents = await JsonOf(await client.GetAsync("/api/projects/1/documents"));
        documents.EnumerateArray().Should().BeEmpty();
        (await client.DeleteAsync($"/api/documents/{documentId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
