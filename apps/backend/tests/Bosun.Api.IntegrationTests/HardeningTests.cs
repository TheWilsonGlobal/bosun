using System.Net;
using System.Text.Json;
using Bosun.Api.Auth;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Infrastructure.Configuration;
using Bosun.Rules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bosun.Api.IntegrationTests;

/// <summary>Reviewer findings on phases 0-2 that are visible through the HTTP API or the services.</summary>
public class ChangeSetHardeningTests(TestApp app) : IClassFixture<TestApp>
{
    private static async Task<ChangeSetDto> CreateAsync(HttpClient client, string title, params object[] ops) =>
        await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title, ops })).ReadAsync<ChangeSetDto>();

    private static async Task<ValidationResultDto> ValidateAsync(HttpClient client, int id) =>
        await (await client.PostJsonAsync($"/api/change-sets/{id}/validate")).ReadAsync<ValidationResultDto>();

    private static async Task<ChangeSetDto> GetAsync(HttpClient client, int id) =>
        await (await client.GetAsync($"/api/change-sets/{id}")).ReadAsync<ChangeSetDto>();

    [Fact]
    public async Task An_approved_change_set_that_later_touches_a_sil_tag_is_demoted_and_can_be_completed_by_a_second_approver()
    {
        var admin = await app.LoginAsync();
        var approver = await app.LoginApproverAsync();

        var ordinary = await CreateAsync(admin, "Plain edit",
            new { type = "updateTag", tagName = "ME-EXH-TT02", field = "description", @new = "edited while still ordinary" });
        ordinary.RequiresSecondApprover.Should().BeFalse();
        (await ValidateAsync(admin, ordinary.Id)).Valid.Should().BeTrue();
        (await (await admin.PostJsonAsync($"/api/change-sets/{ordinary.Id}/approve")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Approved);

        // Meanwhile the tag is reclassified SIL by another (fully approved) change set.
        var reclassify = await CreateAsync(admin, "Make it SIL",
            new { type = "updateTag", tagName = "ME-EXH-TT02", field = "safetyClass", @new = "SIL" });
        (await ValidateAsync(admin, reclassify.Id)).RequiresSecondApprover.Should().BeTrue();
        await admin.PostJsonAsync($"/api/change-sets/{reclassify.Id}/approve");
        await approver.PostJsonAsync($"/api/change-sets/{reclassify.Id}/approve");
        (await (await admin.PostJsonAsync($"/api/change-sets/{reclassify.Id}/apply")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Applied);

        // The old approval is no longer enough: apply refuses, and Approve cannot be reached because the status is Approved...
        var stuck = await admin.PostJsonAsync($"/api/change-sets/{ordinary.Id}/apply");
        (await stuck.ReadProblemAsync(403)).Code.Should().Be("second_approver_required");

        // ...so re-validating demotes it to Validated, keeps the first approval, and now flags the pending second approval.
        var revalidated = await ValidateAsync(admin, ordinary.Id);
        revalidated.Should().Match<ValidationResultDto>(v => v.Valid && v.RequiresSecondApprover && v.Status == ChangeSetStatus.Validated);
        var pending = await GetAsync(admin, ordinary.Id);
        pending.Should().Match<ChangeSetDto>(c => c.Status == ChangeSetStatus.Validated && c.PendingSecondApproval && c.ApprovedBy == TestApp.AdminUser);

        // the same user cannot complete it; the approver can, and then it applies
        (await (await admin.PostJsonAsync($"/api/change-sets/{ordinary.Id}/approve")).ReadProblemAsync(403)).Code.Should().Be("second_approver_required");
        var completed = await (await approver.PostJsonAsync($"/api/change-sets/{ordinary.Id}/approve")).ReadAsync<ChangeSetDto>();
        completed.Should().Match<ChangeSetDto>(c => c.Status == ChangeSetStatus.Approved && c.SecondApprovedBy == TestApp.ApproverUser);
        (await (await admin.PostJsonAsync($"/api/change-sets/{ordinary.Id}/apply")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Applied);
        (await admin.TagAsync("ME-EXH-TT02")).Description.Should().Be("edited while still ordinary");
    }

    [Fact]
    public async Task Revalidating_an_approved_set_that_still_needs_no_second_approver_stays_approved()
    {
        var admin = await app.LoginAsync();
        var draft = await CreateAsync(admin, "Plain edit 2",
            new { type = "updateTag", tagName = "ME-EXH-TT03", field = "description", @new = "plain" });
        await ValidateAsync(admin, draft.Id);
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve");

        (await ValidateAsync(admin, draft.Id)).Status.Should().Be(ChangeSetStatus.Approved);
    }

    [Theory]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":99,"unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":1,"unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":"1","unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":"XX","unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":"AI","safetyClass":7,"unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"createTag","tagName":"AE-DG1-TT09","signalType":"AI","safetyClass":"BOGUS","unit":"°C","rangeLow":0,"rangeHigh":100}""")]
    [InlineData("""{"type":"setAlarmLimit","tagName":"ME-EXH-TT01","level":9,"new":{"value":90,"delayS":1,"hysteresis":1}}""")]
    [InlineData("""{"type":"setAlarmLimit","tagName":"ME-EXH-TT01","level":"HHH","new":{"value":90,"delayS":1,"hysteresis":1}}""")]
    public async Task Undefined_or_numeric_enum_values_are_a_400_at_the_boundary(string opJson)
    {
        var client = await app.LoginAsync();
        var body = new StringContent("{\"title\":\"bad enum\",\"ops\":[" + opJson + "]}", System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/projects/1/change-sets", body);
        var (code, problem) = await response.ReadProblemAsync(400);

        code.Should().Be("invalid_request");
        problem.GetProperty("index").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Add_ops_also_refuses_numeric_enums_and_an_unknown_value_in_update_tag_is_a_422_with_the_stable_code()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "enum draft", new { type = "updateTag", tagName = "ME-EXH-TT04", field = "description", @new = "x" });

        var numeric = new StringContent("""{"ops":[{"type":"createTag","tagName":"AE-DG1-TT09","signalType":99}]}""", System.Text.Encoding.UTF8, "application/json");
        (await (await client.PostAsync($"/api/change-sets/{draft.Id}/ops", numeric)).ReadProblemAsync(400)).Code.Should().Be("invalid_request");

        var update = await CreateAsync(client, "enum update", new { type = "updateTag", tagName = "ME-EXH-TT05", field = "signalType", @new = "99" });
        var validation = await ValidateAsync(client, update.Id);
        validation.Valid.Should().BeFalse();
        validation.Violations.Should().Contain(v => v.Code == ViolationCodes.InvalidValue && v.Field == "signalType");

        var approve = await client.PostJsonAsync($"/api/change-sets/{update.Id}/approve");
        (await approve.ReadProblemAsync(409)).Code.Should().Be("invalid_state");
    }

    [Fact]
    public async Task An_undefined_enum_built_in_process_is_refused_before_it_can_be_stored()
    {
        var client = await app.LoginAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var svc = scope.ServiceProvider.GetRequiredService<ChangeSetService>();
        var user = new UserContext(TestApp.AdminUser, new HashSet<string>());
        var op = new CreateTagOp { TagName = "AE-DG1-TT10", SignalType = (SignalType)99, Unit = "°C", RangeLow = 0, RangeHigh = 100 };

        var create = async () => await svc.CreateAsync(1, new CreateChangeSetRequest("in-process", ChangeSetSource.Manual, [new OpInput(op, false)]), user, CancellationToken.None);
        (await create.Should().ThrowAsync<AppException>()).Which.Should().Match<AppException>(e => e.Kind == ErrorKind.Validation && e.Code == "invalid_request" && e.Message.Contains("signalType"));

        var draft = await CreateAsync(client, "valid draft", new { type = "updateTag", tagName = "ME-EXH-TT10", field = "description", @new = "x" });
        var add = async () => await svc.AddOpsAsync(draft.Id, [new OpInput(op, false)], user, CancellationToken.None);
        await add.Should().ThrowAsync<AppException>();

        (await client.TagsAsync("?filter=AE-DG1-TT10")).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Retyping_a_channel_bound_tag_is_rejected_at_validation()
    {
        var client = await app.LoginAsync();
        var tag = await client.TagAsync("ME-EXH-TT06");
        tag.ChannelId.Should().NotBeNull("the seed wires the exhaust sensors");

        var draft = await CreateAsync(client, "Retype bound tag", new { type = "updateTag", tagName = "ME-EXH-TT06", field = "signalType", @new = "DI" });
        var validation = await ValidateAsync(client, draft.Id);

        validation.Valid.Should().BeFalse();
        validation.Violations.Should().Contain(v => v.Code == ViolationCodes.ChannelSignalMismatch && v.Field == "signalType");
    }

    [Fact]
    public async Task Seeded_tags_of_every_system_raise_no_unknown_system_warning()
    {
        var client = await app.LoginAsync();
        var names = new List<string>();
        foreach (var system in new[] { "ME", "AE", "BLR", "PMS", "BWTS", "TANK" })
        {
            names.Add((await client.TagsAsync($"?system={system}&pageSize=1")).Items.Single().TagName);
        }

        var draft = await CreateAsync(client, "Touch every system",
            names.Select(n => (object)new { type = "updateTag", tagName = n, field = "description", @new = "touched" }).ToArray());
        var validation = await ValidateAsync(client, draft.Id);
        validation.Violations.Should().NotContain(v => v.Code == ViolationCodes.UnknownSystem);
    }

    [Fact]
    public async Task Requires_second_approver_is_known_while_the_change_set_is_still_a_draft()
    {
        var client = await app.LoginAsync();

        var plain = await CreateAsync(client, "plain", new { type = "updateTag", tagName = "ME-EXH-TT07", field = "description", @new = "p" });
        plain.RequiresSecondApprover.Should().BeFalse();

        var sil = await CreateAsync(client, "SIL touch", new { type = "updateTag", tagName = "ME-SPD-ST01", field = "description", @new = "s" });
        sil.Should().Match<ChangeSetDto>(c => c.Status == ChangeSetStatus.Draft && c.RequiresSecondApprover);

        var grown = await (await client.PostJsonAsync($"/api/change-sets/{plain.Id}/ops",
            new { ops = new object[] { new { type = "updateTag", tagName = "ME-SPD-ST01", field = "description", @new = "s2" } } })).ReadAsync<ChangeSetDto>();
        grown.Should().Match<ChangeSetDto>(c => c.Status == ChangeSetStatus.Draft && c.RequiresSecondApprover);

        var list = await (await client.GetAsync("/api/projects/1/change-sets?pageSize=200")).ReadAsync<PagedResult<ChangeSetSummaryDto>>();
        list.Items.Single(c => c.Id == sil.Id).RequiresSecondApprover.Should().BeTrue();
    }

    [Fact]
    public async Task Agent_drafts_are_audited_with_the_human_applier_as_actor_and_the_agent_as_proposer()
    {
        var client = await app.LoginAsync();
        int id;
        int runId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            // change_sets.agent_run_id is a real foreign key now: the run has to exist.
            var db = scope.ServiceProvider.GetRequiredService<IBosunDbContext>();
            var run = new Bosun.Domain.Entities.AgentRun { ProjectId = 1, Message = "x", CreatedBy = "t", StartedAt = DateTime.UtcNow };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
            var svc = scope.ServiceProvider.GetRequiredService<ChangeSetService>();
            var op = new UpdateTagOp { TagName = "ME-EXH-TT08", Field = "description", New = "agent wrote this" };
            var created = await svc.CreateAsync(
                1,
                new CreateChangeSetRequest("Agent draft", ChangeSetSource.Agent, [new OpInput(op, false)], "model-x", "abcd1234", runId),
                new UserContext("agent-run", new HashSet<string>()),
                CancellationToken.None);
            id = created.Id;
        }

        var validation = await ValidateAsync(client, id);
        validation.Valid.Should().BeTrue(string.Join("; ", validation.Violations.Select(v => v.Code + " " + v.Message)));
        await client.PostJsonAsync($"/api/change-sets/{id}/approve");
        (await client.PostJsonAsync($"/api/change-sets/{id}/apply")).EnsureSuccessStatusCode();

        var raw = await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={id}")).Content.ReadAsStringAsync();
        var entry = JsonDocument.Parse(raw).RootElement.EnumerateArray().Single();
        entry.GetProperty("actorType").GetString().Should().Be("user");
        entry.GetProperty("proposedBy").GetString().Should().Be("agent");
        entry.GetProperty("actor").GetString().Should().Be(TestApp.AdminUser);
        entry.GetProperty("approvedBy").GetString().Should().Be(TestApp.AdminUser);
        entry.GetProperty("model").GetString().Should().Be("model-x");
        entry.GetProperty("promptVersion").GetString().Should().Be("abcd1234");
        entry.GetProperty("agentRunId").GetInt32().Should().Be(runId);

        // and a plain human edit says user/user
        var manual = await client.ApplyOpsAsync("manual", new { type = "updateTag", tagName = "ME-EXH-PT01", field = "description", @new = "human" });
        var manualEntry = (await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={manual.Id}")).ReadAsync<List<AuditEntryDto>>()).Single();
        manualEntry.Should().Match<AuditEntryDto>(a => a.ActorType == ActorType.User && a.ProposedBy == ActorType.User && a.Model == null);
    }

    [Theory]
    [InlineData("/api/projects/1/tags?page=1000001", 400)]
    [InlineData("/api/projects/1/tags?page=2147483647&pageSize=1000", 400)]
    [InlineData("/api/projects/1/tags?page=99999999999", 400)]
    [InlineData("/api/projects/1/tags?page=1000000&pageSize=1000", 200)]
    [InlineData("/api/projects/1/change-sets?page=1000001", 400)]
    [InlineData("/api/projects/1/change-sets?page=1000000&pageSize=200", 200)]
    public async Task Page_is_capped_so_the_offset_cannot_overflow(string url, int expected)
    {
        var client = await app.LoginAsync();
        var response = await client.GetAsync(url);
        ((int)response.StatusCode).Should().Be(expected, await response.Content.ReadAsStringAsync());
        if (expected == 400)
        {
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        }
    }
}

public class AuthHardeningTests
{
    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void An_empty_admin_password_in_the_env_file_is_treated_as_unset()
    {
        var root = Path.Combine(Path.GetTempPath(), "bosun-root-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Bosun.sln"), "");
            File.WriteAllText(Path.Combine(root, ".env"), "ADMIN_PASSWORD=\nADMIN_USERNAME=  \nAPPROVER_PASSWORD=\"\"\n");

            var configuration = new ConfigurationBuilder().AddBosunEnv(root).Build();

            configuration["Auth:AdminPassword"].Should().Be(BosunEnvironment.DefaultAdminPassword, "unset falls back to the documented default...");
            configuration["Auth:AdminUsername"].Should().Be("admin");

            // ...which every non-Development environment refuses
            var act = () => AuthSetup.EnsureSafeCredentials(configuration, new FakeEnv("Production"));
            act.Should().Throw<InvalidOperationException>().WithMessage("*ADMIN_PASSWORD*");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_blank_configured_password_never_logs_anyone_in()
    {
        using var app = new CustomApp("Development", new Dictionary<string, string?>
        {
            ["Auth:AdminPassword"] = "",
            ["Auth:ApproverPassword"] = "",
            ["Auth:ApproverUsername"] = "",
        });
        var client = app.CreateClient();

        foreach (var user in new[] { "admin", "approver", "" })
        {
            var response = await client.PostJsonAsync("/api/auth/login", new { username = user, password = "" });
            (await response.ReadProblemAsync(401)).Code.Should().Be("invalid_credentials");
        }

        var store = app.Services.GetRequiredService<UserStore>();
        store.Validate("admin", "").Should().BeNull();
        store.Validate("admin", null).Should().BeNull();
        store.Users.Should().NotContain(u => u.Password.Length == 0);
    }

    [Fact]
    public async Task The_session_cookie_is_secure_outside_development_only()
    {
        using var production = new CustomApp("Production", new Dictionary<string, string?> { ["Auth:AdminPassword"] = "a-real-production-secret" });
        using var development = new CustomApp("Development");

        var secure = production.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://127.0.0.1") });
        var prod = await secure.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "a-real-production-secret" });
        prod.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().Contain("; secure").And.Contain("httponly").And.Contain("samesite=strict");

        // even if the request arrives as plain http (TLS terminated by nginx), the cookie is still marked Secure
        var plain = await production.CreateClient().PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = "a-real-production-secret" });
        plain.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().Contain("; secure");

        var dev = await development.CreateClient().PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword });
        dev.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant().Should().NotContain("; secure");
    }

    [Fact]
    public void Forwarded_proto_and_for_headers_are_honoured_from_a_trusted_proxy()
    {
        using var app = new CustomApp("Development");
        app.CreateClient();
        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedProto).And.HaveFlag(ForwardedHeaders.XForwardedFor);
        options.KnownProxies.Should().NotBeEmpty("only the loopback proxy is trusted by default");
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client_and_other_endpoints_are_not()
    {
        using var app = new CustomApp("Development", new Dictionary<string, string?>
        {
            ["Auth:LoginRateLimit:PermitLimit"] = "3",
            ["Auth:LoginRateLimit:WindowSeconds"] = "60",
        });
        var client = app.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            (await (await client.PostJsonAsync("/api/auth/login", new { username = "admin", password = "wrong" })).ReadProblemAsync(401)).Code.Should().Be("invalid_credentials");
        }

        var limited = await client.PostJsonAsync("/api/auth/login", new { username = TestApp.AdminUser, password = TestApp.AdminPassword });
        (await limited.ReadProblemAsync(429)).Code.Should().Be("login_rate_limited");
        limited.Headers.RetryAfter.Should().NotBeNull();

        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_names_the_environment_only_in_development()
    {
        using var development = new CustomApp("Development");
        using var production = new CustomApp("Production", new Dictionary<string, string?> { ["Auth:AdminPassword"] = "a-real-production-secret" });

        var dev = JsonDocument.Parse(await development.CreateClient().GetStringAsync("/api/health")).RootElement;
        dev.GetProperty("environment").GetString().Should().Be("Development");

        foreach (var path in new[] { "/api/health", "/health" })
        {
            var text = await production.CreateClient().GetStringAsync(path);
            var prod = JsonDocument.Parse(text).RootElement;
            prod.TryGetProperty("environment", out _).Should().BeFalse(text);
            text.Should().NotContain("Production");
            prod.GetProperty("status").GetString().Should().Be("ok");
        }
    }
}
