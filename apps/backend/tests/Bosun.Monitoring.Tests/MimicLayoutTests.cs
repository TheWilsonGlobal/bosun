using System.Text.Json;
using Bosun.Infrastructure.Configuration;

namespace Bosun.Monitoring.Tests;

/// <summary>seed/mimic.json is hand-placed coordinates: this keeps two live widgets from being drawn on top of each other.</summary>
public class MimicLayoutTests
{
    private sealed record Box(string Id, double X, double Y, double Width, double Height)
    {
        public bool Overlaps(Box other) =>
            X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
    }

    private static (double Width, double Height, List<Box> Bindings) Load()
    {
        var root = RepoRoot.Resolve();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "seed", "mimic.json")));
        var view = document.RootElement.GetProperty("viewBox");
        var boxes = document.RootElement.GetProperty("bindings").EnumerateArray()
            .Select(b => new Box(
                b.GetProperty("id").GetString()!,
                b.GetProperty("x").GetDouble(),
                b.GetProperty("y").GetDouble(),
                b.GetProperty("width").GetDouble(),
                b.GetProperty("height").GetDouble()))
            .ToList();
        return (view.GetProperty("width").GetDouble(), view.GetProperty("height").GetDouble(), boxes);
    }

    [Fact]
    public void No_two_binding_rectangles_overlap()
    {
        var (_, _, boxes) = Load();
        boxes.Should().HaveCountGreaterThan(30);

        var overlaps = boxes
            .SelectMany((a, i) => boxes.Skip(i + 1).Where(a.Overlaps).Select(b => $"{a.Id} x {b.Id}"))
            .ToList();

        overlaps.Should().BeEmpty();
    }

    [Fact]
    public void Every_binding_rectangle_fits_inside_the_viewBox()
    {
        var (width, height, boxes) = Load();

        boxes.Where(b => b.Width <= 0 || b.Height <= 0 || b.X < 0 || b.Y < 0 || b.X + b.Width > width || b.Y + b.Height > height)
            .Select(b => b.Id).Should().BeEmpty();
    }

    [Fact]
    public void Binding_ids_are_unique()
    {
        var (_, _, boxes) = Load();

        boxes.GroupBy(b => b.Id).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty();
    }
}
