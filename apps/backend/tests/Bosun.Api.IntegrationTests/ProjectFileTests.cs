using System.Text.Json;
using Bosun.Application;
using Bosun.Domain;
using Bosun.Infrastructure.Persistence;
using Bosun.Infrastructure.Seed;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Bosun.Api.IntegrationTests;

public class ProjectFileTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Export_download_is_a_valid_bosun_json_with_all_tags()
    {
        var client = app.CreateClient();
        var response = await client.GetAsync("/api/projects/1/export");
        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().EndWith(".bosun.json");

        var file = ProjectFileSerializer.Deserialize(await response.Content.ReadAsStringAsync());
        file.Format.Should().Be("bosun.project");
        file.Vessel.Name.Should().Be("MV Ha Long");
        file.Tags.Should().HaveCount(5000);
        file.Tags.Single(t => t.TagName == "ME-EXH-TT01").Should().Match<TagFile>(t => t.Channel != null && t.AlarmLimits.Count == 0);
        file.AlarmGroups.Should().Contain(g => g.Name == "ME-EXH");
        file.Controllers.Sum(c => c.Modules.Sum(m => m.ChannelCount)).Should().BeGreaterThanOrEqualTo(file.Tags.Count(t => t.Channel != null));
    }

    [Fact]
    public async Task Generated_seed_round_trips_file_to_database_to_file_unchanged()
    {
        var original = ProjectFileImporter.FromGenerator();
        var json = ProjectFileSerializer.Serialize(original);

        // file -> Domain -> a brand-new real SQLite file -> export again
        var dir = Path.Combine(Path.GetTempPath(), "bosun-tests", "roundtrip-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var target = new CustomApp("Development", new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = $"Data Source={Path.Combine(dir, "import.db")}",
                ["Seed:Enabled"] = "false",
            });

            using var client = target.CreateClient(); // boots: migrates the empty file
            await using (var scope = target.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BosunDbContext>();
                (await ProjectFileImporter.ImportIntoEmptyAsync(db, ProjectFileSerializer.Deserialize(json))).Should().Be(5000);

                var again = () => ProjectFileImporter.ImportIntoEmptyAsync(db, original);
                await again.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already contains a vessel*");
            }

            var exported = await client.GetStringAsync("/api/projects/1/export");
            JsonSerializer.Deserialize<JsonElement>(exported).ToString()
                .Should().Be(JsonSerializer.Deserialize<JsonElement>(json).ToString(), "export(import(file)) must equal file");

            // FTS index was populated by the triggers during the bulk import
            var tags = await (await client.GetAsync("/api/projects/1/tags?filter=ME-EXH")).ReadAsync<PagedResult<TagDto>>();
            tags.Total.Should().Be(9);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Wrong_format_or_dangling_references_are_rejected()
    {
        var file = ProjectFileImporter.FromGenerator(new GeneratorOptions(TargetTags: 50));

        var wrongFormat = () => ProjectFileSerializer.Deserialize(JsonSerializer.Serialize(new { format = "something-else", formatVersion = 1 }));
        wrongFormat.Should().Throw<JsonException>();

        var tooNew = () => ProjectFileSerializer.Deserialize(ProjectFileSerializer.Serialize(file with { FormatVersion = 99 }));
        tooNew.Should().Throw<JsonException>().WithMessage("*version 99*");

        var dangling = file with { Tags = [.. file.Tags, new TagFile { TagName = "X-Y-ZZ01", AlarmGroup = "NOPE" }] };
        var act = () => ProjectFileMapper.ToGraph(dangling);
        act.Should().Throw<JsonException>().WithMessage("*unknown alarm group 'NOPE'*");

        var badChannel = file with { Tags = [.. file.Tags, new TagFile { TagName = "X-Y-ZZ01", Channel = "NOPE/1/1" }] };
        var act2 = () => ProjectFileMapper.ToGraph(badChannel);
        act2.Should().Throw<JsonException>().WithMessage("*unknown channel*");
    }

    [Fact]
    public void Generator_is_deterministic()
    {
        var a = ProjectFileSerializer.Serialize(ProjectFileImporter.FromGenerator());
        var b = ProjectFileSerializer.Serialize(ProjectFileImporter.FromGenerator());
        a.Should().Be(b);
        ProjectFileImporter.FromGenerator(new GeneratorOptions(Seed: 7)).Tags.Select(t => t.Channel)
            .Should().NotEqual(ProjectFileImporter.FromGenerator().Tags.Select(t => t.Channel), "the seed controls which tags are left unbound");
    }
}
