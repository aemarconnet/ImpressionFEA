using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Cad;

public enum UserBilletShape
{
    /// <summary>Round bar standing on end (axis = press axis).</summary>
    VerticalBar,

    /// <summary>Round bar lying flat; its axis is turned by <see cref="UserBillet.AngleDeg"/> about the press axis (0° = along x).</summary>
    HorizontalBar,

    /// <summary>Rectangular block (width along x, depth along y, height along z before rotation).</summary>
    Block,
}

/// <summary>
/// A workpiece defined by the user for die-only imports: shape, size, orientation (rotation about the press axis) and
/// plan position. Lengths in mm, in the die frame (press axis +z, origin on the bottom die's centre of gravity).
/// </summary>
public sealed record UserBillet
{
    public UserBilletShape Shape { get; init; } = UserBilletShape.VerticalBar;

    /// <summary>Bar diameter, mm.</summary>
    public double Diameter { get; init; } = 50;

    /// <summary>Bar length (height for a vertical bar), mm.</summary>
    public double Length { get; init; } = 80;

    /// <summary>Block size along its own x / y / z, mm.</summary>
    public double Width { get; init; } = 50;

    public double Depth { get; init; } = 50;

    public double Height { get; init; } = 50;

    /// <summary>Rotation about the press axis, degrees (horizontal bar and block).</summary>
    public double AngleDeg { get; init; }

    /// <summary>Plan position of the workpiece centre, mm.</summary>
    public double OffsetX { get; init; }

    public double OffsetY { get; init; }

    public double Volume => Shape == UserBilletShape.Block ? Width * Depth * Height : Math.PI * Diameter * Diameter / 4 * Length;

    /// <summary>Height of the workpiece along the press axis, mm.</summary>
    public double VerticalSize => Shape switch { UserBilletShape.VerticalBar => Length, UserBilletShape.HorizontalBar => Diameter, _ => Height };

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (Shape == UserBilletShape.Block)
        {
            if (!(Width > 0 && Depth > 0 && Height > 0)) e.Add("Workpiece block sizes must be > 0.");
        }
        else if (!(Diameter > 0 && Length > 0))
        {
            e.Add("Workpiece diameter and length must be > 0.");
        }

        return e;
    }

    /// <summary>Same shape scaled so its volume equals <paramref name="volume"/>: keeps the diameter (bars) or the plan size (block).</summary>
    public UserBillet WithVolume(double volume) => Shape == UserBilletShape.Block
        ? this with { Height = volume / (Width * Depth) }
        : this with { Length = volume / (Math.PI * Diameter * Diameter / 4) };

    /// <summary>Closed, outward-oriented triangulated solid with its lowest point at <paramref name="baseZ"/>.</summary>
    public TriMeshSolid ToSolid(double baseZ, int segments = 96)
    {
        double a = AngleDeg * Math.PI / 180, ca = Math.Cos(a), sa = Math.Sin(a);
        Vec3 Place(double x, double y, double z) => new(OffsetX + ca * x - sa * y, OffsetY + sa * x + ca * y, baseZ + z);
        var v = new List<Vec3>();
        var t = new List<int>();
        if (Shape == UserBilletShape.Block)
        {
            double hx = Width / 2, hy = Depth / 2;
            for (int k = 0; k < 8; k++) v.Add(Place((k & 1) == 0 ? -hx : hx, (k & 2) == 0 ? -hy : hy, (k & 4) == 0 ? 0 : Height));
            int[] q = [0, 2, 3, 1, 4, 5, 7, 6, 0, 1, 5, 4, 2, 6, 7, 3, 0, 4, 6, 2, 1, 3, 7, 5]; // outward quads
            for (int k = 0; k < q.Length; k += 4)
            {
                t.AddRange([q[k], q[k + 1], q[k + 2], q[k], q[k + 2], q[k + 3]]);
            }
        }
        else
        {
            double r = Diameter / 2;
            bool vertical = Shape == UserBilletShape.VerticalBar;
            // Local frame: axis s from 0 to Length; vertical bars stand on z, horizontal bars lie along x at z = r.
            Vec3 P(double s, double phi) => vertical
                ? Place(r * Math.Cos(phi), r * Math.Sin(phi), s)
                : Place(s - Length / 2, r * Math.Cos(phi), r + r * Math.Sin(phi));
            for (int i = 0; i < segments; i++)
            {
                double phi = 2 * Math.PI * i / segments;
                v.Add(P(0, phi));
                v.Add(P(Length, phi));
            }

            int c0 = v.Count, c1 = c0 + 1;
            v.Add(vertical ? Place(0, 0, 0) : Place(-Length / 2, 0, r));
            v.Add(vertical ? Place(0, 0, Length) : Place(Length / 2, 0, r));
            for (int i = 0; i < segments; i++)
            {
                int j = (i + 1) % segments;
                int a0 = 2 * i, a1 = 2 * i + 1, b0 = 2 * j, b1 = 2 * j + 1;
                t.AddRange([a0, b0, b1, a0, b1, a1]); // side
                t.AddRange([c0, b0, a0]);             // start cap
                t.AddRange([c1, a1, b1]);             // end cap
            }
        }

        var solid = new TriMeshSolid(v.ToArray(), t.ToArray());
        if (solid.Volume < 0)
        {
            var tt = t.ToArray();
            for (int k = 0; k < tt.Length; k += 3) (tt[k + 1], tt[k + 2]) = (tt[k + 2], tt[k + 1]);
            solid = new TriMeshSolid(v.ToArray(), tt);
        }

        return solid;
    }
}
