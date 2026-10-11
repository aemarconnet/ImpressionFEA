namespace ImpressionFEA.Core.Solver;

/// <summary>Principal values of symmetric stress tensors (for ductile-damage criteria).</summary>
public static class Principal
{
    /// <summary>Largest eigenvalue of a symmetric 3×3 tensor (closed form, trigonometric solution of the characteristic cubic).</summary>
    public static double Max3(double xx, double yy, double zz, double xy, double yz, double zx)
    {
        double p1 = xy * xy + yz * yz + zx * zx;
        double q = (xx + yy + zz) / 3;
        if (p1 < 1e-30 * (xx * xx + yy * yy + zz * zz + 1e-300)) return Math.Max(xx, Math.Max(yy, zz)); // diagonal
        double p2 = (xx - q) * (xx - q) + (yy - q) * (yy - q) + (zz - q) * (zz - q) + 2 * p1;
        double p = Math.Sqrt(p2 / 6);
        double bxx = (xx - q) / p, byy = (yy - q) / p, bzz = (zz - q) / p, bxy = xy / p, byz = yz / p, bzx = zx / p;
        double detB = bxx * (byy * bzz - byz * byz) - bxy * (bxy * bzz - byz * bzx) + bzx * (bxy * byz - byy * bzx);
        double r = Math.Clamp(detB / 2, -1, 1);
        double phi = Math.Acos(r) / 3;
        return q + 2 * p * Math.Cos(phi);
    }

    /// <summary>Largest principal value of an in-plane 2×2 tensor and an out-of-plane normal component (2D axisymmetric / plane strain).</summary>
    public static double Max2D(double xx, double yy, double xy, double zz)
    {
        double c = 0.5 * (xx + yy), r = Math.Sqrt(0.25 * (xx - yy) * (xx - yy) + xy * xy);
        return Math.Max(c + r, zz);
    }
}
