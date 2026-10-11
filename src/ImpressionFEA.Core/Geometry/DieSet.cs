namespace ImpressionFEA.Core.Geometry;

public enum Symmetry
{
    /// <summary>Solid of revolution about the z axis; model is the (r, z) meridian half-section.</summary>
    Axisymmetric,

    /// <summary>Long prismatic section (out-of-plane length L); model is the x &gt;= 0 half-section.</summary>
    PlaneStrain,
}

/// <summary>
/// A closed-die impression pair. Profiles are given in the closed (end-of-stroke) position; during the
/// stroke the top die sits at a vertical offset equal to the remaining die gap.
/// </summary>
public sealed class DieSet
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required Symmetry Symmetry { get; init; }

    public required DieSurface Bottom { get; init; }

    public required DieSurface TopClosed { get; init; }

    /// <summary>Outer radius (half-width) of the impression; the flash land starts here.</summary>
    public required double CavityOuterRadius { get; init; }

    /// <summary>Radial extent shown in the viewport (flash land + gutter).</summary>
    public required double ViewExtent { get; init; }

    public required double RecommendedBilletRadius { get; init; }

    public double RecommendedLength { get; init; } = 100;

    /// <summary>Billet volume / cavity volume used to size the recommended billet (excess goes to flash).</summary>
    public double VolumeFactor { get; init; } = 1.10;

    public required double RecommendedElementSize { get; init; }

    public required double RecommendedStrokeIncrement { get; init; }

    public bool IsPlaneStrain => Symmetry == Symmetry.PlaneStrain;

    /// <summary>Weight converting a meridian/section area element at abscissa x into volume (mm³ per mm²).</summary>
    public static double VolumeWeight(Symmetry symmetry, double x, double length) =>
        symmetry == Symmetry.Axisymmetric ? 2 * Math.PI * x : 2 * length;

    /// <summary>Closed impression volume inside the flash-land radius (mm³). Length is used for plane strain only.</summary>
    public double CavityVolume(double length)
    {
        const int n = 4000;
        double dx = CavityOuterRadius / n;
        double v = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (i + 0.5) * dx;
            double h = TopClosed.HeightAt(x) - Bottom.HeightAt(x);
            if (h > 0) v += h * VolumeWeight(Symmetry, x, length) * dx;
        }

        return v;
    }

    public double BilletVolume(double radius, double height, double length) =>
        Symmetry == Symmetry.Axisymmetric ? Math.PI * radius * radius * height : 2 * radius * height * length;

    public double RecommendedHeight(double radius, double length)
    {
        double perHeight = BilletVolume(radius, 1.0, length);
        return VolumeFactor * CavityVolume(length) / perHeight;
    }

    /// <summary>Billet seat height: the billet rests on the highest bottom-die point under its footprint.</summary>
    public double SeatHeight(double radius) => Bottom.HeightRange(0, radius).Max;

    /// <summary>Initial die gap so that the top die just touches the billet top.</summary>
    public double InitialGap(double radius, double height) =>
        SeatHeight(radius) + height - TopClosed.HeightRange(0, radius).Min;

    public override string ToString() => Name;
}
