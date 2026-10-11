using System.Runtime.InteropServices;
using System.Text;
using ImpressionFEA.Core.ThreeD;

namespace ImpressionFEA.Core.Cad;

/// <summary>One solid body of a STEP file: exact B-rep mass properties and an outward-oriented tessellation (mm).</summary>
public sealed record CadBody(int Index, double Volume, Vec3 CenterOfGravity, Vec3 Min, Vec3 Max, TriMeshSolid Mesh)
{
    public override string ToString() => $"Body {Index + 1}";
}

/// <summary>Reads STEP (ISO 10303-21) files through ImpressionFeaCad.dll, a small C API over Open CASCADE (native/cad).</summary>
public static partial class StepImporter
{
    private const string Lib = "ImpressionFeaCad";

    static StepImporter()
    {
        // The OCCT runtime lives in the "cad" subfolder of the app; loading the shim by absolute path makes Windows
        // resolve its dependencies from that folder.
        NativeLibrary.SetDllImportResolver(typeof(StepImporter).Assembly, (name, asm, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            string file = Path.Combine(AppContext.BaseDirectory, "cad", Lib + ".dll");
            return File.Exists(file) && NativeLibrary.TryLoad(file, out var h) ? h : IntPtr.Zero;
        });
    }

    [LibraryImport(Lib, EntryPoint = "cf_step_open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Open(string path, double linDefl, double angDefl, byte[] err, int errLen);

    [LibraryImport(Lib, EntryPoint = "cf_body_count")]
    private static partial int BodyCount(IntPtr h);

    [LibraryImport(Lib, EntryPoint = "cf_body_info")]
    private static partial int BodyInfo(IntPtr h, int i, out int nVerts, out int nTris, out double volume, [Out] double[] cog, [Out] double[] bbox);

    [LibraryImport(Lib, EntryPoint = "cf_body_mesh")]
    private static partial int BodyMesh(IntPtr h, int i, [Out] double[] verts, [Out] int[] tris);

    [LibraryImport(Lib, EntryPoint = "cf_close")]
    private static partial void Close(IntPtr h);

    /// <summary>True when the Open CASCADE runtime can be loaded; otherwise a reason.</summary>
    public static bool IsAvailable(out string message)
    {
        try
        {
            _ = BodyCount(IntPtr.Zero);
            message = "Open CASCADE STEP reader available.";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            message = "STEP import is unavailable: ImpressionFeaCad.dll / Open CASCADE runtime not found next to the app (build native/cad/build-cad.ps1). " + ex.Message;
            return false;
        }
    }

    /// <summary>Reads every solid of a STEP file. Throws <see cref="InvalidDataException"/> with a readable message on failure.</summary>
    public static IReadOnlyList<CadBody> Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) throw new FileNotFoundException("STEP file not found.", path);
        if (!IsAvailable(out string why)) throw new InvalidOperationException(why);

        // Chordal tolerance relative to the file's scale is unknown up front; 0.02 mm with 0.15 rad suits forging dies (50–500 mm).
        var err = new byte[1024];
        IntPtr h = Open(path, 0.02, 0.15, err, err.Length);
        if (h == IntPtr.Zero)
        {
            int len = Array.IndexOf(err, (byte)0);
            throw new InvalidDataException(Encoding.UTF8.GetString(err, 0, len < 0 ? err.Length : len));
        }

        try
        {
            int n = BodyCount(h);
            var bodies = new List<CadBody>(n);
            var cog = new double[3];
            var box = new double[6];
            for (int i = 0; i < n; i++)
            {
                if (BodyInfo(h, i, out int nv, out int nt, out double vol, cog, box) != 0) throw new InvalidDataException($"Body {i + 1} could not be queried.");
                if (nt == 0) throw new InvalidDataException($"Body {i + 1} could not be tessellated.");
                var v = new double[3 * nv];
                var t = new int[3 * nt];
                if (BodyMesh(h, i, v, t) != 0) throw new InvalidDataException($"Body {i + 1} tessellation could not be read.");
                var pts = new Vec3[nv];
                for (int k = 0; k < nv; k++) pts[k] = new Vec3(v[3 * k], v[3 * k + 1], v[3 * k + 2]);
                var mesh = new TriMeshSolid(pts, t);
                if (mesh.Volume < 0)
                {
                    for (int k = 0; k < nt; k++) (t[3 * k + 1], t[3 * k + 2]) = (t[3 * k + 2], t[3 * k + 1]);
                    mesh = new TriMeshSolid(pts, t);
                }

                bodies.Add(new CadBody(i, Math.Abs(vol), new Vec3(cog[0], cog[1], cog[2]), new Vec3(box[0], box[1], box[2]), new Vec3(box[3], box[4], box[5]), mesh));
            }

            return bodies;
        }
        finally
        {
            Close(h);
        }
    }
}
