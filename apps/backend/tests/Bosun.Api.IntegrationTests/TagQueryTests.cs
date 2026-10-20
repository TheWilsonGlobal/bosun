using System.Collections.Immutable;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Rules;

namespace Bosun.Api.IntegrationTests;

public class TagQueryTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Seed_has_5000_unique_tags_across_the_six_systems()
    {
        var client = app.CreateClient();
        var projects = await (await client.GetAsync("/api/projects")).ReadAsync<List<ProjectDto>>();
        projects.Should().ContainSingle().Which.Should().Match<ProjectDto>(p => p.VesselName == "MV Ha Long" && p.TagCount == 5000);

        var facets = await (await client.GetAsync("/api/projects/1/tag-facets")).ReadAsync<TagFacetsDto>();
        facets.Systems.Should().BeEquivalentTo("ME", "AE", "BLR", "PMS", "BWTS", "TANK");
        facets.SignalTypes.Should().BeEquivalentTo([SignalType.AI, SignalType.AO, SignalType.DI, SignalType.DO]);
        facets.AlarmGroups.Should().Contain("ME-EXH").And.HaveCountGreaterThan(20);

        var seen = new HashSet<string>();
        var errors = new List<string>();
        for (var page = 1; page <= 5; page++)
        {
            var result = await client.TagsAsync($"?pageSize=1000&page={page}");
            result.Total.Should().Be(5000);
            foreach (var tag in result.Items)
            {
                seen.Add(tag.TagName).Should().BeTrue($"{tag.TagName} is duplicated");
                var snapshot = new TagSnapshot
                {
                    TagName = tag.TagName,
                    SignalType = tag.SignalType,
                    Unit = tag.Unit,
                    RangeLow = tag.RangeLow,
                    RangeHigh = tag.RangeHigh,
                    Limits = tag.AlarmLimits.ToImmutableSortedDictionary(l => l.Level, l => new AlarmLimitValue(l.Value, l.DelayS, l.Hysteresis)),
                };
                errors.AddRange(TagRules.Validate(snapshot).Where(v => v.Severity == Severity.Error).Select(v => $"{tag.TagName}: {v.Code}"));
            }
        }

        seen.Should().HaveCount(5000);
        errors.Should().BeEmpty("the seed must satisfy the same rules the configurator enforces");
    }

    [Fact]
    public async Task Seed_contains_the_demo_exhaust_sensors_without_alarms_and_some_tags_with_alarms()
    {
        var client = app.CreateClient();
        for (var cylinder = 1; cylinder <= 8; cylinder++)
        {
            var tag = await client.TagAsync($"ME-EXH-TT{cylinder:00}");
            tag.AlarmLimits.Should().BeEmpty();
            tag.AlarmGroup.Should().BeNull();
            tag.Unit.Should().Be("°C");
        }

        (await client.TagsAsync("?hasAlarm=true&pageSize=1")).Total.Should().BeGreaterThan(1000);
        (await client.TagsAsync("?hasAlarm=false&pageSize=1")).Total.Should().BeGreaterThan(100);
        (await client.TagsAsync("?safetyClass=SIL,ESD&pageSize=1")).Total.Should().BeGreaterThan(20);
    }

    [Fact]
    public async Task Paging_returns_items_total_page_and_pageSize()
    {
        var client = app.CreateClient();
        var first = await client.TagsAsync("?pageSize=25&page=1");
        var second = await client.TagsAsync("?pageSize=25&page=2");
        var last = await client.TagsAsync("?pageSize=25&page=200");

        first.Should().Match<PagedResult<TagDto>>(p => p.Total == 5000 && p.Page == 1 && p.PageSize == 25 && p.Items.Count == 25);
        second.Items.Select(t => t.Id).Should().NotIntersectWith(first.Items.Select(t => t.Id));
        last.Items.Should().HaveCount(25);
        (await client.TagsAsync("?pageSize=25&page=201")).Items.Should().BeEmpty();
        (await client.TagsAsync()).PageSize.Should().Be(50);
    }

    [Fact]
    public async Task Sorting_asc_desc_and_dash_prefix()
    {
        var client = app.CreateClient();
        var asc = (await client.TagsAsync("?sort=tagName&order=asc&pageSize=100")).Items.Select(t => t.TagName).ToList();
        var desc = (await client.TagsAsync("?sort=tagName&order=desc&pageSize=100")).Items.Select(t => t.TagName).ToList();
        var dash = (await client.TagsAsync("?sort=-tagName&pageSize=100")).Items.Select(t => t.TagName).ToList();

        asc.Should().BeInAscendingOrder(StringComparer.Ordinal);
        desc.Should().BeInDescendingOrder(StringComparer.Ordinal);
        dash.Should().Equal(desc);

        var byHigh = (await client.TagsAsync("?sort=rangeHigh&order=desc&signalType=AI&pageSize=50")).Items.Select(t => t.RangeHigh).ToList();
        byHigh.Should().BeInDescendingOrder();

        var byGroup = (await client.TagsAsync("?sort=alarmGroup&alarmGroup=ME-JCW,ME-LUB&pageSize=100")).Items.Select(t => t.AlarmGroup).ToList();
        byGroup.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("ME-EXH", 9)]
    [InlineData("me-exh", 9)]
    [InlineData("EXH-TT0", null)]
    [InlineData("Exhaust gas temperature cyl 3", null)]
    [InlineData("ME-EXH-TT01", 1)]
    [InlineData("ME", null)]
    [InlineData("ME-", null)]
    [InlineData("zzzz-no-such-tag", 0)]
    public async Task Filter_matches_substrings_case_insensitively_via_fts(string filter, int? expectedTotal)
    {
        var client = app.CreateClient();
        var result = await client.TagsAsync($"?filter={Uri.EscapeDataString(filter)}&pageSize=1000");

        if (expectedTotal is { } total)
        {
            result.Total.Should().Be(total);
        }
        else
        {
            result.Total.Should().BeGreaterThan(0);
        }

        result.Items.All(t =>
            t.TagName.Contains(filter, StringComparison.OrdinalIgnoreCase) || (t.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("every hit must contain the literal text");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"ME\" OR \"AE\"")]
    [InlineData("ME* NOT AE")]
    [InlineData("tag_name:ME-EXH")]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("50%")]
    public async Task Filter_treats_fts_and_like_syntax_as_plain_text(string filter)
    {
        var client = app.CreateClient();
        var response = await client.GetAsync($"/api/projects/1/tags?filter={Uri.EscapeDataString(filter)}");
        var result = await response.ReadAsync<PagedResult<TagDto>>();
        result.Items.All(t =>
            t.TagName.Contains(filter, StringComparison.OrdinalIgnoreCase) || (t.Description ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("every hit must contain the literal text");
    }

    [Fact]
    public async Task Column_filters_combine()
    {
        var client = app.CreateClient();

        var me = await client.TagsAsync("?system=ME&pageSize=1000");
        me.Items.Should().OnlyContain(t => t.System == "ME");

        var meTemperatures = await client.TagsAsync("?system=ME&unit=%C2%B0C&signalType=AI&pageSize=1000");
        meTemperatures.Total.Should().BeLessThan(me.Total).And.BeGreaterThan(50);
        meTemperatures.Items.Should().OnlyContain(t => t.System == "ME" && t.Unit == "°C" && t.SignalType == SignalType.AI);

        var multi = await client.TagsAsync("?system=ME,AE&pageSize=1000");
        multi.Items.Select(t => t.System).Distinct().Should().BeEquivalentTo("ME", "AE");

        var withGroup = await client.TagsAsync("?alarmGroup=ME-JCW&pageSize=1000");
        withGroup.Items.Should().OnlyContain(t => t.AlarmGroup == "ME-JCW").And.NotBeEmpty();

        var none = await client.TagsAsync("?alarmGroup=none&system=ME&pageSize=1000");
        none.Items.Should().OnlyContain(t => t.AlarmGroup == null).And.NotBeEmpty();

        var alarmed = await client.TagsAsync("?system=ME&hasAlarm=true&pageSize=1000");
        alarmed.Items.Should().OnlyContain(t => t.HasAlarm && t.AlarmLimits.Count > 0);
        var plain = await client.TagsAsync("?system=ME&hasAlarm=false&pageSize=1000");
        plain.Items.Should().OnlyContain(t => !t.HasAlarm && t.AlarmLimits.Count == 0);
        (alarmed.Total + plain.Total).Should().Be(me.Total);

        var unbound = await client.TagsAsync("?bound=false&pageSize=1000");
        unbound.Items.Should().OnlyContain(t => t.ChannelId == null).And.NotBeEmpty();
        (await client.TagsAsync("?bound=true&pageSize=5")).Items.Should().OnlyContain(t => t.ChannelId != null && t.Channel != null);

        (await client.TagsAsync("?filter=ME-EXH&hasAlarm=false&signalType=AI")).Total.Should().Be(9);
        (await client.TagsAsync("?safetyClass=SIL&pageSize=1000")).Items.Should().OnlyContain(t => t.SafetyClass == SafetyClass.SIL);

        var controllers = await (await client.GetAsync("/api/projects/1/controllers")).ReadAsync<List<ControllerDto>>();
        controllers.Should().HaveCountGreaterThan(5);
        var onController = await client.TagsAsync($"?controllerId={controllers[0].Id}&pageSize=1000");
        onController.Total.Should().BeGreaterThan(0);
        onController.Items.Should().OnlyContain(t => t.Channel!.StartsWith(controllers[0].Name + "/"));
    }

    [Fact]
    public async Task Get_single_tag_and_reference_lists()
    {
        var client = app.CreateClient();
        var listed = await client.TagAsync("ME-LUB-PT01");
        var single = await (await client.GetAsync($"/api/projects/1/tags/{listed.Id}")).ReadAsync<TagDto>();
        single.Should().BeEquivalentTo(listed);
        single.SafetyClass.Should().Be(SafetyClass.ESD);
        single.AlarmLimits.Select(l => l.Level).Should().Equal(AlarmLevel.LL, AlarmLevel.L);

        var groups = await (await client.GetAsync("/api/projects/1/alarm-groups")).ReadAsync<List<AlarmGroupDto>>();
        groups.Should().Contain(g => g.Name == "ME-EXH" && g.TagCount == 0);
        groups.Single(g => g.Name == "ME-JCW").TagCount.Should().Be(10);

        var controllers = await (await client.GetAsync("/api/projects/1/controllers")).ReadAsync<List<ControllerDto>>();
        var module = controllers.SelectMany(c => c.Modules).First(m => m.BoundChannelCount > 0);
        var channels = await (await client.GetAsync($"/api/projects/1/modules/{module.Id}/channels")).ReadAsync<List<ChannelDto>>();
        channels.Should().HaveCount(module.ChannelCount);
        channels.Count(c => c.BoundTag is not null).Should().Be(module.BoundChannelCount);
    }

    [Fact]
    public async Task Errors_are_problem_details_with_stable_codes()
    {
        var client = app.CreateClient();

        (await (await client.GetAsync("/api/projects/999/tags")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.GetAsync("/api/projects/1/tags/999999")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.GetAsync("/api/projects/1/tags?sort=nope")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.GetAsync("/api/projects/1/tags?page=0")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.GetAsync("/api/projects/1/tags?pageSize=1001")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.GetAsync("/api/projects/1/tags?signalType=XX")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.GetAsync("/api/projects/1/tags?order=sideways")).ReadProblemAsync(400)).Code.Should().Be("invalid_request");
        (await (await client.GetAsync("/api/projects/abc/tags")).ReadProblemAsync(404)).Code.Should().Be("not_found");
        (await (await client.GetAsync("/api/nope")).ReadProblemAsync(404)).Code.Should().Be("not_found");
    }

    [Fact]
    public async Task Health_endpoints_and_openapi_are_up()
    {
        var client = app.CreateClient();
        foreach (var path in new[] { "/health", "/api/health" })
        {
            var response = await client.GetAsync(path);
            response.IsSuccessStatusCode.Should().BeTrue();
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("\"status\":\"ok\"").And.Contain("\"databaseProvider\":\"sqlite\"");
        }

        var openApi = await client.GetAsync("/openapi/v1.json");
        openApi.IsSuccessStatusCode.Should().BeTrue();
        (await openApi.Content.ReadAsStringAsync()).Should().Contain("/api/projects/{projectId}/tags");
        (await client.GetAsync("/scalar")).IsSuccessStatusCode.Should().BeTrue();
    }
}
