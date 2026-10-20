using Bosun.Application;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Api.IntegrationTests;

public class ChangeSetLifecycleTests(TestApp app) : IClassFixture<TestApp>
{
    private static async Task<ChangeSetDto> CreateAsync(HttpClient client, string title, params object[] ops) =>
        await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title, ops })).ReadAsync<ChangeSetDto>();

    [Fact]
    public async Task Inline_edit_goes_through_the_same_change_set_path_and_writes_audit_rows()
    {
        var client = await app.LoginAsync();
        var before = await client.TagAsync("ME-EXH-TT01");

        var draft = await CreateAsync(client, "Edit ME-EXH-TT01 description",
            new { type = "updateTag", tagName = "ME-EXH-TT01", field = "description", @new = "Exhaust gas temp cyl 1 (zebrafish)" });

        draft.Status.Should().Be(ChangeSetStatus.Draft);
        draft.Source.Should().Be(ChangeSetSource.Manual);
        draft.CreatedBy.Should().Be(TestApp.AdminUser);
        var op = draft.Ops.Should().ContainSingle().Which.Should().BeOfType<UpdateTagOp>().Subject;
        op.Old.Should().Be(before.Description, "an omitted old is filled in from the current value");

        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.Should().Match<ValidationResultDto>(v => v.Valid && v.Status == ChangeSetStatus.Validated && !v.RequiresSecondApprover);

        var approved = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>();
        approved.Status.Should().Be(ChangeSetStatus.Approved);
        approved.ApprovedBy.Should().Be(TestApp.AdminUser);

        var applied = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadAsync<ChangeSetDto>();
        applied.Status.Should().Be(ChangeSetStatus.Applied);
        applied.AppliedBy.Should().Be(TestApp.AdminUser);
        applied.AppliedAt.Should().NotBeNull();

        var after = await client.TagAsync("ME-EXH-TT01");
        after.Description.Should().Be("Exhaust gas temp cyl 1 (zebrafish)");
        after.Version.Should().Be(before.Version + 1, "apply bumps the integer concurrency token");

        var audit = await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={draft.Id}")).ReadAsync<List<AuditEntryDto>>();
        var entry = audit.Should().ContainSingle().Which;
        entry.Should().Match<AuditEntryDto>(a =>
            a.Actor == TestApp.AdminUser && a.ApprovedBy == TestApp.AdminUser && a.ActorType == ActorType.User && a.TagName == "ME-EXH-TT01"
            && a.OpType == "updateTag" && a.ChangeSetId == draft.Id && a.ProjectId == 1);
        entry.Diff!["field"]!.GetValue<string>().Should().Be("description");
        entry.Diff["old"]!.GetValue<string>().Should().Be(before.Description);
        entry.Diff["new"]!.GetValue<string>().Should().Be("Exhaust gas temp cyl 1 (zebrafish)");
        entry.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        (DateTime.UtcNow - entry.Timestamp).Should().BeLessThan(TimeSpan.FromMinutes(1));

        // the FTS5 index is kept in sync by triggers: the new word is findable, the old description is not
        (await client.TagsAsync("?filter=zebrafish")).Items.Should().ContainSingle().Which.TagName.Should().Be("ME-EXH-TT01");
    }

    [Fact]
    public async Task The_demo_instruction_as_ops_alarm_limit_plus_group_for_all_exhaust_sensors()
    {
        var client = await app.LoginAsync();
        var ops = Enumerable.Range(1, 8).SelectMany(i => new object[]
        {
            new { type = "setAlarmLimit", tagName = $"ME-EXH-TT{i:00}", level = "H", @new = new { value = 95, delayS = 10, hysteresis = 1 } },
            new { type = "assignGroup", tagName = $"ME-EXH-TT{i:00}", @new = "ME-EXH" },
        }).ToArray();

        var applied = await client.ApplyOpsAsync("High exhaust gas temperature alarms", ops);
        applied.Status.Should().Be(ChangeSetStatus.Applied);

        var tags = await client.TagsAsync("?filter=ME-EXH-TT&hasAlarm=true");
        tags.Items.Select(t => t.TagName).Should().BeEquivalentTo(Enumerable.Range(1, 8).Select(i => $"ME-EXH-TT{i:00}"));
        tags.Items.Should().OnlyContain(t => t.AlarmGroup == "ME-EXH" && t.AlarmLimits.Single().Value == 95 && t.AlarmLimits.Single().DelayS == 10);

        var audit = await (await client.GetAsync($"/api/projects/1/audit-entries?changeSetId={applied.Id}")).ReadAsync<List<AuditEntryDto>>();
        audit.Should().HaveCount(16);
    }

    [Fact]
    public async Task Validation_errors_keep_the_change_set_in_draft_and_block_approval()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "Bad limits",
            new { type = "setAlarmLimit", tagName = "ME-EXH-PT01", level = "H", @new = new { value = 3.9, delayS = 5, hysteresis = 0.1 } },
            new { type = "setAlarmLimit", tagName = "ME-EXH-PT01", level = "HH", @new = new { value = 3.5, delayS = -2, hysteresis = 0.1 } });

        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.Valid.Should().BeFalse();
        validation.Status.Should().Be(ChangeSetStatus.Draft);
        validation.Violations.Select(v => v.Code).Should().BeEquivalentTo(ViolationCodes.LimitOrder, ViolationCodes.DelayInvalid);
        validation.Violations.Should().OnlyContain(v => v.TagName == "ME-EXH-PT01" && v.Severity == Severity.Error && v.Message.Length > 0);

        var (code, _) = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadProblemAsync(409);
        code.Should().Be("invalid_state");

        var diff = await (await client.GetAsync($"/api/change-sets/{draft.Id}/diff")).ReadAsync<DiffDto>();
        diff.Rows.Should().HaveCount(2);
        diff.Rows[0].Should().Match<DiffRowDto>(r => r.Field == "limits.H" && r.Old == "(none)" && r.New!.StartsWith("3.9"));
        diff.Violations.Should().HaveCount(2);

        // fix it by editing: append an op that repairs the order -> back to a valid draft
        await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/ops", new
        {
            ops = new object[] { new { type = "setAlarmLimit", tagName = "ME-EXH-PT01", level = "HH", @new = new { value = 3.95, delayS = 2, hysteresis = 0.1 } } },
        })).ReadAsync<ChangeSetDto>();
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>()).Valid.Should().BeTrue();
    }

    [Fact]
    public async Task Adding_ops_to_a_validated_or_approved_change_set_resets_it_to_draft()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "Two steps",
            new { type = "updateTag", tagName = "ME-EXH-TT02", field = "description", @new = "step one" });
        await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate");
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Approved);

        var edited = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/ops", new
        {
            ops = new object[] { new { type = "updateTag", tagName = "ME-EXH-TT03", field = "description", @new = "step two" } },
        })).ReadAsync<ChangeSetDto>();

        edited.Status.Should().Be(ChangeSetStatus.Draft);
        edited.ApprovedBy.Should().BeNull("an edit invalidates earlier approvals");
        edited.Ops.Should().HaveCount(2);
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadProblemAsync(409)).Code.Should().Be("invalid_state");
    }

    [Fact]
    public async Task State_machine_rejects_out_of_order_transitions()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "Order",
            new { type = "updateTag", tagName = "ME-EXH-TT04", field = "description", @new = "order test" });

        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadProblemAsync(409)).Code.Should().Be("invalid_state");
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadProblemAsync(409)).Code.Should().Be("invalid_state");

        var rejected = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/reject", new { reason = "not needed" })).ReadAsync<ChangeSetDto>();
        rejected.Should().Match<ChangeSetDto>(c => c.Status == ChangeSetStatus.Rejected && c.RejectReason == "not needed" && c.RejectedBy == TestApp.AdminUser);

        foreach (var action in new[] { "validate", "approve", "apply", "reject" })
        {
            (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/{action}")).ReadProblemAsync(409)).Code.Should().Be("invalid_state", action);
        }

        var addOps = await client.PostJsonAsync($"/api/change-sets/{draft.Id}/ops", new { ops = new object[] { new { type = "deleteTag", tagName = "ME-EXH-TT04" } } });
        (await addOps.ReadProblemAsync(409)).Code.Should().Be("invalid_state");

        // a rejected change set never touched the tag
        (await client.TagAsync("ME-EXH-TT04")).Description.Should().NotBe("order test");

        // reject without a body is fine too
        var second = await CreateAsync(client, "Reject without body",
            new { type = "updateTag", tagName = "ME-EXH-TT04", field = "description", @new = "x" });
        var response = await client.PostAsync($"/api/change-sets/{second.Id}/reject", content: null);
        (await response.ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Rejected);
    }

    [Fact]
    public async Task Applied_change_sets_cannot_be_applied_twice_and_listing_filters_by_status()
    {
        var client = await app.LoginAsync();
        var applied = await client.ApplyOpsAsync("Once",
            new { type = "updateTag", tagName = "ME-EXH-TT05", field = "description", @new = "applied once" });
        (await (await client.PostJsonAsync($"/api/change-sets/{applied.Id}/apply")).ReadProblemAsync(409)).Code.Should().Be("invalid_state");

        var list = await (await client.GetAsync("/api/projects/1/change-sets?status=applied")).ReadAsync<PagedResult<ChangeSetSummaryDto>>();
        list.Items.Should().Contain(c => c.Id == applied.Id && c.OpCount == 1).And.OnlyContain(c => c.Status == ChangeSetStatus.Applied);
        (await (await client.GetAsync("/api/projects/1/change-sets?status=bogus")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");

        var diff = await (await client.GetAsync($"/api/change-sets/{applied.Id}/diff")).ReadAsync<DiffDto>();
        diff.Status.Should().Be(ChangeSetStatus.Applied);
        diff.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task At_most_200_ops_per_change_set()
    {
        var client = await app.LoginAsync();
        var names = (await client.TagsAsync("?system=TANK&pageSize=201")).Items.Select(t => t.TagName).ToList();
        names.Should().HaveCount(201);

        object Op(string name) => new { type = "updateTag", tagName = name, field = "description", @new = "bulk " + name };

        var tooMany = await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "Too big", ops = names.Select(Op).ToArray() });
        var (code, body) = await tooMany.ReadProblemAsync(422);
        code.Should().Be("too_many_ops");
        body.GetProperty("max").GetInt32().Should().Be(200);

        var draft = await CreateAsync(client, "Exactly 200", names.Take(200).Select(Op).ToArray());
        draft.Ops.Should().HaveCount(200);
        var more = await client.PostJsonAsync($"/api/change-sets/{draft.Id}/ops", new { ops = new[] { Op(names[200]) } });
        (await more.ReadProblemAsync(422)).Code.Should().Be("too_many_ops");

        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>()).Valid.Should().BeTrue();
        await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/approve")).ReadAsync<ChangeSetDto>();
        (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/apply")).ReadAsync<ChangeSetDto>()).Status.Should().Be(ChangeSetStatus.Applied);
        (await client.TagAsync(names[199])).Description.Should().Be("bulk " + names[199]);
    }

    [Fact]
    public async Task Create_tag_with_limit_then_delete_it_again()
    {
        var client = await app.LoginAsync();
        var applied = await client.ApplyOpsAsync("New sensor",
            new { type = "createTag", tagName = "ME-EXH-TT09", description = "Exhaust gas temp turbocharger inlet (quokka)", signalType = "AI", unit = "°C", rangeLow = 0, rangeHigh = 700, safetyClass = "NONE", alarmGroup = "ME-EXH" },
            new { type = "setAlarmLimit", tagName = "ME-EXH-TT09", level = "HH", @new = new { value = 650, delayS = 2, hysteresis = 5 } });
        applied.Status.Should().Be(ChangeSetStatus.Applied);

        var created = await client.TagAsync("ME-EXH-TT09");
        created.Should().Match<TagDto>(t =>
            t.System == "ME" && t.Equipment == "EXH" && t.AlarmGroup == "ME-EXH" && t.Version == 1 && t.RangeHigh == 700 && t.ChannelId == null);
        created.AlarmLimits.Should().ContainSingle().Which.Value.Should().Be(650);
        (await client.TagsAsync("?filter=quokka")).Total.Should().Be(1);

        var deleted = await client.ApplyOpsAsync("Remove it", new { type = "deleteTag", tagName = "ME-EXH-TT09" });
        var op = deleted.Ops.Should().ContainSingle().Which.Should().BeOfType<DeleteTagOp>().Subject;
        op.Old.Should().NotBeNull("delete records the full snapshot so it can be reverted");
        op.Old!.Limits.Should().ContainKey(AlarmLevel.HH);
        op.Old.AlarmGroup.Should().Be("ME-EXH");

        (await client.TagsAsync("?filter=quokka")).Total.Should().Be(0, "the delete trigger removed it from the FTS index");
        (await client.TagsAsync("?filter=ME-EXH-TT09")).Total.Should().Be(0);
        (await (await client.GetAsync($"/api/change-sets/{deleted.Id}")).ReadAsync<ChangeSetDto>()).Ops[0].Should().BeOfType<DeleteTagOp>();
    }

    [Fact]
    public async Task Duplicate_names_and_unknown_references_are_rejected_by_validation()
    {
        var client = await app.LoginAsync();
        var draft = await CreateAsync(client, "Bad ops",
            new { type = "createTag", tagName = "ME-EXH-TT01", signalType = "AI", unit = "°C", rangeLow = 0, rangeHigh = 1 },
            new { type = "createTag", tagName = "ME-NEW-TT01", signalType = "AI", unit = "°C", rangeLow = 0, rangeHigh = 1 },
            new { type = "createTag", tagName = "ME-NEW-TT01", signalType = "AI", unit = "°C", rangeLow = 0, rangeHigh = 1 },
            new { type = "assignGroup", tagName = "ME-EXH-TT02", @new = "NO-SUCH-GROUP" },
            new { type = "updateTag", tagName = "NOPE-NOPE-TT01", field = "unit", @new = "bar" },
            new { type = "createTag", tagName = "bad name", signalType = "AI", unit = "furlongs", rangeLow = 5, rangeHigh = 1 });

        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.Valid.Should().BeFalse();
        validation.Violations.Select(v => v.Code).Should().Contain([
            ViolationCodes.DuplicateInProject, ViolationCodes.DuplicateInChangeSet, ViolationCodes.AlarmGroupUnknown, ViolationCodes.TagNotFound,
            ViolationCodes.TagNameConvention, ViolationCodes.UnitUnknown, ViolationCodes.RangeInvalid]);
    }

    [Fact]
    public async Task Bind_unbind_and_swap_channels_in_one_change_set()
    {
        var client = await app.LoginAsync();

        // two AI tags that sit on channels of the same controller, plus one free AI channel
        var bound = (await client.TagsAsync("?system=BLR&signalType=AI&bound=true&pageSize=2")).Items;
        var a = bound[0];
        var b = bound[1];
        a.ChannelId.Should().NotBe(b.ChannelId);

        var swap = await client.ApplyOpsAsync("Swap channels",
            new { type = "bindChannel", tagName = a.TagName, old = a.ChannelId, @new = (int?)null },
            new { type = "bindChannel", tagName = b.TagName, old = b.ChannelId, @new = a.ChannelId },
            new { type = "bindChannel", tagName = a.TagName, old = (int?)null, @new = b.ChannelId });
        swap.Status.Should().Be(ChangeSetStatus.Applied);

        (await client.TagAsync(a.TagName)).ChannelId.Should().Be(b.ChannelId);
        (await client.TagAsync(b.TagName)).ChannelId.Should().Be(a.ChannelId);

        // a channel that another tag owns cannot be taken
        var draft = await CreateAsync(client, "Steal", new { type = "bindChannel", tagName = a.TagName, @new = a.ChannelId });
        var validation = await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>();
        validation.Violations.Should().Contain(v => v.Code == ViolationCodes.ChannelInUse);
    }

    [Fact]
    public async Task Bind_an_unbound_tag_to_a_free_channel_of_its_type()
    {
        var client = await app.LoginAsync();
        var unbound = (await client.TagsAsync("?signalType=DI&bound=false&pageSize=1")).Items.Single();
        var controllers = await (await client.GetAsync("/api/projects/1/controllers")).ReadAsync<List<ControllerDto>>();

        ChannelDto? free = null;
        foreach (var module in controllers.SelectMany(c => c.Modules).Where(m => m.SignalType == SignalType.DI && m.BoundChannelCount < m.ChannelCount))
        {
            var channels = await (await client.GetAsync($"/api/projects/1/modules/{module.Id}/channels")).ReadAsync<List<ChannelDto>>();
            free = channels.FirstOrDefault(c => c.BoundTag is null);
            if (free is not null)
            {
                break;
            }
        }

        free.Should().NotBeNull();
        await client.ApplyOpsAsync("Bind", new { type = "bindChannel", tagName = unbound.TagName, @new = free!.Id });
        (await client.TagAsync(unbound.TagName)).ChannelId.Should().Be(free.Id);

        var wrongType = (await client.TagsAsync("?signalType=AI&bound=false&pageSize=1")).Items.FirstOrDefault();
        if (wrongType is not null)
        {
            var draft = await CreateAsync(client, "Wrong type", new { type = "bindChannel", tagName = wrongType.TagName, @new = free.Id });
            (await (await client.PostJsonAsync($"/api/change-sets/{draft.Id}/validate")).ReadAsync<ValidationResultDto>())
                .Violations.Should().Contain(v => v.Code == ViolationCodes.ChannelSignalMismatch || v.Code == ViolationCodes.ChannelInUse);
        }
    }

    [Fact]
    public async Task Malformed_requests_are_invalid_request_problems()
    {
        var client = await app.LoginAsync();

        (await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "", ops = Array.Empty<object>() })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "x", ops = new object[] { new { type = "explode", tagName = "A-B-CC01" } } })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "x", ops = new object[] { new { type = "updateTag", field = "unit" } } })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "x", ops = new object[] { "nope" } })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "x", source = "agent" })).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.PostJsonAsync("/api/projects/999/change-sets", new { title = "x" })).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.PostJsonAsync("/api/change-sets/999999/validate")).ReadProblemAsync(404)).Code.Should().Be("not_found");

        var badJson = await client.PostAsync("/api/projects/1/change-sets", new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        (await badJson.ReadProblemAsync(400)).Code.Should().Be("invalid_request");

        var wrongType = await client.PostAsync("/api/projects/1/change-sets", new StringContent("title=x", System.Text.Encoding.UTF8, "text/plain"));
        (await wrongType.ReadProblemAsync(415)).Code.Should().Be("unsupported_media_type");

        var emptyDraft = await CreateAsync(client, "Empty");
        emptyDraft.Ops.Should().BeEmpty();
        var empty = await (await client.PostJsonAsync($"/api/change-sets/{emptyDraft.Id}/validate")).ReadAsync<ValidationResultDto>();
        empty.Violations.Should().ContainSingle(v => v.Code == ViolationCodes.EmptyChangeSet);

        var extraction = await (await client.PostJsonAsync("/api/projects/1/change-sets", new { title = "From a PDF", source = "extraction" })).ReadAsync<ChangeSetDto>();
        extraction.Source.Should().Be(ChangeSetSource.Extraction);
    }
}
