using System.Diagnostics;
using System.Net;
using Bosun.Application;
using Bosun.Infrastructure.Configuration;
using Bosun.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Api.IntegrationTests;

public class AuthTests(TestApp app) : IClassFixture<TestApp>
{
    [Theory]
    [InlineData("/api/projects/1/change-sets")]
    [InlineData("/api/change-sets/1/ops")]
    [InlineData("/api/change-sets/1/validate")]
    [InlineData("/api/change-sets/1/approve")]
    [InlineData("/api/change-sets/1/apply")]
    [InlineData("/api/change-sets/1/reject")]
    public async Task State_changing_endpoints_require_a_session(string url)
    {
        var anonymous = app.CreateClient();
        var response = await anonymous.PostJsonAsync(url, new { title = "x", ops = Array.Empty<object>() });
        (await response.ReadProblemAsync(401)).Code.Should().Be("unauthorized");
    }

    [Fact]
    public async Task Approve_and_apply_without_a_session_cannot_change_anything()
    {
        var admin = await app.LoginAsync();
        var draft = await admin.PostJsonAsync("/api/projects/1/change-sets", new
        {
            title = "Auth",
            ops = new object[] { new { type = "updateTag", tagName = "ME-EXH-TT03", field = "description", @new = "should never apply" } },
        }).ContinueWith(t => t.Result.ReadAsync<ChangeSetDto>()).Unwrap();
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/validate");

        var anonymous = app.CreateClient();
        (await (await anonymous.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadProblemAsync(401)).Code.Should().Be("unauthorized");
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve");
        (await (await anonymous.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadProblemAsync(401)).Code.Should().Be("unauthorized");

        (await admin.TagAsync("ME-EXH-TT03")).Description.Should().NotBe("should never apply");
    }

    [Fact]
    public async Task Reads_are_open_in_development()
    {
        var anonymous = app.CreateClient();
        (await anonymous.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync("/api/projects/1/tags?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_me_logout_cycle()
    {
        var client = app.CreateClient();

        var bad = await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "wrong" });
        (await bad.ReadProblemAsync(401)).Code.Should().Be("invalid_credentials");
        (await (await client.PostJsonAsync("/api/auth/login", new { username = "nobody", password = TestApp.AdminPassword })).ReadProblemAsync(401)).Code.Should().Be("invalid_credentials");
        (await (await client.PostJsonAsync("/api/auth/login", new { })).ReadProblemAsync(401)).Code.Should().Be("invalid_credentials");

        var ok = await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword });
        var session = await ok.ReadAsync<SessionDto>();
        session.Should().Match<SessionDto>(s => s.Username == TestApp.AdminUser && s.Roles.Contains("admin"));
        ok.Headers.GetValues("Set-Cookie").Single().Should().StartWith("bosun.session=").And.Contain("httponly", Exactly.Once()).And.Contain("samesite=strict");

        (await (await client.GetAsync("/api/auth/me")).ReadAsync<SessionDto>()).Username.Should().Be(TestApp.AdminUser);

        (await client.PostJsonAsync("/api/auth/logout")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var approver = await app.LoginApproverAsync();
        (await (await approver.GetAsync("/api/auth/me")).ReadAsync<SessionDto>()).Roles.Should().Equal(UserContext.ApproverRole);
    }

    private sealed record SessionDto(string Username, List<string> Roles);
}

/// <summary>Outside Development: no auto-migrate/seed or OpenAPI, reads need a session, and the default password is refused.</summary>
public class ProductionModeTests
{
    private static Dictionary<string, string?> Real(string adminPassword) => new()
    {
        ["Auth:AdminPassword"] = adminPassword,
    };

    [Fact]
    public async Task Reads_require_a_session_and_health_stays_open()
    {
        using var production = new ProductionApp(Real("a-real-production-secret"));
        // the session cookie is Secure outside Development, so the client talks https (as it does behind nginx)
        var anonymous = production.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://127.0.0.1") });

        (await (await anonymous.GetAsync("/api/projects/1/tags")).ReadProblemAsync(401)).Code.Should().Be("unauthorized");
        (await anonymous.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync("/openapi/v1.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var login = await anonymous.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "a-real-production-secret" });
        login.IsSuccessStatusCode.Should().BeTrue();
        (await anonymous.GetAsync("/api/projects/1/tags?pageSize=1")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("admin123")]
    [InlineData("")]
    [InlineData("   ")]
    public void Refuses_to_start_with_the_default_or_an_empty_admin_password(string password)
    {
        using var production = new ProductionApp(Real(password));
        var start = () => production.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("Refusing to start") && e.ToString().Contains("ADMIN_PASSWORD"));
    }

    [Fact]
    public void Refuses_to_start_with_the_documented_development_approver_password_in_production()
    {
        var settings = Real("a-real-production-secret");
        settings["Auth:ApproverPassword"] = Bosun.Api.Auth.UserStore.DevApproverPassword;
        using var production = new ProductionApp(settings);
        var start = () => production.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("development approver password"));
    }

    [Fact]
    public async Task The_development_approver_default_exists_only_in_development()
    {
        using var development = new DevDefaultsApp("Development");
        using var production = new DevDefaultsApp("Production");

        var dev = development.CreateClient();
        (await dev.PostJsonAsync("/api/auth/login", new { username = Bosun.Api.Auth.UserStore.DevApproverUsername, password = Bosun.Api.Auth.UserStore.DevApproverPassword }))
            .IsSuccessStatusCode.Should().BeTrue();

        var prod = production.CreateClient();
        (await prod.PostJsonAsync("/api/auth/login", new { username = Bosun.Api.Auth.UserStore.DevApproverUsername, password = Bosun.Api.Auth.UserStore.DevApproverPassword }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed class ProductionApp(Dictionary<string, string?> extra) : TestApp("Production", extra);

    /// <summary>No configured approver at all, so the Development default is the only candidate.</summary>
    private sealed class DevDefaultsApp(string environment) : TestApp(environment, new Dictionary<string, string?>
    {
        ["Auth:AdminPassword"] = "a-real-production-secret",
        ["Auth:ApproverUsername"] = "",
        ["Auth:ApproverPassword"] = "",
    });
}

public class ConfigurationTests
{
    [Fact]
    public void Unsupported_db_provider_fails_fast_with_a_clear_message()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "postgres",
            ["Database:ConnectionString"] = "Host=127.0.0.1",
        }).Build();

        var act = () => DatabaseSettings.From(configuration);
        act.Should().Throw<InvalidOperationException>().WithMessage("*'postgres'*not supported*Phase 8*");
    }

    [Fact]
    public async Task Host_refuses_to_start_when_db_provider_is_not_sqlite()
    {
        using var app = new CustomApp("Development", new Dictionary<string, string?> { ["Database:Provider"] = "postgres" });
        var start = () => app.CreateClient();
        start.Should().Throw<Exception>().Where(e => e.ToString().Contains("DB_PROVIDER='postgres'"));
        await Task.CompletedTask;
    }

    [Fact]
    public void Repo_root_is_found_by_walking_up_to_Bosun_sln_from_any_start_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosun-root-" + Guid.NewGuid().ToString("N")[..8]);
        var nested = Path.Combine(root, "apps", "backend", "src", "Bosun.Api", "bin", "Debug");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(root, "Bosun.sln"), "");
            File.WriteAllText(Path.Combine(root, ".env"),
                "# comment\nDB_PROVIDER=sqlite\nDB_CONNECTION=Data Source=data/custom.db\nBACKEND_PORT=\"5999\"\nADMIN_PASSWORD='quoted pw'\n\nNOT_A_PAIR\n");

            RepoRoot.TryFind(nested).Should().Be(root);
            RepoRoot.TryFind(root).Should().Be(root);
            RepoRoot.TryFind(Path.GetTempPath(), null).Should().BeNull("no Bosun.sln above the temp dir");

            var configuration = new ConfigurationBuilder().AddBosunEnv(nested).Build();
            configuration["Bosun:RepoRoot"].Should().Be(root);
            configuration["Server:Port"].Should().Be("5999");
            configuration["Auth:AdminPassword"].Should().Be("quoted pw");
            configuration["Database:ConnectionString"].Should().Be($"Data Source={Path.Combine(root, "data", "custom.db")}");
            Directory.GetCurrentDirectory().Should().NotStartWith(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Real_environment_variables_win_over_the_env_file_and_absolute_paths_are_kept()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosun-root-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Bosun.sln"), "");
            File.WriteAllText(Path.Combine(root, ".env"), "BACKEND_PORT=5480\nDB_CONNECTION=Data Source=data/db/bosun.db\n");
            Environment.SetEnvironmentVariable("BACKEND_PORT", "5555");
            try
            {
                new ConfigurationBuilder().AddBosunEnv(root).Build()["Server:Port"].Should().Be("5555");
            }
            finally
            {
                Environment.SetEnvironmentVariable("BACKEND_PORT", null);
            }

            var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "x.db");
            BosunEnvironment.ResolveConnection($"Data Source={absolute}", "sqlite", root).Should().Be($"Data Source={absolute}");
            BosunEnvironment.ResolveConnection("Data Source=:memory:", "sqlite", root).Should().Be("Data Source=:memory:");
            BosunEnvironment.ResolveConnection("Host=db;Port=5486", "postgres", root).Should().Be("Host=db;Port=5486");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_database_path_resolves_from_the_repo_root_whatever_the_cwd_is()
    {
        var repoRoot = RepoRoot.Resolve();
        File.Exists(Path.Combine(repoRoot, "Bosun.sln")).Should().BeTrue();
        var expected = new ConfigurationBuilder().AddBosunEnv().Build()["Database:ConnectionString"]!;
        expected.Should().StartWith($"Data Source={repoRoot}", "a relative DB_CONNECTION is anchored at the repo root, not at the cwd");

        // the real executable, started from three unrelated working directories, prints the very same path
        var dll = Path.Combine(AppContext.BaseDirectory, "Bosun.Api.dll");
        File.Exists(dll).Should().BeTrue();
        var outputs = new[] { Path.GetTempPath(), repoRoot, Path.Combine(repoRoot, "apps", "backend", "src") }
            .Select(cwd => RunPrintConfig(dll, cwd))
            .ToList();

        outputs.Should().OnlyContain(o => o == outputs[0]);
        outputs[0].Should().Contain($"root={repoRoot}").And.Contain($"db={Path.Combine(repoRoot, "data", "db", "bosun.db")}");
    }

    private static string RunPrintConfig(string dll, string workingDirectory)
    {
        var start = new ProcessStartInfo("dotnet", $"\"{dll}\" --print-config")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment.Remove("DB_CONNECTION");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000).Should().BeTrue();
        process.ExitCode.Should().Be(0, process.StandardError.ReadToEnd());
        return output.Trim().Replace("\r\n", "\n");
    }
}

public class DatabaseTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Connections_use_wal_and_a_5s_busy_timeout_and_the_fts_table_exists()
    {
        app.CreateClient(); // boot the host
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();

