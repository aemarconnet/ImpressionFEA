using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImpressionFEA.Core.Materials;

namespace ImpressionFEA.App.Infrastructure;

/// <summary>
/// Built-in materials plus the user's own, read from Documents\ImpressionFEA\materials.json (an array of materials in
/// the same flow-stress form σ̄ = C(T)·(ε₀+ε̄)ⁿ·ε̄̇ᵐ, C(T) = C_ref·exp(−β(T−T_ref)); MPa, °C, 1/s).
/// </summary>
public static class MaterialLibrary
{
    private sealed record UserMaterial
    {
        public string Name { get; init; } = "";

        public string Family { get; init; } = "My materials";

        public string Condition { get; init; } = "User-defined";

        public string Source { get; init; } = "";

        public double CRef { get; init; }

        public double N { get; init; }

        public double M { get; init; }

        public double Eps0 { get; init; } = 0.05;

        public double TRef { get; init; }

        public double Beta { get; init; }

        public double TMin { get; init; }

        public double TMax { get; init; }

        public double DefaultTemperature { get; init; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string UserFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ImpressionFEA", "materials.json");

    /// <summary>Built-in materials followed by the valid user materials; problems are reported in <paramref name="errors"/>.</summary>
    public static IReadOnlyList<MaterialPreset> Load(out List<string> errors)
    {
        errors = [];
        var all = new List<MaterialPreset>(MaterialPresets.All);
        if (!File.Exists(UserFile)) return all;
        List<UserMaterial>? user;
        try
        {
            user = JsonSerializer.Deserialize<List<UserMaterial>>(File.ReadAllText(UserFile), Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            errors.Add("materials.json could not be read: " + ex.Message);
            return all;
        }

        foreach (var u in user ?? [])
        {
            string? problem = string.IsNullOrWhiteSpace(u.Name) ? "a material has no name"
                : all.Any(m => string.Equals(m.Name, u.Name, StringComparison.OrdinalIgnoreCase)) ? $"\"{u.Name}\" is already defined"
                : u.CRef <= 0 ? $"\"{u.Name}\": cRef must be > 0 (MPa)"
                : u.N < 0 || u.N > 1 ? $"\"{u.Name}\": n must be within 0…1"
                : u.M < 0 || u.M > 1 ? $"\"{u.Name}\": m must be within 0…1"
                : u.Eps0 <= 0 ? $"\"{u.Name}\": eps0 must be > 0"
                : u.TMax <= u.TMin ? $"\"{u.Name}\": tMax must be above tMin"
                : null;
            if (problem is not null)
            {
                errors.Add("materials.json: " + problem + " — skipped.");
                continue;
            }

            double t = u.DefaultTemperature is var d && d >= u.TMin && d <= u.TMax ? d : (u.TMin + u.TMax) / 2;
            all.Add(new MaterialPreset(u.Name.Trim(), u.Condition, u.CRef, u.N, u.M, u.Eps0, u.TRef, u.Beta, u.TMin, u.TMax, t)
            {
                Family = string.IsNullOrWhiteSpace(u.Family) ? "My materials" : u.Family,
                Source = string.IsNullOrWhiteSpace(u.Source) ? "Your own data (materials.json)." : u.Source,
                IsUser = true,
            });
        }

        return all;
    }

    /// <summary>Creates the user materials file with one commented example if it does not exist; returns its path.</summary>
    public static string EnsureUserFile()
    {
        if (File.Exists(UserFile)) return UserFile;
        Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
        File.WriteAllText(UserFile, """
            // ImpressionFEA user materials. Flow stress: σ̄ = C(T)·(eps0 + ε̄)^n · ε̄̇^m  [MPa, ε̄̇ in 1/s]
            // with C(T) = cRef · exp(−beta · (T − tRef)), T in °C. tMin…tMax is the allowed forging window.
            // Fit cRef, n and m to your flow curves at tRef; fit beta from cRef at two temperatures:
            //   beta = ln(C(T1) / C(T2)) / (T2 − T1).
            // Save, then press "Reload materials" in ImpressionFEA (or restart it).
            [
              {
                "name": "Example 42CrMo4 (my supplier)",
                "family": "My materials",
                "condition": "Hot forging",
                "source": "Supplier flow curves, 2026 (where the numbers come from)",
                "cRef": 108, "n": 0.03, "m": 0.14, "eps0": 0.05,
                "tRef": 1100, "beta": 0.0032,
                "tMin": 900, "tMax": 1250, "defaultTemperature": 1150
              }
            ]
            """);
        return UserFile;
    }
}
