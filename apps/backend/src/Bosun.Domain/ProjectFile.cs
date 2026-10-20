using System.Text.Json;
using Bosun.Domain.Entities;

namespace Bosun.Domain;

/// <summary>
/// The <c>.bosun.json</c> project file: what a desktop-era tool would save and open. It is a plain Domain model
/// (no database ids; channels are addressed as <c>controller/module/number</c>), so a desktop app and the web
/// backend read and write the very same document.
/// </summary>
public sealed record BosunProjectFile
{
    public const string FormatName = "bosun.project";
    public const int CurrentVersion = 1;

    public string Format { get; init; } = FormatName;
    public int FormatVersion { get; init; } = CurrentVersion;
    public required VesselFile Vessel { get; init; }
    public required ProjectInfoFile Project { get; init; }
    public List<AlarmGroupFile> AlarmGroups { get; init; } = [];
    public List<ControllerFile> Controllers { get; init; } = [];
    public List<TagFile> Tags { get; init; } = [];
}

public sealed record VesselFile(string Name, string? ImoNumber);

public sealed record ProjectInfoFile(string Name, string? Description, DateTime CreatedAt);

public sealed record AlarmGroupFile(string Name, string? Description);

public sealed record ControllerFile(string Name, string? Model, string? IpAddress, List<IoModuleFile> Modules);

/// <summary>A module with <c>ChannelCount</c> channels numbered 1..N, all of the module's signal type.</summary>
public sealed record IoModuleFile(string Name, int Slot, SignalType SignalType, int ChannelCount);

public sealed record TagFile
{
    public required string TagName { get; init; }
    public string? Description { get; init; }
    public SignalType SignalType { get; init; }
    public string? Unit { get; init; }
    public double? RangeLow { get; init; }
    public double? RangeHigh { get; init; }
    public SafetyClass SafetyClass { get; init; }
    public string? AlarmGroup { get; init; }

    /// <summary>"controller/module/number" or null.</summary>
    public string? Channel { get; init; }
    public List<AlarmLimitFile> AlarmLimits { get; init; } = [];
}

public sealed record AlarmLimitFile(AlarmLevel Level, double Value, double DelayS, double Hysteresis);

public static class ProjectFileSerializer
{
    private static readonly JsonSerializerOptions Indented = new(BosunJson.Options) { WriteIndented = true };

    public static string Serialize(BosunProjectFile file) => JsonSerializer.Serialize(file, Indented);

    public static BosunProjectFile Deserialize(string json)
    {
        var file = JsonSerializer.Deserialize<BosunProjectFile>(json, BosunJson.Options)
            ?? throw new JsonException("The file is empty.");
        if (file.Format != BosunProjectFile.FormatName)
        {
            throw new JsonException($"Not a Bosun project file (format '{file.Format}').");
        }

        if (file.FormatVersion != BosunProjectFile.CurrentVersion)
        {
            throw new JsonException($"Unsupported .bosun.json version {file.FormatVersion} (this build reads {BosunProjectFile.CurrentVersion}).");
        }

        return file;
    }
}

/// <summary>Converts between the entity graph and the file model. Pure: no EF, no I/O.</summary>
public static class ProjectFileMapper
{
    public static string ChannelLabel(string controller, string module, int number) => $"{controller}/{module}/{number}";

