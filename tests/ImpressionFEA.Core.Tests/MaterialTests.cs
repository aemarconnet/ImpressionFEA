using ImpressionFEA.Core.Materials;

namespace ImpressionFEA.Core.Tests;

public class MaterialTests
{
    [Fact]
    public void PresetsAreConsistent()
    {
        var all = MaterialPresets.All;
        Assert.Same(MaterialPresets.Steel4140, all[0]); // the default material
        Assert.Equal(all.Count, all.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var m in all)
        {
            Assert.True(m.CRef > 0 && m.N is >= 0 and < 1 && m.M is > 0 and < 1 && m.Eps0 > 0, m.Name);
            Assert.True(m.TMin < m.TMax && m.InRange(m.DefaultTemperature), m.Name);
            // Nominal: in the upper three quarters of the forging window, never at its very top.
            Assert.True(m.DefaultTemperature >= m.TMin + 0.25 * (m.TMax - m.TMin) && m.DefaultTemperature < m.TMax, m.Name);
            Assert.NotEqual("Other", m.Family);
            double s = m.At(m.DefaultTemperature).Stress(0.5, 1);
            Assert.True(s is > 0.01 and < 2000, $"{m.Name}: {s} MPa");
        }
    }

    [Theory]
    [InlineData("AISI 4140", "AISI 4140 alloy steel")]
    [InlineData("6061-T6", "6061-T6 aluminium")]
    [InlineData("C11000", "C11000 ETP copper")]
    [InlineData("Modeling plasticine", "Modeling plasticine")]
    public void DemoNamesResolveToTheSameMaterial(string wanted, string expected)
    {
        // Setups match the first name equal to, or starting with, the stored name.
        var m = MaterialPresets.All.First(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase)
            || x.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected, m.Name);
    }

    [Fact]
    public void HotFlowStressOrdersAsExpected()
    {
        double At(string prefix)
        {
            var m = MaterialPresets.All.First(x => x.Name.StartsWith(prefix, StringComparison.Ordinal));
            return m.At(m.DefaultTemperature).Stress(0.5, 1);
        }

        Assert.True(At("AISI D2") > At("AISI 304") && At("AISI 304") > At("AISI 1045"));
        Assert.True(At("AISI 1045") > At("6061-T6") && At("6061-T6") > At("Modeling plasticine"));
    }

    /// <summary>
    /// The fitted presets reproduce Altan, Ngaile &amp; Shen (ASM 2005) Tables 4.7–4.9 (C in ksi × 6.895 = flow stress
    /// at 1/s) within the scatter of a single exponential temperature fit.
    /// </summary>
    [Theory]
    [InlineData("AISI 1045", 1100, 0.4, 15.5)]
    [InlineData("AISI 1045", 1000, 0.4, 24.7)]
    [InlineData("AISI 1060", 1100, 0.5, 12.0)]
    [InlineData("AISI 1095", 1100, 0.5, 10.6)]
    [InlineData("AISI 4140", 1100, 0.5, 14.4)] // "alloy steel" row
    [InlineData("AISI 304", 1100, 0.5, 19.6)] // 302 SS
    [InlineData("AISI 410", 1000, 0.5, 16.0)] // 403 SS
    [InlineData("AISI D2", 1000, 0.5, 27.1)] // D3
    [InlineData("1100 commercially", 450, 0.512, 4.4)]
    [InlineData("7075", 400, 2.66, 9.7)]
    [InlineData("C11000", 750, 0.512, 8.5)] // 99.9+ % copper
    [InlineData("C37700", 600, 0.5, 8.5)] // CDA 365
    [InlineData("C37700", 800, 0.5, 2.1)]
    [InlineData("C28000", 600, 0.5, 5.4)] // CDA 280
    [InlineData("C28000", 800, 0.5, 2.8)]
    public void FittedPresetsMatchAltanTables(string prefix, double temperature, double strain, double cKsi)
    {
        var m = MaterialPresets.All.First(x => x.Name.StartsWith(prefix, StringComparison.Ordinal));
        double model = m.At(temperature).Stress(strain, 1);
        double table = cKsi * 6.895;
        Assert.True(Math.Abs(model / table - 1) < 0.15, $"{m.Name} at {temperature} °C, ε {strain}: model {model:F1} MPa, table {table:F1} MPa");
        Assert.Contains("Altan", m.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPresetStatesItsSource()
    {
        foreach (var m in MaterialPresets.All) Assert.False(string.IsNullOrWhiteSpace(m.Source), m.Name);
    }
}
