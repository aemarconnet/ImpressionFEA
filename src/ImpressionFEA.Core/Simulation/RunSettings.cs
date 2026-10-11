using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImpressionFEA.Core.Simulation;

/// <summary>
/// Every input of a run as a small JSON document (an "ImpressionFEA setup", *.ifea.json). Used to save and reopen
/// setups, for demos, for multi-run sweeps and for re-running an updated STEP file with identical settings. Kept
/// free of UI types so other front ends (a CAD add-in, a batch runner) can share it.
/// </summary>
public sealed record RunSettings
{
    public const string FileExtension = ".ifea.json";

    public int FormatVersion { get; init; } = 1;

    /// <summary>"2D" or "3D".</summary>
    public string Analysis { get; init; } = "3D";

    /// <summary>Built-in die set name (2D or 3D preset), when no STEP file is used.</summary>
    public string? Preset { get; init; }

    /// <summary>3-body STEP assembly; relative paths are resolved against the setup file's folder.</summary>
    public string? StepFile { get; init; }

    /// <summary>1-based body index of the moving top die in <see cref="StepFile"/> (null: the suggested die).</summary>
    public int? TopDieBody { get; init; }

    public string Material { get; init; } = "";

    public double Temperature { get; init; }

    public double Friction { get; init; } = 0.3;

    public double? BilletRadius { get; init; }

    public double? BilletHeight { get; init; }

    public double? BilletLength { get; init; }

    public double ElementSize { get; init; }

    public double StrokeIncrement { get; init; }

    public double RamSpeed { get; init; } = 50;

    public double PressCapacity { get; init; } = 1000;

    public string LinearBackend { get; init; } = "Auto";

    // advanced (3D)
    public bool AutoStep { get; init; } = true;

    public int MaxCutbacks { get; init; } = 5;

    public int RefinementLevels { get; init; } = 1;

    public bool RefineThinFlash { get; init; }

    public double RemeshQuality { get; init; } = 0.12;

    public int MaxNodeCount { get; init; } = ThreeD.SimulationConfig3D.DefaultMaxNodes;

    public int MaxIterations { get; init; } = 70;

    public double Tolerance { get; init; } = 1e-4;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static JsonSerializerOptions JsonOptions => Json;

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>Reads a setup; a relative <see cref="StepFile"/> is made absolute against the file's folder.</summary>
    public static RunSettings Load(string path)
    {
        var s = JsonSerializer.Deserialize<RunSettings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("The setup file is empty.");
        return s.ResolvedAgainst(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "");
    }

    public RunSettings ResolvedAgainst(string folder) =>
        StepFile is { Length: > 0 } f && !Path.IsPathRooted(f) ? this with { StepFile = Path.GetFullPath(Path.Combine(folder, f)) } : this;
}
