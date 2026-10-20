using System.Net.Http.Json;
using System.Text.Json;
using Bosun.Application;
using Bosun.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Bosun.Api.IntegrationTests;

/// <summary>
/// Pre-seeded SQLite file created once per test run by booting the real app (migrations + the 5,000-tag seed),
/// then copied per test class: a real file each time, never EF InMemory, without re-seeding for every class.
/// </summary>
internal static class TemplateDatabase
{
    private static readonly Lazy<string> Path_ = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Path => Path_.Value;

    private static string Build()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bosun-tests", "template-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = System.IO.Path.Combine(dir, "template.db");

        using (var app = new TestApp(file, seed: true))
        {
            using var client = app.CreateClient(); // starts the host: migrate + seed
            client.GetAsync("/health").GetAwaiter().GetResult().EnsureSuccessStatusCode();
        }

        // Fold the WAL into the main file so a plain file copy is a complete database.
        SqliteConnection.ClearAllPools();
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDelete(dir);
        return file;
    }

    public static void TryDelete(string dir)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: the temp dir is outside the repo
        }
    }
}

/// <summary>The real API on a TestServer against its own real SQLite file (copied from the seeded template).</summary>
public class TestApp : WebApplicationFactory<Program>
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "test-admin-password";
    public const string ApproverUser = "approver";
    public const string ApproverPassword = "test-approver-password";

    private readonly string? tempDir;
    private readonly string environment;
    private readonly bool seed;
    private readonly Dictionary<string, string?> extra;

    public string DbPath { get; }

    /// <summary>Test-class database: a fresh copy of the seeded template.</summary>
    public TestApp()
        : this(Copy(out var dir), seed: false)
    {
        tempDir = dir;
    }

    protected TestApp(string environment, Dictionary<string, string?>? extra = null)
        : this(Copy(out var dir), seed: false, environment, extra)
    {
        tempDir = dir;
    }

    internal TestApp(string dbPath, bool seed, string environment = "Development", Dictionary<string, string?>? extra = null)
    {
        DbPath = dbPath;
        this.seed = seed;
        this.environment = environment;
        this.extra = extra ?? [];
    }

    private static string Copy(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "bosun-tests", "db-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "bosun.db");
        File.Copy(TemplateDatabase.Path, target);
        return target;
    }

    /// <summary>Hook for tests that swap services (for example the LLM client for a scripted one).</summary>
    protected virtual void ConfigureTestServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureServices(ConfigureTestServices);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={DbPath}",
                ["Database:Provider"] = "sqlite",
                ["Auth:AdminUsername"] = AdminUser,
                ["Auth:AdminPassword"] = AdminPassword,
                ["Auth:ApproverUsername"] = ApproverUser,
                ["Auth:ApproverPassword"] = ApproverPassword,
                ["Seed:Enabled"] = seed ? "true" : "false",

                // Test output stays readable: warnings and errors only (the JSON console would otherwise print every request).
                ["Serilog:MinimumLevel:Default"] = "Warning",

                // Never let a test spend tokens by accident: fake keys and a fake model (the .env keys are overridden).
                // LiveLlmTests passes the real values back in explicitly.
                ["Llm:Provider"] = "gemini",
                ["Llm:Model"] = "test-model",
                ["Llm:ApiKey"] = "test-key-0123456789abcdef",
                ["Embedding:Model"] = "test-embedding",
                ["Embedding:ApiKey"] = "test-key-0123456789abcdef",
                ["Embedding:Dimensions"] = "4",
                ["Auth:LoginRateLimit:PermitLimit"] = "100000", // many tests log in; the limiter has its own test with a tight limit
            };
            foreach (var (key, value) in extra)
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && tempDir is not null)
        {
            TemplateDatabase.TryDelete(tempDir);
        }
    }

    public async Task<HttpClient> LoginAsync(string user = AdminUser, string password = AdminPassword)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password }, BosunJson.Options);
        response.EnsureSuccessStatusCode();
        return client;
    }

    public Task<HttpClient> LoginApproverAsync() => LoginAsync(ApproverUser, ApproverPassword);
}

/// <summary>A <see cref="TestApp"/> with a chosen environment and extra configuration.</summary>
public sealed class CustomApp(string environment, Dictionary<string, string?>? extra = null) : TestApp(environment, extra);

public static class ApiExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync(this HttpClient client, string url, object? body = null) =>
        client.PostAsync(url, body is null ? new StringContent("{}", System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body, options: BosunJson.Options));

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"expected success but got {(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, BosunJson.Options)!;
    }

    /// <summary>The RFC 7807 body; asserts the status and returns the stable <c>code</c>.</summary>
    public static async Task<(string Code, JsonElement Body)> ReadProblemAsync(this HttpResponseMessage response, int expectedStatus)
    {
        var text = await response.Content.ReadAsStringAsync();
        ((int)response.StatusCode).Should().Be(expectedStatus, text);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        body.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        return (body.GetProperty("code").GetString()!, body);
    }

    public static async Task<PagedResult<TagDto>> TagsAsync(this HttpClient client, string query = "") =>
        await (await client.GetAsync($"/api/projects/1/tags{query}")).ReadAsync<PagedResult<TagDto>>();

    public static async Task<TagDto> TagAsync(this HttpClient client, string tagName)
    {
        var page = await client.TagsAsync($"?filter={Uri.EscapeDataString(tagName)}&pageSize=50");
        return page.Items.Single(t => t.TagName == tagName);
    }

    /// <summary>Create -> validate -> approve -> apply for a change set with the given ops; returns the applied change set.</summary>
    public static async Task<ChangeSetDto> ApplyOpsAsync(this HttpClient client, string title, params object[] ops)
    {
        var created = await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title, ops })).ReadAsync<ChangeSetDto>();
        (await (await client.PostJsonAsync($"/api/change-sets/{created.Id}/validate")).ReadAsync<ValidationResultDto>()).Valid.Should().BeTrue();
        await (await client.PostJsonAsync($"/api/change-sets/{created.Id}/approve")).ReadAsync<ChangeSetDto>();
        return await (await client.PostJsonAsync($"/api/change-sets/{created.Id}/apply")).ReadAsync<ChangeSetDto>();
    }
}
