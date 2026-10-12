using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Simulation;

/// <summary>
/// Reference (billet) position of every node of a 3D mesh (<paramref name="Mesh"/>, shared by the increments between
/// remeshes; use the field only with that mesh), and the extent of the billet. A fibre of the bar stock is the set of
/// material points with the same reference position across the fibre direction, so iso-lines of a reference
/// coordinate on a section are the grain flow lines of an etched cut.
/// </summary>
public sealed record GrainReference3D(TetMesh Mesh, float[] X0, float[] Y0, float[] Z0, Vec3 Min, Vec3 Max);

/// <summary>
/// Grain flow of a 3D forging (post-processing only): carries the reference position of the material through every
/// increment. Between remeshes the nodes are material points and keep their values (arrays are shared); across a
/// remesh each new node is traced back to the start of the increment (x − Δt·v) and interpolated in the previous
/// mesh. Computed incrementally, so a live run only processes its new steps; memory grows only with the remeshes.
/// </summary>
public sealed class GrainFlow3DTracer
{
    private readonly List<Snapshot3D> _snaps = [];
    private readonly List<GrainReference3D> _refs = [];

    /// <summary>Reference field at increment <paramref name="index"/> (index 0 = the billet), or null for a 2D run.</summary>
    public GrainReference3D? At(IReadOnlyList<ForgingSnapshot> snapshots, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        if (index < 0 || index >= snapshots.Count) return null;
        if (_snaps.Count > 0 && !ReferenceEquals(_snaps[0], snapshots[0]))
        {
            _snaps.Clear();
            _refs.Clear();
        }

        for (int k = 0; k < Math.Min(_snaps.Count, index + 1); k++)
        {
            if (ReferenceEquals(_snaps[k], snapshots[k])) continue;
            _snaps.RemoveRange(k, _snaps.Count - k);
            _refs.RemoveRange(k, _refs.Count - k);
            break;
        }

        while (_snaps.Count <= index)
        {
            if (snapshots[_snaps.Count] is not Snapshot3D s) return null;
            _refs.Add(_snaps.Count == 0 ? Seed(s) : Advance(s));
            _snaps.Add(s);
        }

        return _refs[index];
    }

    private static GrainReference3D Seed(Snapshot3D s)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
        for (int n = 0; n < s.NodeCount; n++)
        {
            x0 = Math.Min(x0, s.X[n]); x1 = Math.Max(x1, s.X[n]);
            y0 = Math.Min(y0, s.Y[n]); y1 = Math.Max(y1, s.Y[n]);
            z0 = Math.Min(z0, s.Z[n]); z1 = Math.Max(z1, s.Z[n]);
        }

        return new GrainReference3D(s.Mesh, s.X, s.Y, s.Z, new Vec3(x0, y0, z0), new Vec3(x1, y1, z1));
    }

    private GrainReference3D Advance(Snapshot3D cur)
    {
        var prev = _snaps[^1];
        var pr = _refs[^1];
        if (ReferenceEquals(cur.Mesh, prev.Mesh) && prev.NodeCount == cur.NodeCount) return pr; // same material points

        var px = new Vec3[prev.NodeCount];
        for (int n = 0; n < px.Length; n++) px[n] = new Vec3(prev.X[n], prev.Y[n], prev.Z[n]);
        var mesh = prev.Mesh;
        var t = mesh.Tets;
        double vol = 0;
        for (int e = 0; e < mesh.ElementCount; e++) vol += Math.Abs(TetMesh.Volume(px[t[4 * e]], px[t[4 * e + 1]], px[t[4 * e + 2]], px[t[4 * e + 3]]));
        double cell = 2 * Math.Cbrt(Math.Max(vol, 1e-12) / Math.Max(mesh.ElementCount, 1));
        var loc = new TetLocator(mesh, px, cell);

        double dt = cur.Time - prev.Time;
        bool v = cur.HasVelocity && cur.VelocityX.Length == cur.NodeCount && cur.VelocityZ.Length == cur.NodeCount;
        var rx = new float[cur.NodeCount];
        var ry = new float[cur.NodeCount];
        var rz = new float[cur.NodeCount];
        for (int n = 0; n < cur.NodeCount; n++)
        {
            var p = new Vec3(cur.X[n], cur.Y[n], cur.Z[n]);
            if (v) p -= dt * new Vec3(cur.VelocityX[n], cur.VelocityY[n], cur.VelocityZ[n]);
            int e = loc.Find(p, out double w0, out double w1, out double w2, out double w3);
            if (e < 0)
            {
                e = loc.FindNearest(p);
                if (e < 0) continue;
                (w0, w1, w2, w3) = ClampedWeights(px[t[4 * e]], px[t[4 * e + 1]], px[t[4 * e + 2]], px[t[4 * e + 3]], p);
            }

            int a = t[4 * e], b = t[4 * e + 1], c = t[4 * e + 2], d = t[4 * e + 3];
            rx[n] = (float)(w0 * pr.X0[a] + w1 * pr.X0[b] + w2 * pr.X0[c] + w3 * pr.X0[d]);
            ry[n] = (float)(w0 * pr.Y0[a] + w1 * pr.Y0[b] + w2 * pr.Y0[c] + w3 * pr.Y0[d]);
            rz[n] = (float)(w0 * pr.Z0[a] + w1 * pr.Z0[b] + w2 * pr.Z0[c] + w3 * pr.Z0[d]);
        }

        return pr with { Mesh = cur.Mesh, X0 = rx, Y0 = ry, Z0 = rz };
    }

    /// <summary>Barycentric weights of p in tet abcd with negative weights cut to zero (p just outside the mesh).</summary>
    private static (double, double, double, double) ClampedWeights(Vec3 a, Vec3 b, Vec3 c, Vec3 d, Vec3 p)
    {
        double vol = TetMesh.Volume(a, b, c, d);
        if (Math.Abs(vol) < 1e-30) return (0.25, 0.25, 0.25, 0.25);
        double w0 = Math.Max(0, TetMesh.Volume(p, b, c, d) / vol);
        double w1 = Math.Max(0, TetMesh.Volume(a, p, c, d) / vol);
        double w2 = Math.Max(0, TetMesh.Volume(a, b, p, d) / vol);
        double w3 = Math.Max(0, TetMesh.Volume(a, b, c, p) / vol);
        double s = w0 + w1 + w2 + w3;
        return s > 0 ? (w0 / s, w1 / s, w2 / s, w3 / s) : (0.25, 0.25, 0.25, 0.25);
    }
}
