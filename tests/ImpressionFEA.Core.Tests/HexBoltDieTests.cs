using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Tests;

public class HexBoltDieTests
{
    [Fact]
    public void DiesAreExactFlatAndCylindricalSolids()
    {
        var d = DiePresets3D.HexBoltHead();
        Assert.Equal(Symmetry3D.Quarter, d.Symmetry);
        Assert.Equal(1.6, d.FlashThickness);

        // Top die (closed): head top inside the hex, land over the 3/16" ring, gutter beyond.
        Assert.Equal(15.5, d.TopClosed.SurfaceZ(0, 0), 3);
        Assert.Equal(15.5, d.TopClosed.SurfaceZ(10, 5), 3);
        Assert.Equal(1.6, d.TopClosed.SurfaceZ(0, 19.05 + 2.4), 3); // middle of the land, across a flat
        Assert.Equal(6.6, d.TopClosed.SurfaceZ(0, 19.05 + 4.76 + 8), 3); // outer face beyond the gutter step

        // Bottom die: flat parting face, Ø1" bore 1-1/2" deep (with clearance), 45° chamfer.
        Assert.Equal(0, d.Bottom.SurfaceZ(20, 0), 3);
        Assert.Equal(-38.1, d.Bottom.SurfaceZ(0, 0), 3);
        Assert.Equal(-38.1, d.Bottom.SurfaceZ(12.7, 0), 3);
        Assert.Equal(-0.5, d.Bottom.SurfaceZ(13.0 + 0.5, 0), 1);

        // Cavity: hex head (drafted prism) + bore, within a few per cent of the hand calculation.
        double hexArea = 2 * Math.Sqrt(3) * 19.05 * 19.05;
        double expected = hexArea * (15.5 - 1.6) + hexArea * 1.6 + Math.PI * 13.0 * 13.0 * 38.1;
        Assert.InRange(d.CavityVolume(), 0.95 * expected, 1.02 * expected);

        // The 1" bar seats on the bore floor and the top die first touches it above the parting plane.
        var (r, h) = d.RecommendedBillet();
        var (seat, gap) = d.Place(r, h);
        Assert.Equal(-38.1, seat, 2);
        Assert.True(gap > 10, $"gap {gap}");
    }

    [Fact]
    public void FlashThicknessIsAParameter()
    {
        var d = DiePresets3D.HexBoltHead().WithFlashThickness!(3.2);
        Assert.Equal(3.2, d.FlashThickness);
        Assert.Equal(3.2, d.TopClosed.SurfaceZ(0, 19.05 + 2.4), 3);
        Assert.Equal("Hex bolt head 1\" (3D)", d.Name);
    }

    /// <summary>Die-edge detection finds the cavity-to-flash-land edge (top die) and the bore entry (bottom die), not the faceted cylinder.</summary>
    [Fact]
    public void SharpDieEdgesAreFound()
    {
        var d = DiePresets3D.HexBoltHead();
        var top = d.TopClosed.FeaturePoints(1.0, 20);
        var bottom = d.Bottom.FeaturePoints(1.0, 20);
        static double R(Vec3 p) => Math.Sqrt(p.X * p.X + p.Y * p.Y);
        Assert.Contains(top, p => p.Z > 1.5 && p.Z < 3.0 && R(p) > 18.5 && R(p) < 23.5); // hex wall into the flash land (fillet)
        Assert.Contains(bottom, p => Math.Abs(p.Z) < 1.1 && Math.Abs(R(p) - 13.5) < 1.0); // 45° chamfer at the bore entry
        Assert.DoesNotContain(bottom, p => p.Z < -5 && p.Z > -30 && Math.Abs(R(p) - 13.0) < 0.2); // bore wall facets (3°) are not edges
    }
}
