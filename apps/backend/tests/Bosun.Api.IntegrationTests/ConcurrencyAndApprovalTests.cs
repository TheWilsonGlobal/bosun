using Bosun.Application;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Api.IntegrationTests;

public class ConcurrencyTests(TestApp app) : IClassFixture<TestApp>
{
    private static async Task<ChangeSetDto> CreateAsync(HttpClient client, string title, params object[] ops) =>
        await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title, ops })).ReadAsync<ChangeSetDto>();

    private static async Task ApproveAsync(HttpClient client, int id)
    {
        (await (await client.PostJsonAsync($"/api/change-sets/{id}/validate")).ReadAsync<ValidationResultDto>()).Valid.Should().BeTrue();
        await (await client.PostJsonAsync($"/api/change-sets/{id}/approve")).ReadAsync<ChangeSetDto>();
    }

    [Fact]
    public async Task Second_change_set_on_the_same_field_conflicts_at_apply_with_409_and_detail()
    {
        var client = await app.LoginAsync();
        var original = (await client.TagAsync("ME-EXH-TT01")).RangeHigh;

        // two engineers draft an edit of the same field from the same starting point
        var first = await CreateAsync(client, "Range to 650",
            new { type = "updateTag", tagName = "ME-EXH-TT01", field = "rangeHigh", @new = "650" });
        var second = await CreateAsync(client, "Range to 700",
            new { type = "updateTag", tagName = "ME-EXH-TT01", field = "rangeHigh", @new = "700" });
        ((UpdateTagOp)second.Ops[0]).Old.Should().Be(original!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await ApproveAsync(client, first.Id);
        await ApproveAsync(client, second.Id);

        (await (await client.PostJsonAsync($"/api/change-sets/{first.Id}/apply")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Applied);
        (await client.TagAsync("ME-EXH-TT01")).RangeHigh.Should().Be(650);

        var conflict = await client.PostJsonAsync($"/api/change-sets/{second.Id}/apply");
        var (code, body) = await conflict.ReadProblemAsync(409);
        code.Should().Be("concurrency_conflict");
        var conflicts = body.GetProperty("conflicts");
        conflicts.GetArrayLength().Should().Be(1);
        conflicts[0].GetProperty("code").GetString().Should().Be(ViolationCodes.OldValueMismatch);
        conflicts[0].GetProperty("tagName").GetString().Should().Be("ME-EXH-TT01");
        conflicts[0].GetProperty("field").GetString().Should().Be("rangeHigh");

        // nothing from the losing change set was written, and it stays approved-but-unapplied
        (await client.TagAsync("ME-EXH-TT01")).RangeHigh.Should().Be(650);
        (await (await client.GetAsync($"/api/change-sets/{second.Id}")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Approved);
        (await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={second.Id}")).ReadAsync<List<AuditEntryDto>>()).Should().BeEmpty();

        // re-validating reports the stale op, and the owner can reject it
        var revalidated = await (await client.PostJsonAsync($"/api/change-sets/{second.Id}/validate")).ReadAsync<ValidationResultDto>();
        revalidated.Valid.Should().BeFalse();
        revalidated.Status.Should().Be(ChangeSetStatus.Draft);
        revalidated.Violations.Should().Contain(v => v.Code == ViolationCodes.OldValueMismatch);
    }

    [Fact]
    public async Task An_explicit_stale_old_value_from_the_grid_is_a_conflict()
    {
        var client = await app.LoginAsync();
        var tag = await client.TagAsync("ME-EXH-TT06");

        // the grid showed "stale description", but the tag has since changed
        var draft = await CreateAsync(client, "Stale grid edit",
            new { type = "updateTag", tagName = "ME-EXH-TT06", field = "description", old = "stale description", @new = "fresh" });
        await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate");
        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();

        validation.Valid.Should().BeFalse();
        validation.Violations.Should().ContainSingle(v => v.Code == ViolationCodes.OldValueMismatch && v.Field == "description");
        (await client.TagAsync("ME-EXH-TT06")).Description.Should().Be(tag.Description);
    }

    [Fact]
    public async Task A_stale_alarm_limit_old_value_conflicts_and_a_matching_one_applies()
    {
        var client = await app.LoginAsync();
        await client.ApplyOpsAsync("Set H",
            new { type = "setAlarmLimit", tagName = "ME-EXH-TT07", level = "H", @new = new { value = 90, delayS = 5, hysteresis = 1 } });

        var stale = await CreateAsync(client, "Stale limit",
            new { type = "setAlarmLimit", tagName = "ME-EXH-TT07", level = "H", old = (object?)null, @new = new { value = 99, delayS = 5, hysteresis = 1 } });
        (await (await client.PostJsonAsync($"/api/change-sets/{stale.Id}/validate")).ReadAsync<ValidationResultDto>())
            .Violations.Should().Contain(v => v.Code == ViolationCodes.OldValueMismatch && v.Field == "limits.H");

        var matching = await client.ApplyOpsAsync("Raise H",
            new { type = "setAlarmLimit", tagName = "ME-EXH-TT07", level = "H", old = new { value = 90, delayS = 5, hysteresis = 1 }, @new = new { value = 92, delayS = 5, hysteresis = 1 } });
        matching.Status.Should().Be(ChangeSetStatus.Applied);
        (await client.TagAsync("ME-EXH-TT07")).AlarmLimits.Single().Value.Should().Be(92);
    }

    [Fact]
    public async Task Concurrent_applies_are_serialised_and_exactly_one_wins()
    {
        var client = await app.LoginAsync();
        var ids = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var draft = await CreateAsync(client, $"Race {i}",
                new { type = "updateTag", tagName = "ME-EXH-TT08", field = "description", old = "race-start", @new = $"racer {i}" });
            ids.Add(draft.Id);
        }

        // make "race-start" the real current value
        await client.ApplyOpsAsync("Prime", new { type = "updateTag", tagName = "ME-EXH-TT08", field = "description", @new = "race-start" });

        foreach (var id in ids)
        {
            await client.PostJsonAsync($"/api/change-sets/{id}/validate");
            await client.PostJsonAsync($"/api/change-sets/{id}/approve");
        }

        var results = await Task.WhenAll(ids.Select(id => client.PostJsonAsync($"/api/change-sets/{id}/apply")));
        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);
        results.Where(r => !r.IsSuccessStatusCode).Should().OnlyContain(r => r.StatusCode == System.Net.HttpStatusCode.Conflict);
        (await client.TagAsync("ME-EXH-TT08")).Description.Should().StartWith("racer ");
    }
}

public class ApprovalTests(TestApp app) : IClassFixture<TestApp>
{
    private static async Task<ChangeSetDto> CreateAsync(HttpClient client, string title, params object[] ops) =>
        await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title, ops })).ReadAsync<ChangeSetDto>();

    [Fact]
    public async Task Normal_changes_need_one_human_approval()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "Plain", new { type = "updateTag", tagName = "ME-EXH-TT01", field = "description", @new = "plain" });
        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.RequiresSecondApprover.Should().BeFalse();
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Approved);
    }

    [Fact]
    public async Task SIL_tags_need_a_second_distinct_approver_with_the_approver_role()
    {
        var admin = await app.LoginAsync();
        var approver = await app.LoginApproverAsync();
        var sil = await admin.TagAsync("ME-SPD-ST01");
        sil.SafetyClass.Should().Be(SafetyClass.SIL);

        var draft = await CreateAsync(admin, "Tune overspeed alarm",
            new { type = "setAlarmLimit", tagName = "ME-SPD-ST01", level = "H", @new = new { value = 105, delayS = 5, hysteresis = 1 } });

        var validation = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.RequiresSecondApprover.Should().BeTrue();
        validation.Violations.Should().Contain(v => v.Code == ViolationCodes.RequiresSecondApprover && v.Severity == Severity.Info);

        // first approval: recorded, but not enough
        var first = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>();
        first.Status.Should().Be(ChangeSetStatus.Validated);
        first.PendingSecondApproval.Should().BeTrue();
        first.ApprovedBy.Should().Be(TestApp.AdminUser);

        // not appliable yet
        (await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadProblemAsync(409)).Code.Should().Be("invalid_state");

        // the same person cannot approve twice
        var same = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadProblemAsync(403);
        same.Code.Should().Be("second_approver_required");
        same.Body.GetProperty("reason").GetString().Should().Be("same_user");

        // a different user with the approver role completes it
        var second = await (await approver.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>();
        second.Status.Should().Be(ChangeSetStatus.Approved);
        second.SecondApprovedBy.Should().Be(TestApp.ApproverUser);
        second.PendingSecondApproval.Should().BeFalse();

        var applied = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadAsync<ChangeSetDto>();
        applied.Status.Should().Be(ChangeSetStatus.Applied);
        (await admin.TagAsync("ME-SPD-ST01")).AlarmLimits.Should().Contain(l => l.Level == AlarmLevel.H && l.Value == 105);

        var audit = await (await admin.GetAsync($"/api/projects/1/audit-entries?changeSetId={draft.Id}")).ReadAsync<List<AuditEntryDto>>();
        audit.Should().ContainSingle().Which.ApprovedBy.Should().Be($"{TestApp.AdminUser}, {TestApp.ApproverUser}");
    }

    [Fact]
    public async Task A_second_approver_without_the_approver_role_is_refused()
    {
        var admin = await app.LoginAsync();
        var approver = await app.LoginApproverAsync();

        var draft = await CreateAsync(admin, "ESD tweak",
            new { type = "updateTag", tagName = "AE-G1LUB-PT01", field = "description", @new = "LO pressure shutdown, edited" });
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/validate");

        // the approver goes first, then the admin (no approver role) tries to be the second approver
        (await (await approver.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>()).PendingSecondApproval.Should().BeTrue();
        var refused = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadProblemAsync(403);
        refused.Code.Should().Be("approver_role_required");

        (await (await admin.GetAsync($"/api/change-sets/{draft.Id}")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Validated);
    }

    [Fact]
    public async Task Reclassifying_a_tag_to_SIL_or_creating_a_safety_tag_also_needs_two_approvers()
    {
        var admin = await app.LoginAsync();

        var upgrade = await CreateAsync(admin, "Upgrade", new { type = "updateTag", tagName = "ME-EXH-TT02", field = "safetyClass", @new = "SIL" });
        (await (await admin.PostJsonAsync($"/api/change-sets/{upgrade.Id}/validate")).ReadAsync<ValidationResultDto>()).RequiresSecondApprover.Should().BeTrue();

        var create = await CreateAsync(admin, "New ESD tag",
            new { type = "createTag", tagName = "ME-ESD-XS01", signalType = "DI", safetyClass = "ESD" });
        var result = await (await admin.PostJsonAsync($"/api/change-sets/{create.Id}/validate")).ReadAsync<ValidationResultDto>();
        result.Valid.Should().BeTrue();
        result.RequiresSecondApprover.Should().BeTrue();
    }

    [Fact]
    public async Task Editing_a_half_approved_change_set_discards_the_first_approval()
    {
        var admin = await app.LoginAsync();
        var draft = await CreateAsync(admin, "SIL edit", new { type = "updateTag", tagName = "ME-SPD-ST02", field = "description", @new = "overspeed 2" });
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/validate");
        await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/approve");

        var edited = await (await admin.PostJsonAsync($"/api/change-sets/{draft.Id}/ops", new
        {
            ops = new object[] { new { type = "updateTag", tagName = "ME-SPD-ST03", field = "description", @new = "overspeed 3" } },
        })).ReadAsync<ChangeSetDto>();

        edited.Status.Should().Be(ChangeSetStatus.Draft);
        edited.ApprovedBy.Should().BeNull();
        edited.PendingSecondApproval.Should().BeFalse();
    }
}