        (await db.Database.SqlQuery<string>($"SELECT journal_mode AS Value FROM pragma_journal_mode").SingleAsync()).Should().Be("wal");
        (await db.Database.SqlQuery<int>($"SELECT timeout AS Value FROM pragma_busy_timeout").SingleAsync()).Should().Be(5000);
        (await db.Database.SqlQuery<int>($"SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync()).Should().Be(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*) AS Value FROM tag_fts WHERE tag_fts MATCH '\"ME-EXH\"'").SingleAsync()).Should().Be(9);
        (await db.Database.SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type='trigger' AND name LIKE 'tags_fts_%' ORDER BY name").ToListAsync())
            .Should().Equal("tags_fts_ad", "tags_fts_ai", "tags_fts_au");
        (await db.Database.SqlQuery<string>($"SELECT sql AS Value FROM sqlite_master WHERE name='tag_fts'").SingleAsync()).Should().Contain("trigram");
    }

    [Fact]
    public async Task Database_file_is_the_one_the_test_configured_and_unique_indexes_hold()
    {
        app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        db.Database.GetConnectionString().Should().Contain(app.DbPath);

        db.Tags.Add(new Bosun.Domain.Entities.Tag { ProjectId = 1, TagName = "ME-EXH-TT01", System = "ME", Equipment = "EXH" });
        var duplicate = async () => await db.SaveChangesAsync();
        (await duplicate.Should().ThrowAsync<DbUpdateException>()).Which.InnerException!.Message.Should().Contain("UNIQUE");
    }

    [Fact]
    public async Task Timestamps_round_trip_as_utc_and_versions_start_at_one()
    {
        app.CreateClient();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
        var project = await db.Projects.AsNoTracking().SingleAsync();
        project.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        project.Version.Should().Be(1);

        var latest = await db.Projects.OrderByDescending(p => p.CreatedAt).Select(p => p.CreatedAt).FirstAsync();
        latest.Should().Be(project.CreatedAt);
    }
}
