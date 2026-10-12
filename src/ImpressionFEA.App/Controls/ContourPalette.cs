using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.App.Controls;

/// <summary>How element results are coloured (as the SOLIDWORKS Simulation option of the same name).</summary>
public enum FieldAveraging
{
    /// <summary>Element values averaged to the nodes and blended across elements (smooth contours).</summary>
    Nodal,

    /// <summary>Each element in one flat colour from its own value (no averaging; shows jumps between elements).</summary>
    Element,
}

/// <summary>Banded contour palette and field helpers shared by the 2D section and 3D solid viewports.</summary>
public static class ContourPalette
{
    /// <summary>Element values mapped to [0, 1] of the colour range (clamped just inside, for texture lookup).</summary>
    public static double[] ElementFraction(ForgingSnapshot s, ResultField field, double lo, double hi)
    {
        var vals = s.Field(field);
        double inv = hi > lo ? 1.0 / (hi - lo) : 0;
        var t = new double[vals.Length];
        for (int e = 0; e < vals.Length; e++) t[e] = Math.Clamp((vals[e] - lo) * inv, 0.001, 0.999);
        return t;
    }

    public const int Bands = 12;

    /// <summary>Opaque Pbgra32 colours, low → high.</summary>
    public static int[] Colors { get; } = Build();

    public static Color ColorOf(int band)
    {
        int c = Colors[Math.Clamp(band, 0, Bands - 1)];
        return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }

    /// <summary>Bands × 1 pixel strip for texture-mapped contours (sample with nearest-neighbour scaling).</summary>
    public static BitmapSource Strip()
    {
        var bmp = BitmapSource.Create(Bands, 1, 96, 96, PixelFormats.Pbgra32, null, Colors, Bands * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Colour-bar label of an internal field value in the current display units (stress: ksi / MPa at fixed decimals).</summary>
    public static string Label(double v, ResultField field)
    {
        var u = ImpressionFEA.App.Infrastructure.Units.Current;
        return field is ResultField.VonMisesStress or ResultField.MeanStress
            ? u.S(v).ToString(u.StressFormat, CultureInfo.InvariantCulture)
            : Format(v);
    }

    public static string Format(double v)
    {
        double a = Math.Abs(v);
        return a >= 1000 ? v.ToString("F0", CultureInfo.InvariantCulture)
             : a >= 10 ? v.ToString("F1", CultureInfo.InvariantCulture)
             : a >= 0.01 || a == 0 ? v.ToString("F3", CultureInfo.InvariantCulture)
             : v.ToString("E1", CultureInfo.InvariantCulture);
    }

    /// <summary>Area-weighted average of element values at the nodes.</summary>
    public static double[] NodalField(StepSnapshot s, ResultField field)
    {
        var mesh = s.Mesh;
        var conn = mesh.Connectivity;
        var vals = s.Field(field);
        var sum = new double[mesh.NodeCount];
        var wsum = new double[mesh.NodeCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            int a = conn[4 * e], b = conn[4 * e + 1], c = conn[4 * e + 2], d = conn[4 * e + 3];
            double area = 0.5 * Math.Abs((s.X[c] - s.X[a]) * (s.Y[d] - s.Y[b]) - (s.X[d] - s.X[b]) * (s.Y[c] - s.Y[a])) + 1e-12;
            for (int k = 0; k < 4; k++)
            {
                int n = conn[4 * e + k];
                sum[n] += area * vals[e];
                wsum[n] += area;
            }
        }

        for (int n = 0; n < sum.Length; n++) sum[n] = wsum[n] > 0 ? sum[n] / wsum[n] : 0;
        return sum;
    }

    private static int[] Build()
    {
        var p = new int[Bands];
        for (int i = 0; i < Bands; i++)
        {
            double t = (i + 0.5) / Bands;
            double r = 0.1357 + t * (4.5974 - t * (42.3277 - t * (130.5887 - t * (150.5666 - t * 58.1375))));
            double g = 0.0914 + t * (2.1856 + t * (4.8052 - t * (14.0195 - t * (4.2109 + t * 2.7747))));
            double b = 0.1067 + t * (12.5925 - t * (60.1097 - t * (109.0745 - t * (88.5066 - t * 26.8183))));
            int R = (int)(Math.Clamp(r, 0, 1) * 255), G = (int)(Math.Clamp(g, 0, 1) * 255), B = (int)(Math.Clamp(b, 0, 1) * 255);
            p[i] = unchecked((int)0xFF000000) | (R << 16) | (G << 8) | B;
        }

        return p;
    }
}
