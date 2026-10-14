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
    /// <summary>samples/demos next to the executable (copied there by the build; read-only when installed).</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "samples", "demos");

    /// <summary>The user's own and edited demos, Documents\ImpressionFEA\demos; a file here replaces the built-in one of the same name.</summary>
    public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImpressionFEA", "demos");

    /// <summary>Loads every demo; files that fail to parse are reported in <paramref name="errors"/> and skipped.</summary>
    public static IReadOnlyList<DemoDefinition> Load(out IReadOnlyList<string> errors)
    {
        var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Folder, UserFolder })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.GetFiles(folder, "*.json")) files[Path.GetFileName(file)] = file;
        }

        var list = new List<DemoDefinition>();
        var errs = new List<string>();
        foreach (var file in files.Values)
        {
            try
            {
                var d = JsonSerializer.Deserialize<DemoDefinition>(File.ReadAllText(file), RunSettings.JsonOptions)
                    ?? throw new InvalidDataException("empty file");
                list.Add(d with { Setup = Resolve(d.Setup, Path.GetDirectoryName(file)!), Source = file });
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                errs.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        errors = errs;
        return list;
    }

    /// <summary>A user demo's relative STEP path is tried next to it first, then against the built-in demos (bundled samples).</summary>
    private static RunSettings Resolve(RunSettings setup, string folder)
    {
        var s = setup.ResolvedAgainst(folder);
        if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase) && s.StepFile is { } p && !File.Exists(p)) s = setup.ResolvedAgainst(Folder);
        return s;
    }

    /// <summary>Creates <see cref="UserFolder"/> on first use, with a short read-me on adding and editing demos.</summary>
    public static string EnsureUserFolder()
    {
        if (Directory.Exists(UserFolder)) return UserFolder;
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, "README.txt"),
            "Your own ImpressionFEA demos (*.json, one per file). A file here with the same name as a built-in demo replaces it." + Environment.NewLine
            + "To edit a built-in demo, copy it from" + Environment.NewLine + "  " + Folder + Environment.NewLine
            + "into this folder and press Reload on the Demos tab. Relative stepFile paths (e.g. ../sample_gear_3bodies.step) still find the bundled samples." + Environment.NewLine);
        return UserFolder;
    }
}
