// Generates sample STEP files (3 solids each: bottom die, billet, top die) for testing ImpressionFEA's STEP import.
// Built and run by build-cad.ps1 -Samples. Units: mm. Press axis: +Z (top die above).
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeRevol.hxx>
#include <BRep_Builder.hxx>
#include <STEPControl_Writer.hxx>
#include <TopoDS_Compound.hxx>
#include <gp_Ax1.hxx>
#include <gp_Ax2.hxx>
#include <gp_Trsf.hxx>

#include <cmath>
#include <cstdio>
#include <utility>
#include <vector>

namespace
{
TopoDS_Shape Moved(const TopoDS_Shape& s, double dx, double dy, double dz)
{
    gp_Trsf t;
    t.SetTranslation(gp_Vec(dx, dy, dz));
    return BRepBuilderAPI_Transform(s, t, true).Shape();
}

TopoDS_Shape MirrorZ(const TopoDS_Shape& s)
{
    gp_Trsf t;
    t.SetMirror(gp_Ax2(gp_Pnt(0, 0, 0), gp_Dir(0, 0, 1)));
    return BRepBuilderAPI_Transform(s, t, true).Shape();
}

void Write(const char* path, const TopoDS_Shape& bottom, const TopoDS_Shape& billet, const TopoDS_Shape& top)
{
    BRep_Builder b;
    TopoDS_Compound c;
    b.MakeCompound(c);
    b.Add(c, bottom);
    b.Add(c, billet);
    b.Add(c, top);
    STEPControl_Writer w;
    w.Transfer(c, STEPControl_AsIs);
    std::printf("%s: %s\n", path, w.Write(path) == IFSelect_RetDone ? "ok" : "FAILED");
}

// Two-body file: the dies only.
void WriteDies(const char* path, const TopoDS_Shape& bottom, const TopoDS_Shape& top)
{
    BRep_Builder b;
    TopoDS_Compound c;
    b.MakeCompound(c);
    b.Add(c, bottom);
    b.Add(c, top);
    STEPControl_Writer w;
    w.Transfer(c, STEPControl_AsIs);
    std::printf("%s: %s\n", path, w.Write(path) == IFSelect_RetDone ? "ok" : "FAILED");
}

// Solid of revolution (about Z) of a closed polygon given as (r, z) points.
TopoDS_Shape Revolve(const std::vector<std::pair<double, double>>& rz)
{
    BRepBuilderAPI_MakePolygon poly;
    for (auto& p : rz) poly.Add(gp_Pnt(p.first, 0, p.second));
    poly.Close();
    TopoDS_Face face = BRepBuilderAPI_MakeFace(poly.Wire());
    return BRepPrimAPI_MakeRevol(face, gp_Ax1(gp_Pnt(0, 0, 0), gp_Dir(0, 0, 1))).Shape();
}

TopoDS_Shape Cylinder(double r, double z0, double h) { return BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(0, 0, z0), gp_Dir(0, 0, 1)), r, h).Shape(); }

// Square die plate [-a, a]² × [z0, z0 + t] with four corner stop pads reaching up to zStop.
TopoDS_Shape PlateWithStops(double a, double z0, double t, double zStop)
{
    TopoDS_Shape plate = BRepPrimAPI_MakeBox(gp_Pnt(-a, -a, z0), 2 * a, 2 * a, t).Shape();
    for (int sx = -1; sx <= 1; sx += 2)
    {
        for (int sy = -1; sy <= 1; sy += 2)
        {
            TopoDS_Shape pad = BRepPrimAPI_MakeBox(gp_Pnt(sx * (a - 5) - 5, sy * (a - 5) - 5, z0 + t), 10, 10, zStop - (z0 + t)).Shape();
            plate = BRepAlgoAPI_Fuse(plate, pad).Shape();
        }
    }

    return plate;
}
} // namespace

int main()
{
    // 1. Upset: Ø40 × 51.2 billet between flat plates; corner stop pads meet at 20 mm shut height.
    {
        TopoDS_Shape bottom = PlateWithStops(60, -20, 20, 10);        // pads up to z = 10
        TopoDS_Shape top = MirrorZ(PlateWithStops(60, -20, 20, 10));  // pads down to z = -10
        top = Moved(top, 0, 0, 20 + 31.2);                           // shut height 20, 31.2 mm stroke left
        Write("sample_upset_3bodies.step", bottom, Cylinder(20, 0, 51.2), top);
    }

    // 2. Gear blank: the 2D preset's impression (hub, web, rim, 3 mm flash land, gutter) revolved into die blocks,
    //    with an outer stop ring where the dies meet.
    {
        const double dr = std::tan(7.0 * 3.14159265358979 / 180.0);
        std::vector<std::pair<double, double>> depth = {
            {0, 20}, {18, 20}, {18 + 14 * dr, 6}, {44, 6}, {44 + 6 * dr, 12}, {54, 12}, {54 + 10.5 * dr, 1.5},
            {70, 1.5}, {72, 5}, {84, 5}, {86, 0}, {100, 0}};
        std::vector<std::pair<double, double>> section = {{0, -40}, {100, -40}};
        for (auto it = depth.rbegin(); it != depth.rend(); ++it) section.push_back({it->first, -it->second});
        TopoDS_Shape bottom = Revolve(section);
        TopoDS_Shape top = Moved(MirrorZ(bottom), 0, 0, 69.04);
        Write("sample_gear_3bodies.step", bottom, Cylinder(28, -6, 81.04), top);
        // Dies only (no workpiece): the user defines the billet in ImpressionFEA. Top die raised to leave room for it.
        WriteDies("sample_gear_dies_only.step", bottom, Moved(MirrorZ(bottom), 0, 0, 160));
    }

    // 3. Hex flange (not axisymmetric): Ø30 bar seated in a close-fit Ø30.2 shank pocket; the top die carries a hexagonal
    //    pocket (60 mm across flats, 12 mm deep). Corner pads stop the dies with a 2 mm flash gap.
    {
        TopoDS_Shape bottom = PlateWithStops(70, -40, 40, 2);
        bottom = BRepAlgoAPI_Cut(bottom, Cylinder(15.1, -30, 31)).Shape();
        BRepBuilderAPI_MakePolygon hex;
        for (int k = 0; k < 6; ++k)
        {
            double a = 3.14159265358979 / 6 + k * 3.14159265358979 / 3;
            hex.Add(gp_Pnt(30 / std::cos(3.14159265358979 / 6) * std::cos(a), 30 / std::cos(3.14159265358979 / 6) * std::sin(a), 0));
        }

        hex.Close();
        TopoDS_Shape pocket = BRepPrimAPI_MakePrism(BRepBuilderAPI_MakeFace(hex.Wire()), gp_Vec(0, 0, 12)).Shape();
        TopoDS_Shape topPlate = BRepPrimAPI_MakeBox(gp_Pnt(-70, -70, 0), 140, 140, 40).Shape();
        topPlate = BRepAlgoAPI_Cut(topPlate, pocket).Shape();
        // Closed when the top-plate face reaches z = 2, where it meets the bottom die's corner pads (2 mm flash gap).
        // 30 mm in the shank pocket + 72 mm upset into the hex head: about 10 % more bar than the cavity (the usual flash
        // allowance), so the hex corners fill completely and the excess escapes as flash.
        const double billetH = 30 + 72.0;
        TopoDS_Shape top = Moved(topPlate, 0, 0, -30 + billetH);  // top die face touches the billet top
        Write("sample_hexflange_3bodies.step", bottom, Cylinder(15, -30, billetH), top);
    }

    return 0;
}