    /// <summary>Entity graph (vessel with its first project populated) to file. Requires forward navigations only.</summary>
    public static BosunProjectFile ToFile(Vessel vessel, Project project)
    {
        var labels = new Dictionary<Channel, string>(ReferenceEqualityComparer.Instance);
        foreach (var controller in project.Controllers)
        {
            foreach (var module in controller.Modules)
            {
                foreach (var channel in module.Channels)
                {
                    labels[channel] = ChannelLabel(controller.Name, module.Name, channel.Number);
                }
            }
        }

        return new BosunProjectFile
        {
            Vessel = new VesselFile(vessel.Name, vessel.ImoNumber),
            Project = new ProjectInfoFile(project.Name, project.Description, project.CreatedAt),
            AlarmGroups = project.AlarmGroups.Select(g => new AlarmGroupFile(g.Name, g.Description)).ToList(),
            Controllers = project.Controllers
                .Select(c => new ControllerFile(
                    c.Name,
                    c.Model,
                    c.IpAddress,
                    c.Modules.OrderBy(m => m.Slot).Select(m => new IoModuleFile(m.Name, m.Slot, m.SignalType, m.Channels.Count)).ToList()))
                .ToList(),
            Tags = project.Tags
                .Select(t => new TagFile
                {
                    TagName = t.TagName,
                    Description = t.Description,
                    SignalType = t.SignalType,
                    Unit = t.Unit,
                    RangeLow = t.RangeLow,
                    RangeHigh = t.RangeHigh,
                    SafetyClass = t.SafetyClass,
                    AlarmGroup = t.AlarmGroup?.Name,
                    Channel = t.Channel is null ? null : labels[t.Channel],
                    AlarmLimits = t.AlarmLimits.OrderBy(l => l.Level)
                        .Select(l => new AlarmLimitFile(l.Level, l.Value, l.DelayS, l.Hysteresis)).ToList(),
                })
                .ToList(),
        };
    }

    /// <summary>File to a fresh (untracked) entity graph. Throws <see cref="JsonException"/> for dangling references.</summary>
    public static Vessel ToGraph(BosunProjectFile file)
    {
        var project = new Project { Name = file.Project.Name, Description = file.Project.Description, CreatedAt = file.Project.CreatedAt };
        var vessel = new Vessel { Name = file.Vessel.Name, ImoNumber = file.Vessel.ImoNumber, Projects = [project] };

        var groups = new Dictionary<string, AlarmGroup>(StringComparer.Ordinal);
        foreach (var g in file.AlarmGroups)
        {
            var group = new AlarmGroup { Name = g.Name, Description = g.Description };
            groups[g.Name] = group;
            project.AlarmGroups.Add(group);
        }

        var channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
        foreach (var c in file.Controllers)
        {
            var controller = new Controller { Name = c.Name, Model = c.Model, IpAddress = c.IpAddress };
            foreach (var m in c.Modules)
            {
                var module = new IoModule { Name = m.Name, Slot = m.Slot, SignalType = m.SignalType };
                for (var n = 1; n <= m.ChannelCount; n++)
                {
                    var channel = new Channel { Number = n, SignalType = m.SignalType };
                    module.Channels.Add(channel);
                    channels[ChannelLabel(c.Name, m.Name, n)] = channel;
                }

                controller.Modules.Add(module);
            }

            project.Controllers.Add(controller);
        }

        foreach (var t in file.Tags)
        {
            var parsed = SplitName(t.TagName);
            var tag = new Tag
            {
                TagName = t.TagName,
                Description = t.Description,
                System = parsed.System,
                Equipment = parsed.Equipment,
                SignalType = t.SignalType,
                Unit = t.Unit,
                RangeLow = t.RangeLow,
                RangeHigh = t.RangeHigh,
                SafetyClass = t.SafetyClass,
            };

            if (t.AlarmGroup is { } groupName)
            {
                tag.AlarmGroup = groups.GetValueOrDefault(groupName) ?? throw new JsonException($"Tag '{t.TagName}' references unknown alarm group '{groupName}'.");
            }

            if (t.Channel is { } label)
            {
                tag.Channel = channels.GetValueOrDefault(label) ?? throw new JsonException($"Tag '{t.TagName}' references unknown channel '{label}'.");
            }

            tag.AlarmLimits.AddRange(t.AlarmLimits.Select(l => new AlarmLimit { Level = l.Level, Value = l.Value, DelayS = l.DelayS, Hysteresis = l.Hysteresis }));
            project.Tags.Add(tag);
        }

        return vessel;
    }

    /// <summary>
    /// SYSTEM and EQUIPMENT are the first two dash-separated segments. Names that do not follow the convention are
    /// still loadable (the rules engine reports them); they just get the whole name / empty equipment.
    /// </summary>
    private static (string System, string Equipment) SplitName(string tagName)
    {
        var parts = tagName.Split('-');
        return parts.Length >= 3 ? (parts[0], parts[1]) : (tagName, "");
    }
}
