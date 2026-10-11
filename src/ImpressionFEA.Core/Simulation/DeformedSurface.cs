using System.Text;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Simulation;

/// <summary>Closed triangle surface of the deformed full part (mm, z = press axis) with a nodal result value per vertex.</summary>
public sealed class SurfaceMesh
{
    public required Vec3[] Vertices { get; init; }

    /// <summary>3 vertex indices per triangle, counter-clockwise seen from outside.</summary>
    public required int[] Triangles { get; init; }

    /// <summary>Nodal value of the chosen result field at each vertex.</summary>
    public required double[] Values { get; init; }

    public int TriangleCount => Triangles.Length / 3;
}

/// <summary>
/// Builds the outer surface of a deformed workpiece for export (STL / PLY): 3D results are mirrored from the modelled
/// half / quarter to the full part; 2D axisymmetric sections are revolved, plane-strain sections are mirrored and
/// extruded over the part length.
/// </summary>
public static class DeformedSurface
{
    public static SurfaceMesh Build(ForgingSnapshot snapshot, ResultField field, Symmetry3D symmetry = Symmetry3D.None, bool planeStrain = false, double partLength = 100, int segments = 96)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot switch
        {
            Snapshot3D s => Build3D(s, NodalValues(s, field), symmetry),
            StepSnapshot s when planeStrain => Extrude(s, NodalValues(s, field), partLength),
            StepSnapshot s => Revolve(s, NodalValues(s, field), Math.Max(segments, 8)),
            _ => throw new NotSupportedException("Unknown snapshot type."),
        };
    }

    // ------------------------------------------------------------------ nodal values

    /// <summary>Area-weighted average of 2D element values at the nodes.</summary>
    public static double[] NodalValues(StepSnapshot s, ResultField field)
    {
        ArgumentNullException.ThrowIfNull(s);
        var conn = s.Mesh.Connectivity;
        var vals = s.Field(field);
        var sum = new double[s.Mesh.NodeCount];
        var w = new double[s.Mesh.NodeCount];
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            int a = conn[4 * e], b = conn[4 * e + 1], c = conn[4 * e + 2], d = conn[4 * e + 3];
            double area = 0.5 * Math.Abs((s.X[c] - s.X[a]) * (s.Y[d] - s.Y[b]) - (s.X[d] - s.X[b]) * (s.Y[c] - s.Y[a])) + 1e-12;
            for (int k = 0; k < 4; k++)
            {
                int n = conn[4 * e + k];
                sum[n] += area * vals[e];
                w[n] += area;
            }
        }

        for (int n = 0; n < sum.Length; n++) sum[n] = w[n] > 0 ? sum[n] / w[n] : 0;
        return sum;
    }

    /// <summary>Volume-weighted average of 3D element values at the nodes.</summary>
    public static double[] NodalValues(Snapshot3D s, ResultField field)
    {
        ArgumentNullException.ThrowIfNull(s);
        var t = s.Mesh.Tets;
        var vals = s.Field(field);
        var sum = new double[s.Mesh.NodeCount];
        var w = new double[s.Mesh.NodeCount];
        for (int e = 0; e < s.Mesh.ElementCount; e++)
        {
            Vec3 P(int k) => new(s.X[t[4 * e + k]], s.Y[t[4 * e + k]], s.Z[t[4 * e + k]]);
            double vol = Math.Abs(TetMesh.Volume(P(0), P(1), P(2), P(3))) + 1e-12;
            for (int k = 0; k < 4; k++)
            {
                int n = t[4 * e + k];
                sum[n] += vol * vals[e];
                w[n] += vol;
            }
        }

        for (int n = 0; n < sum.Length; n++) sum[n] = w[n] > 0 ? sum[n] / w[n] : 0;
        return sum;
    }

    // ------------------------------------------------------------------ 3D

    private static SurfaceMesh Build3D(Snapshot3D s, double[] nodal, Symmetry3D symmetry)
    {
        (int Sx, int Sy)[] copies = symmetry switch
        {
            Symmetry3D.Quarter => [(1, 1), (-1, 1), (1, -1), (-1, -1)],
            Symmetry3D.Half => [(1, 1), (1, -1)],
            _ => [(1, 1)],
        };
        var mesh = s.Mesh;
        var f = mesh.BoundaryFaces;
        int nn = mesh.NodeCount;
        var v = new List<Vec3>(nn * copies.Length);
        var val = new List<double>(nn * copies.Length);
        var tri = new List<int>();
        foreach (var (sx, sy) in copies)
        {
            int baseIdx = v.Count;
            for (int n = 0; n < nn; n++)
            {
                v.Add(new Vec3(sx * s.X[n], sy * s.Y[n], s.Z[n]));
                val.Add(nodal[n]);
            }

            bool flip = sx * sy < 0; // a reflection reverses the winding
            for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
            {
                if (mesh.FaceIsSymmetry[k]) continue;
                int a = baseIdx + f[3 * k], b = baseIdx + f[3 * k + 1], c = baseIdx + f[3 * k + 2];
                if (flip) (b, c) = (c, b);
                tri.Add(a);
                tri.Add(b);
                tri.Add(c);
            }
        }

        return Compact(v, val, tri);
    }

    // ------------------------------------------------------------------ 2D

    private static double SignedArea(StepSnapshot s, int[] loop)
    {
        double a = 0;
        for (int k = 0; k < loop.Length; k++)
        {
            int i = loop[k], j = loop[(k + 1) % loop.Length];
            a += s.X[i] * s.Y[j] - s.X[j] * s.Y[i];
        }

        return 0.5 * a;
    }

    /// <summary>Axisymmetric: the meridian loop (r, z) revolved about z.</summary>
    private static SurfaceMesh Revolve(StepSnapshot s, double[] nodal, int segs)
    {
        var loop = s.Mesh.BoundaryLoop;
        int m = loop.Length;
        bool ccw = SignedArea(s, loop) > 0;
        var v = new List<Vec3>(m * segs);
        var val = new List<double>(m * segs);
        for (int k = 0; k < m; k++)
        {
            int n = loop[k];
            for (int j = 0; j < segs; j++)
            {
                double th = 2 * Math.PI * j / segs;
                v.Add(new Vec3(s.X[n] * Math.Cos(th), s.X[n] * Math.Sin(th), s.Y[n]));
                val.Add(nodal[n]);
            }
        }

        var tri = new List<int>();
        for (int k = 0; k < m; k++)
        {
            int k1 = (k + 1) % m;
            if (s.X[loop[k]] < 1e-9 && s.X[loop[k1]] < 1e-9) continue; // edge on the axis
            for (int j = 0; j < segs; j++)
            {
                int j1 = (j + 1) % segs;
                int a = k * segs + j, b = k1 * segs + j, c = k * segs + j1, d = k1 * segs + j1;
                // For a counter-clockwise meridian loop (A, C, B) faces outward (see derivation in the docs).
                if (ccw) AddQuad(tri, a, c, d, b);
                else AddQuad(tri, a, b, d, c);
            }
        }

        return Compact(v, val, tri);
    }

    /// <summary>Plane strain: the half section mirrored about x = 0 and extruded along y over the part length.</summary>
    private static SurfaceMesh Extrude(StepSnapshot s, double[] nodal, double length)
    {
        var mesh = s.Mesh;
        var loop = mesh.BoundaryLoop;
        bool ccw = SignedArea(s, loop) > 0;
        double y0 = -length / 2, y1 = length / 2;
        var v = new List<Vec3>();
        var val = new List<double>();
        var tri = new List<int>();
        foreach (double mir in new[] { 1.0, -1.0 })
        {
            // Vertex layout per half: [node n at y0], [node n at y1].
            int baseIdx = v.Count, nn = mesh.NodeCount;
            foreach (double y in new[] { y0, y1 })
            {
                for (int n = 0; n < nn; n++)
                {
                    v.Add(new Vec3(mir * s.X[n], y, s.Y[n]));
                    val.Add(nodal[n]);
                }
            }

            bool outwardCcw = ccw == (mir > 0); // mirroring reverses the loop orientation
            for (int k = 0; k < loop.Length; k++)
            {
                int i = loop[k], j = loop[(k + 1) % loop.Length];
                if (Math.Abs(s.X[i]) < 1e-9 && Math.Abs(s.X[j]) < 1e-9) continue; // symmetry plane (internal)
                int a0 = baseIdx + i, b0 = baseIdx + j, a1 = baseIdx + nn + i, b1 = baseIdx + nn + j;
                if (outwardCcw) AddQuad(tri, a0, a1, b1, b0);
                else AddQuad(tri, a0, b0, b1, a1);
            }

            var conn = mesh.Connectivity;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                int a = conn[4 * e], b = conn[4 * e + 1], c = conn[4 * e + 2], d = conn[4 * e + 3];
                double area = mir * ((s.X[c] - s.X[a]) * (s.Y[d] - s.Y[b]) - (s.X[d] - s.X[b]) * (s.Y[c] - s.Y[a]));
                // A counter-clockwise quad (area > 0) faces −y; the y0 cap must face −y, the y1 cap +y.
                if (area > 0)
                {
                    AddQuad(tri, baseIdx + a, baseIdx + b, baseIdx + c, baseIdx + d);
                    AddQuad(tri, baseIdx + nn + a, baseIdx + nn + d, baseIdx + nn + c, baseIdx + nn + b);
                }
                else
                {
                    AddQuad(tri, baseIdx + a, baseIdx + d, baseIdx + c, baseIdx + b);
                    AddQuad(tri, baseIdx + nn + a, baseIdx + nn + b, baseIdx + nn + c, baseIdx + nn + d);
                }
            }
        }

        return Compact(v, val, tri);
    }

    private static void AddQuad(List<int> tri, int a, int b, int c, int d)
    {
        tri.Add(a);
        tri.Add(b);
        tri.Add(c);
        tri.Add(a);
        tri.Add(c);
        tri.Add(d);
    }

    /// <summary>Drops degenerate triangles (e.g. at the revolution axis) and unused vertices.</summary>
    private static SurfaceMesh Compact(List<Vec3> v, List<double> val, List<int> tri)
    {
        var map = new int[v.Count];
        Array.Fill(map, -1);
        var nv = new List<Vec3>();
        var nval = new List<double>();
        var nt = new List<int>(tri.Count);
        for (int k = 0; k < tri.Count; k += 3)
        {
            var p = v[tri[k]];
            var q = v[tri[k + 1]];
            var r = v[tri[k + 2]];
            if ((q - p).Cross(r - p).LengthSquared < 1e-18) continue;
            for (int i = 0; i < 3; i++)
            {
                int o = tri[k + i];
                if (map[o] < 0)
                {
                    map[o] = nv.Count;
                    nv.Add(v[o]);
                    nval.Add(val[o]);
                }

                nt.Add(map[o]);
            }
        }

        return new SurfaceMesh { Vertices = nv.ToArray(), Triangles = nt.ToArray(), Values = nval.ToArray() };
    }

    // ------------------------------------------------------------------ writers

    /// <summary>Binary STL (mm). <paramref name="header"/> is truncated to 80 bytes.</summary>
    public static void WriteStl(string path, SurfaceMesh mesh, string header = "ImpressionFEA deformed part (mm)")
    {
        ArgumentNullException.ThrowIfNull(mesh);
        using var w = new BinaryWriter(File.Create(path));
        var h = new byte[80];
        Encoding.ASCII.GetBytes(header.Length > 80 ? header[..80] : header).CopyTo(h, 0);
        w.Write(h);
        w.Write((uint)mesh.TriangleCount);
        var t = mesh.Triangles;
        var p = mesh.Vertices;
        for (int k = 0; k < t.Length; k += 3)
        {
            var a = p[t[k]];
            var b = p[t[k + 1]];
            var c = p[t[k + 2]];
            var n = (b - a).Cross(c - a).Normalized();
            foreach (var q in new[] { n, a, b, c })
            {
                w.Write((float)q.X);
                w.Write((float)q.Y);
                w.Write((float)q.Z);
            }

            w.Write((ushort)0);
        }
    }

    /// <summary>Binary PLY with per-vertex colour (RGB from <paramref name="colorOf"/>) and the raw result value as a scalar property.</summary>
    public static void WritePly(string path, SurfaceMesh mesh, Func<double, (byte R, byte G, byte B)> colorOf, string valueName = "value")
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(colorOf);
        using var fs = File.Create(path);
        var head = new StringBuilder()
            .Append("ply\nformat binary_little_endian 1.0\ncomment ImpressionFEA deformed part, mm\n")
            .Append("element vertex ").Append(mesh.Vertices.Length).Append('\n')
            .Append("property float x\nproperty float y\nproperty float z\n")
            .Append("property uchar red\nproperty uchar green\nproperty uchar blue\n")
            .Append("property float ").Append(valueName).Append('\n')
            .Append("element face ").Append(mesh.TriangleCount).Append('\n')
            .Append("property list uchar int vertex_indices\nend_header\n");
        var hb = Encoding.ASCII.GetBytes(head.ToString());
        fs.Write(hb);
        using var w = new BinaryWriter(fs);
        for (int i = 0; i < mesh.Vertices.Length; i++)
        {
            var p = mesh.Vertices[i];
            var (r, g, b) = colorOf(mesh.Values[i]);
            w.Write((float)p.X);
            w.Write((float)p.Y);
            w.Write((float)p.Z);
            w.Write(r);
            w.Write(g);
            w.Write(b);
            w.Write((float)mesh.Values[i]);
        }

        for (int k = 0; k < mesh.Triangles.Length; k += 3)
        {
            w.Write((byte)3);
            w.Write(mesh.Triangles[k]);
            w.Write(mesh.Triangles[k + 1]);
            w.Write(mesh.Triangles[k + 2]);
        }
    }

    /// <summary>Enclosed volume of a closed, outward-oriented surface (divergence theorem); for checks.</summary>
    public static double Volume(SurfaceMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        double v6 = 0;
        var t = mesh.Triangles;
        var p = mesh.Vertices;
        for (int k = 0; k < t.Length; k += 3) v6 += p[t[k]].Dot(p[t[k + 1]].Cross(p[t[k + 2]]));
        return v6 / 6;
    }
}
