using System.IO;
using System.Text.Json;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Infrastructure;

/// <summary>Suggested parameter sweep of a demo (Multi-run).</summary>
public sealed record DemoSweep(string Parameter, IReadOnlyList<string> Values);

/// <summary>
/// A curated, self-describing example: a full setup (<see cref="RunSettings"/>), teaching instructions and an
/// optional suggested multi-run. Demos are JSON files in samples/demos (one per file, sorted by file name), so
/// instructors can add or edit them without rebuilding the app.
/// </summary>
public sealed record DemoDefinition
{
    public string Title { get; init; } = "";

    public string Level { get; init; } = "";

    public string Summary { get; init; } = "";

    public IReadOnlyList<string> Instructions { get; init; } = [];

    public RunSettings Setup { get; init; } = new();

    public DemoSweep? Sweep { get; init; }

    /// <summary>File the demo was read from (set by the loader).</summary>
    public string Source { get; init; } = "";

    public string Guide => string.Join(Environment.NewLine, Instructions);

    public override string ToString() => Title;
}

public static class DemoCatalog
{
    /// <summary>samples/demos next to the executable (copied there by the build).</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "samples", "demos");

    /// <summary>Loads every demo; files that fail to parse are reported in <paramref name="errors"/> and skipped.</summary>
    public static IReadOnlyList<DemoDefinition> Load(out IReadOnlyList<string> errors)
    {
        var list = new List<DemoDefinition>();
        var errs = new List<string>();
        if (Directory.Exists(Folder))
        {
            foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var d = JsonSerializer.Deserialize<DemoDefinition>(File.ReadAllText(file), RunSettings.JsonOptions)
                        ?? throw new InvalidDataException("empty file");
                    list.Add(d with { Setup = d.Setup.ResolvedAgainst(Folder), Source = file });
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
                {
                    errs.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        errors = errs;
        return list;
    }
}
