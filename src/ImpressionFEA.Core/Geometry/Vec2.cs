namespace ImpressionFEA.Core.Geometry;

/// <summary>2D vector in the (r, z) meridian plane or the (x, y) plane-strain section. Units: mm.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(double s, Vec2 a) => new(s * a.X, s * a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(s * a.X, s * a.Y);

    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;
    public double Length => Math.Sqrt(X * X + Y * Y);

    public Vec2 Normalized()
    {
        double l = Length;
        return l > 0 ? new Vec2(X / l, Y / l) : this;
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
