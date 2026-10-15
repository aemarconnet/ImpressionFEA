using System.Text.Json;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Tests;

public class WorkflowTests
{
    internal static string RepoFolder(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, name))) dir = dir.Parent;
        return dir is null ? "" : Path.Combine(dir.FullName, name);
    }

    [Fact]
    public void SetupRoundTripsWithRelativeStepPath()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ifea-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var s = new RunSettings
            {
                Analysis = "3D",
                StepFile = "parts/die.step",
                TopDieBody = 3,
                Material = "AISI 4140",
                Temperature = 1111,
                ElementSize = 3.5,
                RefinementLevels = 2,
                RefineThinFlash = true,
            };
            string path = Path.Combine(folder, "job" + RunSettings.FileExtension);
            s.Save(path);
            var back = RunSettings.Load(path);
            Assert.Equal(Path.GetFullPath(Path.Combine(folder, "parts/die.step")), back.StepFile);
            Assert.Equal(s with { StepFile = back.StepFile }, back);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ShippedDemosParseAndReferenceExistingGeometry()
    {
        string demos = Path.Combine(RepoFolder("samples"), "demos");
        var files = Directory.GetFiles(demos, "*.json");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()), file);
            Assert.True(root.GetProperty("instructions").GetArrayLength() > 0, file);
            var setup = root.GetProperty("setup").Deserialize<RunSettings>(RunSettings.JsonOptions)!.ResolvedAgainst(demos);
            Assert.Contains(MaterialPresets.All, m => m.Name.StartsWith(setup.Material, StringComparison.OrdinalIgnoreCase));
            if (setup.StepFile is { } step) Assert.True(File.Exists(step), $"{Path.GetFileName(file)}: missing {step}");
            else if (setup.Analysis == "2D") Assert.Contains(DiePresets.All, d => d.Name == setup.Preset);
            else Assert.Contains(DiePresets3D.All, d => d.Name == setup.Preset);
        }
    }

    [Fact]
    public void TelemetryCsvHasHeaderAndOneRowPerIncrement()
    {
        string path = Path.GetTempFileName();
        try
        {
            TelemetryCsv.Write(path, [new TelemetryRow(0, 0, 0, 10, 0, 5, 0, 0, 0, 0, 100, 50), new TelemetryRow(1, 0.01, 0.5, 9.5, 12.3, 6, -0.01, 90, 0.1, 7, 100, 50)], ["test run"]);
            var lines = File.ReadAllLines(path);
            Assert.Equal("# test run", lines[0]);
            Assert.Equal(TelemetryCsv.Header, lines[1]);
            Assert.Equal(4, lines.Length);
            Assert.StartsWith("1,0.01,0.5000,9.5000,12.300,", lines[3]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
