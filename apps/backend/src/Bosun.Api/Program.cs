using Bosun.Agent;
using Bosun.Api;
using Bosun.Api.Auth;
using Bosun.Application;
using Bosun.Application.Llm;
using Bosun.Domain;
using Bosun.Extraction;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Llm;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// The single root .env (found by walking up to Bosun.sln, not from the cwd) becomes the lowest-priority config source.
builder.Configuration.AddBosunEnvAsDefaults();

// The LLM price table (estimates; see the Note inside). Optional, and read from beside the binary.
builder.Configuration.AddJsonFile(new PhysicalFileProvider(AppContext.BaseDirectory), "llm-pricing.json", optional: true, reloadOnChange: false);

// Serilog -> JSON on the console (one compact JSON object per line, message rendered). Every LLM call logs feature, model,
// prompt_version, tokens in/out/thought and latency through this (plan s3).
builder.Host.UseSerilog((context, _, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

// API listens on loopback only (127.0.0.1, never "localhost": the IPv6 fallback costs ~215 ms per connection here).
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration["Server:Port"] ?? "5480"}");
}

builder.Services.ConfigureHttpJsonOptions(o => BosunJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    var problem = ctx.ProblemDetails;
    if (!problem.Extensions.ContainsKey("code"))
    {
        problem.Extensions["code"] = Problems.CodeFor(problem.Status ?? ctx.HttpContext.Response.StatusCode);
    }

    problem.Extensions["traceId"] = System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
});
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddOpenApi();

builder.Services.AddBosunDatabase();
builder.Services.AddBosunApplication();
builder.Services.AddBosunAuth();
builder.Services.AddBosunLlm();
builder.Services.AddBosunAgent();
builder.Services.AddBosunExtraction();
builder.Services.AddHostedService<ExtractionLifecycle>();

// Live agent progress over SignalR (JSON protocol, same casing and enum names as the REST API); the sink replaces the no-op one.
builder.Services.AddSignalR().AddJsonProtocol(o => BosunJson.Configure(o.PayloadSerializerOptions));
builder.Services.AddSingleton<IAgentEventSink, SignalRAgentEventSink>();
builder.Services.AddHostedService<AgentLifecycle>();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Behind nginx/another TLS terminator: trust X-Forwarded-For/-Proto from loopback proxies only (the framework default).
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

// `--print-config`: shows where the database resolves to (used to prove it does not depend on the cwd) and exits.
if (args.Contains("--print-config"))
{
    var resolved = DatabaseSettings.From(app.Configuration);
    Console.WriteLine($"root={app.Configuration["Bosun:RepoRoot"]}");
    Console.WriteLine($"db={new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(resolved.ConnectionString).DataSource}");
    return;
}

// Fail fast: unsupported DB_PROVIDER, or the default admin password outside Development.
_ = DatabaseSettings.From(app.Configuration);
AuthSetup.EnsureSafeCredentials(app.Configuration, app.Environment);

// Maintenance commands: `dotnet run --project apps/backend/src/Bosun.Api -- --reset-db | --seed | --migrate`
if (args.Contains("--migrate"))
{
    await DatabaseInitializer.MigrateAsync(app.Services);
    Console.WriteLine("Migrations applied.");
    return;
}

string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[index + 1] : null;
}

// File arguments are relative to the repo root (dotnet run would otherwise use the project directory as cwd).
string? ArgPath(string name) =>
    ArgValue(name) is { } value ? Path.GetFullPath(value, app.Configuration["Bosun:RepoRoot"]!) : null;

// `--export-seed [path]`: write the generated demo project as a .bosun.json (default seed/vessel-mv-halong.bosun.json).
if (args.Contains("--export-seed"))
{
    var path = ArgPath("--export-seed") ?? Path.Combine(app.Configuration["Bosun:RepoRoot"]!, "seed", "vessel-mv-halong.bosun.json");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var generated = ProjectFileImporter.FromGenerator();
    await File.WriteAllTextAsync(path, ProjectFileSerializer.Serialize(generated));
    Console.WriteLine($"Wrote {generated.Tags.Count} tags to {path}");
    return;
}

// `--export <path> [--project N]`: export a project of the current database as .bosun.json.
if (args.Contains("--export"))
{
    var path = ArgPath("--export") ?? throw new InvalidOperationException("--export needs a file path.");
    var projectId = int.Parse(ArgValue("--project") ?? "1", System.Globalization.CultureInfo.InvariantCulture);
    await using var scope = app.Services.CreateAsyncScope();
    var exported = await scope.ServiceProvider.GetRequiredService<ProjectFileService>().ExportAsync(projectId, CancellationToken.None);
    await File.WriteAllTextAsync(path, ProjectFileSerializer.Serialize(exported));
    Console.WriteLine($"Exported {exported.Tags.Count} tags to {path}");
    return;
}

// `--llm-usage [--since <ISO date>]`: token / latency / ESTIMATED cost per feature and model, from the llm_calls table.
if (args.Contains("--llm-usage"))
{
    await DatabaseInitializer.MigrateAsync(app.Services);
    DateTime? since = ArgValue("--since") is { } text
        ? DateTime.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : null;
    await using var usageScope = app.Services.CreateAsyncScope();
    var summary = await usageScope.ServiceProvider.GetRequiredService<LlmUsageQueries>().SummariseAsync(since, null, CancellationToken.None);
    Console.Write(LlmUsageReport.Format(summary.Rows));
    return;
}

if (args.Contains("--reset-db") || args.Contains("--seed"))
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var from = ArgPath("--from"); // --reset-db --from seed/vessel-mv-halong.bosun.json
    int tags;
    if (args.Contains("--reset-db") && from is not null)
    {
        var file = ProjectFileSerializer.Deserialize(await File.ReadAllTextAsync(from));
        Bosun.Infrastructure.Persistence.Sqlite.SqliteFiles.Delete(DatabaseSettings.From(app.Configuration).ConnectionString);
        await DatabaseInitializer.MigrateAsync(app.Services);
        tags = await DatabaseInitializer.ImportAsync(app.Services, file);
    }
    else
    {
        tags = args.Contains("--reset-db")
            ? await DatabaseInitializer.ResetAsync(app.Services)
            : await DatabaseInitializer.SeedAsync(app.Services);
    }

    var settings = DatabaseSettings.From(app.Configuration);
    Console.WriteLine($"Database ready at {new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(settings.ConnectionString).DataSource}: {tags} tags seeded in {watch.ElapsedMilliseconds} ms.");
    return;
}

// Development: migrate on startup, and seed an empty database so a fresh clone has data.
// Elsewhere migrations are an explicit deployment step.
if (app.Environment.IsDevelopment())
{
    await DatabaseInitializer.MigrateAsync(app.Services);
    if (app.Configuration.GetValue("Seed:Enabled", true))
    {
        await DatabaseInitializer.SeedAsync(app.Services);
    }

    app.MapOpenApi();
    app.MapScalarApiReference(o => o.WithTitle("Bosun API"));
}

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapBosunApi(readsRequireLogin: !app.Environment.IsDevelopment());
app.MapAi(requireLogin: !app.Environment.IsDevelopment());
app.MapAgent(readsRequireLogin: !app.Environment.IsDevelopment());
app.MapExtraction(readsRequireLogin: !app.Environment.IsDevelopment());

app.Run();

/// <summary>Entry point type, public so WebApplicationFactory&lt;Program&gt; can host the API in integration tests.</summary>
public partial class Program;
