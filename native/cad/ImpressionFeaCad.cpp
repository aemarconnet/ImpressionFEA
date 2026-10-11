// ImpressionFeaCad — minimal C API over Open CASCADE for the ImpressionFEA desktop app.
// Reads a STEP file, enumerates its solids, computes exact volume / centre of gravity from the B-rep and returns a
// tessellation of each solid in millimetres. Built by build-cad.ps1 (MSVC, C++17) against OCCT 8.0.1.
#include <BRepGProp.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <BRepBndLib.hxx>
#include <GProp_GProps.hxx>
#include <IFSelect_ReturnStatus.hxx>
#include <Interface_Static.hxx>
#include <Poly_Triangulation.hxx>
#include <STEPControl_Reader.hxx>
#include <Standard_Failure.hxx>
#include <TopExp_Explorer.hxx>
#include <TopLoc_Location.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Face.hxx>
#include <TopoDS_Shape.hxx>
#include <TopoDS_Solid.hxx>

#include <cstring>
#include <string>
#include <vector>

#define CF_API extern "C" __declspec(dllexport)

namespace
{
struct Body
{
    std::vector<double> verts; // x, y, z (mm)
    std::vector<int> tris;     // outward counter-clockwise
    double volume = 0;
    double cog[3] = {0, 0, 0};
    double bbox[6] = {0, 0, 0, 0, 0, 0};
};

struct Model
{
    std::vector<Body> bodies;
};

void SetError(char* err, int len, const std::string& msg)
{
    if (err == nullptr || len <= 0) return;
    strncpy_s(err, len, msg.c_str(), _TRUNCATE);
}

void Tessellate(const TopoDS_Shape& solid, double linDefl, double angDefl, Body& b)
{
    BRepMesh_IncrementalMesh mesher(solid, linDefl, false, angDefl, true);
    for (TopExp_Explorer ex(solid, TopAbs_FACE); ex.More(); ex.Next())
    {
        const TopoDS_Face& face = TopoDS::Face(ex.Current());
        TopLoc_Location loc;
        Handle(Poly_Triangulation) tri = BRep_Tool::Triangulation(face, loc);
        if (tri.IsNull()) continue;
        const gp_Trsf trsf = loc.Transformation();
        const int base = static_cast<int>(b.verts.size() / 3);
        for (int i = 1; i <= tri->NbNodes(); ++i)
        {
            gp_Pnt p = tri->Node(i).Transformed(trsf);
            b.verts.push_back(p.X());
            b.verts.push_back(p.Y());
            b.verts.push_back(p.Z());
        }

        const bool reversed = face.Orientation() == TopAbs_REVERSED;
        for (int i = 1; i <= tri->NbTriangles(); ++i)
        {
            int n1, n2, n3;
            tri->Triangle(i).Get(n1, n2, n3);
            if (reversed) std::swap(n2, n3);
            b.tris.push_back(base + n1 - 1);
            b.tris.push_back(base + n2 - 1);
            b.tris.push_back(base + n3 - 1);
        }
    }
}
} // namespace

// Opens a STEP file (UTF-8 path). linDefl: chordal tolerance in mm; angDefl: angular tolerance in radians.
// Returns a model handle, or null with a message in err.
CF_API void* cf_step_open(const char* utf8Path, double linDefl, double angDefl, char* err, int errLen)
{
    try
    {
        Interface_Static::SetCVal("xstep.cascade.unit", "MM");
        STEPControl_Reader reader;
        if (reader.ReadFile(utf8Path) != IFSelect_RetDone)
        {
            SetError(err, errLen, "The file could not be read as STEP (ISO 10303-21).");
            return nullptr;
        }

        reader.TransferRoots();
        TopoDS_Shape shape = reader.OneShape();
        if (shape.IsNull())
        {
            SetError(err, errLen, "The STEP file contains no transferable geometry.");
            return nullptr;
        }

        auto* model = new Model();
        for (TopExp_Explorer ex(shape, TopAbs_SOLID); ex.More(); ex.Next())
        {
            Body b;
            GProp_GProps props;
            BRepGProp::VolumeProperties(ex.Current(), props);
            b.volume = props.Mass();
            gp_Pnt c = props.CentreOfMass();
            b.cog[0] = c.X();
            b.cog[1] = c.Y();
            b.cog[2] = c.Z();
            Bnd_Box box;
            BRepBndLib::Add(ex.Current(), box);
            box.Get(b.bbox[0], b.bbox[1], b.bbox[2], b.bbox[3], b.bbox[4], b.bbox[5]);
            Tessellate(ex.Current(), linDefl, angDefl, b);
            model->bodies.push_back(std::move(b));
        }

        if (model->bodies.empty())
        {
            delete model;
            SetError(err, errLen, "The STEP file contains no solid bodies (only surfaces or wireframe).");
            return nullptr;
        }

        return model;
    }
    catch (const Standard_Failure& f)
    {
        SetError(err, errLen, std::string("Open CASCADE error: ") + (f.what() ? f.what() : "unknown"));
        return nullptr;
    }
    catch (...)
    {
        SetError(err, errLen, "Unexpected error while reading the STEP file.");
        return nullptr;
    }
}

CF_API int cf_body_count(void* h) { return h ? static_cast<int>(static_cast<Model*>(h)->bodies.size()) : 0; }

// info: volume, cog[3], bbox[6] (xmin, ymin, zmin, xmax, ymax, zmax). Returns 0 on success.
CF_API int cf_body_info(void* h, int i, int* nVerts, int* nTris, double* volume, double* cog, double* bbox)
{
    auto* m = static_cast<Model*>(h);
    if (!m || i < 0 || i >= static_cast<int>(m->bodies.size())) return -1;
    const Body& b = m->bodies[i];
    *nVerts = static_cast<int>(b.verts.size() / 3);
    *nTris = static_cast<int>(b.tris.size() / 3);
    *volume = b.volume;
    std::memcpy(cog, b.cog, sizeof(b.cog));
    std::memcpy(bbox, b.bbox, sizeof(b.bbox));
    return 0;
}

// Copies the tessellation of body i into caller-allocated arrays (3·nVerts doubles, 3·nTris ints).
CF_API int cf_body_mesh(void* h, int i, double* verts, int* tris)
{
    auto* m = static_cast<Model*>(h);
    if (!m || i < 0 || i >= static_cast<int>(m->bodies.size())) return -1;
    const Body& b = m->bodies[i];
    std::memcpy(verts, b.verts.data(), b.verts.size() * sizeof(double));
    std::memcpy(tris, b.tris.data(), b.tris.size() * sizeof(int));
    return 0;
}

CF_API void cf_close(void* h) { delete static_cast<Model*>(h); }
