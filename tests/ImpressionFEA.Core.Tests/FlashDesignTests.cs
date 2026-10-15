using System.Globalization;
using System.Text.Json;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.ThreeD;
using Xunit.Abstractions;

namespace ImpressionFEA.Core.Tests;

/// <summary>
/// Built-in dies against the ASM flash guidance (Altan, Ngaile &amp; Shen, §14.4–14.5; see <see cref="FlashDesign"/>):
/// every flash land is within the survey's spread and the w/t = 2–4 range of the load data, on the thick side, and the
/// geometry matches the declared design.
/// </summary>
public class FlashDesignTests(ITestOutputHelper output)
{
    [Fact]
    public void ViereggeEquationsMatchHandCalculations()
    {
        // Eq 14.6: D = 40 mm → 0.017·40 + 1/√45 = 0.680 + 0.149 = 0.829 mm.
        Assert.Equal(0.829, FlashDesign.ViereggeThickness(40), 3);
        // Eq 14.7: D = 108, H = 24, Rh = 49 → 30 / ∛(108·(1 + 2·108²/(24·206))) = 30 / ∛617.8 = 3.52.
        Assert.Equal(3.52, FlashDesign.ViereggeRatio(108, 24, 49), 2);
    }

    [Fact]
    public void SurveyCurvesFollowFigure14_13()
    {
        // Digitised trend: 1.25 mm at 0.1 kg, 1.85 at 1 kg, 4.6 at 10 kg; w/t falls from 4.5 towards 2.
        Assert.Equal(1.25, FlashDesign.SurveyThickness(0.1), 2);
        Assert.Equal(1.85, FlashDesign.SurveyThickness(1), 2);
        Assert.Equal(4.6, FlashDesign.SurveyThickness(10), 2);
        Assert.InRange(FlashDesign.SurveyThickness(0.31), 1.40, 1.45);
        Assert.Equal(3.25, FlashDesign.SurveyRatio(1), 2);
        Assert.True(FlashDesign.SurveyRatio(0.2) > FlashDesign.SurveyRatio(10));
        for (double q = 0.1; q < 100; q *= 1.3)
            Assert.True(FlashDesign.SurveyThickness(q * 1.3) >= FlashDesign.SurveyThickness(q)); // grows with weight
    }

    [Fact]
    public void AssessmentFlagsFlashOutsideTheGuidance()
    {
        Assert.True(FlashDesign.Assess(new FlashLand(2.0, 6.0), 0.31).WithinGuideline);
        Assert.True(FlashDesign.Assess(new FlashLand(2.0, 6.0), 0.31).ThickSide);
        Assert.Contains(FlashDesign.Assess(new FlashLand(0.8, 5.8), 0.31).Issues, i => i.Contains("above 4", StringComparison.Ordinal)); // old thin case: w/t 7
        Assert.Contains(FlashDesign.Assess(new FlashLand(3.2, 4.76), 0.31).Issues, i => i.Contains("thicker than", StringComparison.Ordinal)); // old thick case
        Assert.Contains(FlashDesign.Assess(new FlashLand(2.0, 3.0), 0.31).Issues, i => i.Contains("below 2", StringComparison.Ordinal));
        Assert.False(FlashDesign.Assess(new FlashLand(1.4, 5.0), 0.31).ThickSide); // typical, not thick side
    }

    public static TheoryData<string> Presets2D() => [.. DiePresets.All.Where(d => d.Flash is not null).Select(d => d.Name)];

    [Theory]
    [MemberData(nameof(Presets2D))]
    public void Preset2DFlashIsOnTheThickSideOfTheGuidance(string name)
    {
        var die = DiePresets.All.Single(d => d.Name == name);
        var measured = FlashDesign.Measure(die)!;
        // A plane-strain section takes the flash of the whole part: the shank's flash is that of the 3D connecting rod.
        double kg = FlashDesign.SteelWeight(die.IsPlaneStrain ? DiePresets3D.ConnectingRod().CavityVolume() : die.CavityVolume(die.RecommendedLength));
        Check(name, die.Flash!, measured, kg);
    }

    public static TheoryData<string, double, double, double, double, double> Presets3D() => new()
    {
        // name, probe ray start (x, y), direction (dx, dy), length: from inside the impression across the land
        { "Hex bolt head 1\" (3D)", 0, 15, 0, 1, 30 },
        { "Connecting rod (3D)", 55, 7, 0, 1, 25 },
        { "Connecting rod (3D)", -24, 0, -1, 0, 20 },
        { "Gear blank (3D)", 53, 0, 1, 0, 30 },
    };

    [Theory]
    [MemberData(nameof(Presets3D))]
    public void Preset3DFlashIsOnTheThickSideOfTheGuidance(string name, double x, double y, double dx, double dy, double length)
    {
        var die = DiePresets3D.All.Single(d => d.Name == name);
        var measured = FlashDesign.Measure(die.Bottom, die.TopClosed, x, y, dx, dy, length)!;
        Check(name, die.Flash!, measured, FlashDesign.SteelWeight(die.CavityVolume()));
    }

    [Fact]
    public void EveryFlashDieDeclaresItsFlashAndUpsetDiesHaveNone()
    {
        Assert.All(DiePresets.All.Where(d => d.Name != "Simple upset block"), d => Assert.NotNull(d.Flash));
        Assert.All(DiePresets3D.All.Where(d => d.Name != "Simple upset block (3D)"), d => Assert.NotNull(d.Flash));
        Assert.Null(FlashDesign.Measure(DiePresets.All.Single(d => d.Name == "Simple upset block")));
        var block = DiePresets3D.SimpleUpsetBlock();
        Assert.Null(FlashDesign.Measure(block.Bottom, block.TopClosed, 10, 0, 1, 0, 40));
    }

    /// <summary>Demo 8 sweeps the hex flash thickness: every value stays within the guidance (survey spread, w/t 2–4).</summary>
    [Fact]
    public void FlashThicknessDemoStaysWithinTheGuidance()
    {
        string file = Path.Combine(WorkflowTests.RepoFolder("samples"), "demos", "08-flash-thickness.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var values = doc.RootElement.GetProperty("sweep").GetProperty("values").EnumerateArray().Select(v => double.Parse(v.GetString()!, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3, values.Count);
        var hex = DiePresets3D.HexBoltHead();
        double kg = FlashDesign.SteelWeight(hex.CavityVolume());
        foreach (double t in values)
        {
            var a = FlashDesign.Assess(hex.WithFlashThickness!(t).Flash!, kg);
            output.WriteLine("demo 8: " + a.Summary);
            Assert.True(a.WithinGuideline, a.Summary);
        }
    }

    private void Check(string name, FlashLand declared, FlashLand measured, double kg)
    {
        var a = FlashDesign.Assess(declared, kg);
        output.WriteLine($"{name}: measured {measured} · {a.Summary}");
        Assert.Equal(declared.Thickness, measured.Thickness, 2);
        Assert.InRange(measured.Width, 0.97 * declared.Width, 1.03 * declared.Width);
        Assert.True(a.WithinGuideline, a.Summary);
        Assert.True(a.ThickSide, a.Summary);
    }
}
