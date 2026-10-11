namespace ImpressionFEA.Core.ThreeD;

/// <summary>3D vector (mm). Z is the press (ram) axis.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(double s, Vec3 a) => new(s * a.X, s * a.Y, s * a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(s * a.X, s * a.Y, s * a.Z);

    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

    public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double LengthSquared => X * X + Y * Y + Z * Z;

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 0 ? new Vec3(X / l, Y / l, Z / l) : this;
    }

    public double this[int i] => i == 0 ? X : i == 1 ? Y : Z;

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + t * (b - a);

    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    /// <summary>Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    public static Vec3 ClosestOnTriangle(Vec3 p, Vec3 a, Vec3 b, Vec3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        double d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        double d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + (d1 / (d1 - d3)) * ab;
        var cp = p - c;
        double d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + (d2 / (d2 - d6)) * ac;
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + ((d4 - d3) / (d4 - d3 + (d5 - d6))) * (c - b);
        double denom = 1 / (va + vb + vc);
        return a + (vb * denom) * ab + (vc * denom) * ac;
    }
}
