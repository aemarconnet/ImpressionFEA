using System.Diagnostics;
using System.Globalization;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Simulation;
using ImpressionFEA.Core.Solver;
using ImpressionFEA.Core.Solver.Linear;

namespace ImpressionFEA.Core.ThreeD;

/// <summary>
/// 3D rigid-viscoplastic flow-formulation FEM for closed-die forging — the 3D counterpart of <see cref="ForgingSolver"/>.
/// <list type="bullet">
/// <item>Linear tetrahedra; deviatoric power integrated per element, incompressibility enforced by a penalty on the
/// <i>averaged nodal</i> volumetric strain rate (Bonet–Burton ANP), which removes volumetric locking of linear tets.</item>
/// <item>Same convex functional, ε̇₀ cut-off, Picard → Newton–Raphson with Armijo line search as the 2D solver;
/// sparse Cholesky (CSparse, AMD ordering, symbolic analysis reused for every refactorisation on a mesh).</item>
/// <item>Height-field rigid dies, node-to-surface contact in orthonormal nodal frames built by Gram–Schmidt from
/// symmetry-plane and die-normal constraints, vector shear friction τ = m·k (arctan-regularised), tensile release.</item>
/// <item>Remeshing by isosurface stuffing driven by inside tests on the deformed mesh, state remapping and volume compensation.</item>
/// </list>
/// </summary>
public sealed class Forging3DSolver : IForgingStepper, IDisposable
{

    /// <summary>ANP penalty relative to the reference viscosity σ̄/ε̇ (volume error ≈ 1/factor per unit strain).</summary>
    public static double PenaltyFactor { get; set; } = 1e4;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly SimulationConfig3D _cfg;
    private readonly DieSet3D _die;
    private readonly FlowStressLaw _law;
    private readonly Action<string> _log;
    private readonly bool _symX;
    private readonly bool _symY;
    private readonly int _symFactor;
    private readonly double _h;
    private readonly double _ram;
    private readonly double _rateRef;
    private readonly double _rateCut;
    private readonly double _kPen;
    private readonly double _u0;
    private readonly double _sigRef;
    private readonly double _gap0;
    private readonly double _vol0;
    private readonly Vec3[] _cavity;
    private double _gap;
    private double _ds;
    private Vec3? _failAt;
    private double _time;
    private double _lastRel;

    // mesh-sized state
    private TetMesh _t = null!;
    private int _nn;
    private int _ne;
    private int _ndof;
    private Vec3[] _x = null!;
    private Vec3[] _vg = null!;
    private double[] _eps = null!;
    private NodeContact[] _contact = null!;
    private Vec3[] _ax0 = null!;
    private Vec3[] _ax1 = null!;
    private Vec3[] _ax2 = null!;
    private int[] _nFix = null!;
    private int[] _dieAxis = null!;
    private bool[] _fixed = null!;
    private double[] _fixedVal = null!;
    private bool[] _release = null!;
    private double[] _kFric = null!;
    private double[] _aFric = null!;
    private double[] _elSig = null!;
    private double[] _elRate = null!;
    private double[] _elMean = null!;
    private double[] _grad = null!;
    private double[] _vol = null!;

    // sparse system (full symmetric CSC, pattern fixed per mesh)
    private int[] _colPtr = null!;
    private int[] _rowIdx = null!;
    private int[][] _nbr = null!;
    private double[] _val = null!;
    private double[] _kvol = null!;
    private ISpdSolver? _linImpl;
    private bool _needAnalyze = true;
    private bool _haveFactor;
    private double[] _v = null!;
    private double[] _r = null!;
    private double[] _dv = null!;
    private double[] _vTrial = null!;
    private double[] _rvol = null!;

    // ANP operator g_n·v = Σ coef·v_m (global), per step
    private int[] _gStart = null!;
    private int[] _gNode = null!;
    private Vec3[] _gCoef = null!;
    private double[] _vNode = null!;

    private readonly double[] _ke = new double[144];
    private readonly double[] _re = new double[12];
    private readonly double[] _ve = new double[12];
    private readonly double[] _b = new double[72];
    private readonly double[] _mb = new double[72];
    private readonly double[] _g = new double[12];

    public Forging3DSolver(SimulationConfig3D config, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0) throw new InputValidationException(errors);
        _cfg = config;
        _die = config.Die;
        _law = config.Material.At(config.Temperature);
        _log = log ?? (_ => { });
        _symX = _die.SymmetricX;
        _symY = _die.SymmetricY;
        _symFactor = _die.SymmetryFactor;
        _h = config.ElementSize;
        _ram = config.RamSpeed;

        double r0 = config.BilletRadius, dim = config.BilletDimension;
        var (seat, gap) = _die.Place(r0, dim);
        _gap0 = _gap = gap;
        _ds = config.StrokeIncrement;
        var (min, max) = _die.BilletBounds(r0, dim, seat);
        double size = _die.Billet switch { BilletShape.VerticalCylinder => dim, BilletShape.HorizontalBar => 2 * r0, _ => max.Z - min.Z };
        _rateRef = _ram / size;
        _rateCut = 1e-3 * _rateRef;
        _sigRef = _law.Stress(0, _rateRef);
        _kPen = PenaltyFactor * _sigRef / _rateRef;
        _u0 = 5e-3 * _ram;

        var inside = _die.BilletInside(r0, dim, seat);
        var res = IsosurfaceStuffing.Stuff(inside, min, max, _h, _symX, _symY);
        if (res.Points.Length == 0) throw new InputValidationException(["The workpiece produced an empty mesh; reduce the element size h."]);
        Allocate(BuildMesh(res), res.Points);
        Array.Fill(_elSig, _sigRef);

        // Initial contact: surface nodes touching the seat / top die; guarantee at least one node per die.
        double tol = 0.05 * _h;
        int nb = 0, nt = 0;
        for (int n = 0; n < _nn; n++)
        {
            if (!_t.IsSurfaceNode[n]) continue;
            if (_die.Bottom.Clearance(_x[n], 0) < tol) { _contact[n] = NodeContact.BottomDie; nb++; }
            else if (_die.TopClosed.Clearance(_x[n], _gap) < tol) { _contact[n] = NodeContact.TopDie; nt++; }
        }

        if (nb == 0) ForceContact(NodeContact.BottomDie);
        if (nt == 0) ForceContact(NodeContact.TopDie);
        for (int n = 0; n < _nn; n++)
        {
            if (_contact[n] != NodeContact.Free) _x[n] = Place(_x[n], _contact[n], n);
        }

        for (int n = 0; n < _nn; n++)
        {
            var p = _x[n];
            double cx = 0.5 * (min.X + max.X), cy = 0.5 * (min.Y + max.Y);
            _vg[n] = _die.Billet == BilletShape.HorizontalBar
                ? new Vec3(0, _rateRef * p.Y, -_rateRef * (p.Z - min.Z))
                : new Vec3(0.5 * _rateRef * (p.X - cx), 0.5 * _rateRef * (p.Y - cy), -_rateRef * (p.Z - min.Z));
        }

        _vol0 = Volume();
        _cavity = BuildCavitySamples();
        LogBanner(r0, dim);
    }

    public double TotalStroke => _gap0;

    public int NominalSteps => (int)Math.Ceiling(_gap0 / _cfg.StrokeIncrement);

    public int StepIndex { get; private set; }

    public int RemeshCount { get; private set; }

    public bool IsFinished => _gap <= 1e-9;

    public TetMesh Mesh => _t;

    private static string F(FormattableString s) => s.ToString(Inv);

    private TetMesh BuildMesh(IsosurfaceStuffing.Result res) => BuildMesh(res.Points, res.Tets);

    private TetMesh BuildMesh(Vec3[] points, int[] tets) =>
        new(points.Length, tets, points.Select(p => _symX && p.X == 0).ToArray(), points.Select(p => _symY && p.Y == 0).ToArray());

    /// <summary>Places where an increment failed; later remeshes refine around them (die corners are fixed in space).</summary>
    private readonly List<(Vec3 At, int Level)> _hotSpots = [];

    /// <summary>Target element size at p: h, reduced in thin flash (closed gap &lt; 2.5·h) and around hot spots, down to h / 2^levels.</summary>
    private double TargetSize(Vec3 p)
    {
        int levels = _cfg.RefinementLevels;
        if (levels <= 0) return _h;
        double hMin = _h / (1 << levels), t = _h;
        double zb = _cfg.RefineThinFlash ? _die.Bottom.SurfaceZ(p.X, p.Y) : double.NaN, zt = _cfg.RefineThinFlash ? _die.TopClosed.SurfaceZ(p.X, p.Y) + _gap : double.NaN;
        if (_cfg.RefineThinFlash && double.IsFinite(zb) && double.IsFinite(zt) && zt - zb < 2.5 * _h) t = Math.Min(t, Math.Max((zt - zb) / 2.5, hMin));
        foreach (var (at, level) in _hotSpots)
        {
            if ((p - at).LengthSquared < 4 * _h * _h) t = Math.Min(t, Math.Max(_h / (1 << level), hMin));
        }

        return t;
    }

    private void ForceContact(NodeContact side)
    {
        int best = -1;
        double bestC = double.MaxValue;
        for (int n = 0; n < _nn; n++)
        {
            if (!_t.IsSurfaceNode[n]) continue;
            double c = side == NodeContact.BottomDie ? _die.Bottom.Clearance(_x[n], 0) : _die.TopClosed.Clearance(_x[n], _gap);
            if (c < bestC) { bestC = c; best = n; }
        }

        if (best >= 0) _contact[best] = side;
    }

    private void LogBanner(double r0, double dim)
    {
        _log(F($"ImpressionFEA native FEM · 3D rigid-viscoplastic · ANP linear tetrahedra · {(_symX ? "quarter" : _symY ? "half" : "full")} model"));
        _log(F($"Die set      : {_die.Name}"));
        _log(F($"Material     : {_cfg.Material.Name} @ {_cfg.Temperature:F0} °C  →  {_law}"));
        if (!_cfg.Material.InRange(_cfg.Temperature))
            _log(F($"WARNING      : {_cfg.Temperature:F0} °C is outside the calibrated window {_cfg.Material.TMin:F0}–{_cfg.Material.TMax:F0} °C; flow stress is extrapolated."));
        if (_die.IsImported)
        {
            var (seat, _) = _die.Place(r0, dim);
            _log(F($"Workpiece    : imported solid, volume {_vol0 / 1000:F2} cm³ (CAD {_die.ImportedBillet!.Volume / 1000:F2} cm³), dropped {-seat:F3} mm onto the bottom die (cavity {_die.CavityVolume() / 1000:F2} cm³)"));
        }
        else
        {
            _log(F($"Billet       : {(_die.Billet == BilletShape.VerticalCylinder ? "cylinder" : "bar")} R {r0:F2} × {(_die.Billet == BilletShape.VerticalCylinder ? "H" : "L")} {dim:F2} mm, volume {_vol0 / 1000:F2} cm³ (cavity {_die.CavityVolume() / 1000:F2} cm³)"));
        }
        _log(F($"Mesh         : {_ne} tets, {_nn} nodes, {_ndof} DOF, {_colPtr[^1]:N0} non-zeros (h = {_h:F2} mm)"));
        _log(F($"Press        : ram {_ram:F1} mm/s, stroke {_gap0:F3} mm, Δs {_cfg.StrokeIncrement:F3} mm, ~{NominalSteps} increments"));
        _log(F($"Linear solver: {_cfg.LinearBackend} — available: {string.Join(" | ", SpdSolverFactory.Describe())}"));
        _log(F($"Numerics     : ANP penalty K {_kPen:E2} MPa·s, ε̇₀ {_rateCut:E2} 1/s, friction m {_cfg.FrictionFactor:F2}, tol {_cfg.Tolerance:E1}"));
        _log(F($"Robustness   : auto-step {(_cfg.AutoStep ? "on" : "off")}, ≤ {_cfg.MaxCutbacks} cutbacks, local refinement {(_cfg.RefinementLevels > 0 ? F($"to h/{1 << _cfg.RefinementLevels}{(_cfg.RefineThinFlash ? " (trouble spots + thin flash)" : " (trouble spots)")}") : "off")}, remesh below q {_cfg.RemeshQuality:F2}, node budget {_cfg.MaxNodeCount:N0}"));
    }

    private void Allocate(TetMesh mesh, Vec3[] x)
    {
        _t = mesh;
        _nn = mesh.NodeCount;
        _ne = mesh.ElementCount;
        _ndof = mesh.DofCount;
        _x = x;
        _vg = new Vec3[_nn];
        _eps = new double[_ne];
        _contact = new NodeContact[_nn];
        _ax0 = new Vec3[_nn];
        _ax1 = new Vec3[_nn];
        _ax2 = new Vec3[_nn];
        _nFix = new int[_nn];
        _dieAxis = new int[_nn];
        _fixed = new bool[_ndof];
        _fixedVal = new double[_ndof];
        _release = new bool[_nn];
        _kFric = new double[_nn];
        _aFric = new double[_nn];
        _elSig = new double[_ne];
        _elRate = new double[_ne];
        _elMean = new double[_ne];
        _grad = new double[12 * _ne];
        _vol = new double[_ne];
        _v = new double[_ndof];
        _r = new double[_ndof];
        _dv = new double[_ndof];
        _vTrial = new double[_ndof];
        _rvol = new double[_ndof];
        _vNode = new double[_nn];
        BuildPattern();
    }

    /// <summary>Sparsity pattern: nodes i, j couple when both lie in the patch of some node (ANP two-ring stencil).</summary>
    private void BuildPattern()
    {
        var sets = new HashSet<int>[_nn];
        for (int n = 0; n < _nn; n++) sets[n] = [];
        var patch = new List<int>();
        for (int n = 0; n < _nn; n++)
        {
            patch.Clear();
            foreach (int e in _t.TetsOf(n))
            {
                for (int a = 0; a < 4; a++) patch.Add(_t.Tets[4 * e + a]);
            }

            foreach (int i in patch)
            {
                foreach (int j in patch) sets[i].Add(j);
            }
        }

        _nbr = new int[_nn][];
        for (int n = 0; n < _nn; n++)
        {
            var arr = sets[n].ToArray();
            Array.Sort(arr);
            _nbr[n] = arr;
        }

        _colPtr = new int[_ndof + 1];
        for (int j = 0; j < _nn; j++)
        {
            for (int c = 0; c < 3; c++) _colPtr[3 * j + c + 1] = _colPtr[3 * j + c] + 3 * _nbr[j].Length;
        }

        _rowIdx = new int[_colPtr[^1]];
        for (int j = 0; j < _nn; j++)
        {
            for (int c = 0; c < 3; c++)
            {
                int p = _colPtr[3 * j + c];
                foreach (int i in _nbr[j])
                {
                    _rowIdx[p++] = 3 * i;
                    _rowIdx[p++] = 3 * i + 1;
                    _rowIdx[p++] = 3 * i + 2;
                }
            }
        }

        _val = new double[_rowIdx.Length];
        _kvol = new double[_rowIdx.Length];
        _needAnalyze = true;
        _haveFactor = false;
    }

    /// <summary>Index of entry (row node i, comp ci; column node j, comp cj).</summary>
    private int Pos(int i, int ci, int j, int cj) => _colPtr[3 * j + cj] + 3 * Array.BinarySearch(_nbr[j], i) + ci;

    private void AddBlock(double[] target, int i, int j, double[] m, int stride, int r0, int c0, double scale)
    {
        int k = Array.BinarySearch(_nbr[j], i);
        for (int cj = 0; cj < 3; cj++)
        {
            int p = _colPtr[3 * j + cj] + 3 * k;
            for (int ci = 0; ci < 3; ci++) target[p + ci] += scale * m[(r0 + ci) * stride + c0 + cj];
        }
    }

    // ------------------------------------------------------------------ stepping

    public ForgingSnapshot Initial() => MakeSnapshot(0, 0, 0);

    public ForgingSnapshot Step(CancellationToken ct)
    {
        if (IsFinished) throw new InvalidOperationException("Simulation already reached the closed-die position.");
        StepIndex++;
        var sw = Stopwatch.StartNew();
        string? trigger = RemeshTrigger();
        if (trigger is not null) Remesh(trigger);

        int iterations = 0;
        double load = 0, reaction = 0, dt = 0;
        bool ok = false;
        string why = "";
        int usedCutback = 0;
        // Recovery ladder: (0) as is → (1) remesh → (2) remesh with local refinement around the failure → (3) one
        // more refinement level there. Each pass re-solves the velocity field on the new mesh.
        const int Passes = 4;
        for (int pass = 0; pass < Passes && !ok; pass++)
        {
            _recoveryPass = pass;
            PrepareStep();
            iterations = SolveVelocity(ct, out bool converged);
            if (!converged)
            {
                if (_lastRel < 1e-2)
                {
                    _log(F($"  WARNING step {StepIndex}: not fully converged (|Δv|/|v| = {_lastRel:E2}); continuing."));
                }
                else if (pass < Passes - 1 && Remesh("stalled velocity solution"))
                {
                    continue;
                }
                else
                {
                    throw new SolverDivergenceException(F($"Velocity solution did not converge at step {StepIndex} (stroke {_gap0 - _gap:F3} mm): |Δv|/|v| = {_lastRel:E2}."));
                }
            }

            double power = FinalEvaluation(out reaction);
            load = power / _ram * _symFactor;
            reaction *= _symFactor;
            double ds = _cfg.AutoStep ? _ds : _cfg.StrokeIncrement;
            dt = Math.Min(ds, _gap) / _ram;
            if (_gap - _ram * dt < 0.05 * ds) dt = _gap / _ram;
            for (int attempt = 0; attempt <= _cfg.MaxCutbacks && !ok; attempt++)
            {
                double dta = dt / (1 << attempt);
                ok = TryUpdate(dta, out why);
                if (ok)
                {
                    dt = dta;
                    usedCutback = attempt;
                }
                else
                {
                    _log(F($"  cutback {attempt + 1}: Δs {_ram * dta:F4} mm — {why}"));
                }
            }

            if (ok || pass == Passes - 1) break;
            if (pass >= 1 && _cfg.RefinementLevels > 0 && _failAt is { } at)
            {
                int level = Math.Min(pass, _cfg.RefinementLevels);
                _hotSpots.Add((at, level));
                if (_hotSpots.Count > 64) _hotSpots.RemoveAt(0);
                _log(F($"  recovery {pass}: local refinement to h/{1 << level} around ({at.X:F1}, {at.Y:F1}, {at.Z:F1}) mm"));
            }

            // A rejected remesh does not end the ladder: the next pass relaxes the corner tolerance / refines further.
            Remesh(pass == 0 ? "fold during update" : F($"recovery {pass}"));
        }

        _recoveryPass = 0;

        if (!ok)
            throw new MeshDistortionException(F($"Mesh distortion at step {StepIndex} (stroke {_gap0 - _gap:F3} mm): {why}. Automatic cutbacks, remeshing and local refinement could not recover. Results up to the previous increment are kept; try a smaller h, more refinement levels (Advanced) or larger fillets on sharp die edges."));

        // Auto time-stepping: keep the increment that worked, grow back towards Δs after clean increments.
        if (_cfg.AutoStep)
        {
            double used = _ram * dt;
            _ds = usedCutback > 0 ? Math.Max(used, _cfg.StrokeIncrement / (1 << _cfg.MaxCutbacks)) : Math.Min(_cfg.StrokeIncrement, 1.5 * _ds);
            if (usedCutback > 0) _log(F($"  auto-step: next Δs {_ds:F4} mm"));
        }

        for (int e = 0; e < _ne; e++) _eps[e] += _elRate[e] * dt;
        _warm = true;
        var snap = MakeSnapshot(StepIndex, load, iterations);
        _log(F($"step {StepIndex,4} | stroke {snap.Stroke,8:F3} mm | it {iterations,2} | load {load / 9806.65,9:F2} t (reaction {reaction / 9806.65:F2} t) | fill {snap.FillPercent,6:F2} % | ΔV {snap.VolumeChangePercent,6:F3} % | {_ne} tets | {sw.Elapsed.TotalMilliseconds,7:F0} ms"));
        return snap;
    }

    // ------------------------------------------------------------------ per-step preparation

    private int Dof(int n, int c) => 3 * n + c;

    private Vec3 Axis(int n, int k) => k == 0 ? _ax0[n] : k == 1 ? _ax1[n] : _ax2[n];

    private static Vec3 DieVelocity(NodeContact c, double ram) => c == NodeContact.TopDie ? new Vec3(0, 0, -ram) : default;

    private IRigidDie Surface(NodeContact c) => c == NodeContact.TopDie ? _die.TopClosed : _die.Bottom;

    private double Offset(NodeContact c, double gap) => c == NodeContact.TopDie ? gap : 0.0;

    private Vec3 Place(Vec3 p, NodeContact c, int n)
    {
        if (c != NodeContact.Free) p = Surface(c).Project(p, Offset(c, _gap), out _);
        return Sym(p, n);
    }

    private Vec3 Sym(Vec3 p, int n) => new(_t.OnX0[n] ? 0 : p.X, _t.OnY0[n] ? 0 : p.Y, p.Z);

    private void PrepareStep()
    {
        Array.Clear(_fixed);
        Array.Clear(_fixedVal);
        Array.Clear(_release);
        Span<Vec3> dirs = stackalloc Vec3[3];
        Span<double> vals = stackalloc double[3];
        Span<Vec3> e = stackalloc Vec3[3];
        Span<double> ev = stackalloc double[3];
        for (int n = 0; n < _nn; n++)
        {
            int k = 0;
            _dieAxis[n] = -1;
            if (_t.OnX0[n]) { dirs[k] = Vec3.UnitX; vals[k] = 0; k++; }
            if (_t.OnY0[n]) { dirs[k] = Vec3.UnitY; vals[k] = 0; k++; }
            int dieIdx = -1;
            if (_contact[n] != NodeContact.Free)
            {
                Surface(_contact[n]).Project(_x[n], Offset(_contact[n], _gap), out var nrm);
                dirs[k] = nrm;
                vals[k] = DieVelocity(_contact[n], _ram).Dot(nrm);
                dieIdx = k;
                k++;
            }

            // Gram–Schmidt on the constraint directions.
            int m = 0;
            for (int q = 0; q < k; q++)
            {
                var d = dirs[q];
                double val = vals[q];
                for (int s = 0; s < m; s++)
                {
                    double proj = d.Dot(e[s]);
                    d -= proj * e[s];
                    val -= proj * ev[s];
                }

                double len = d.Length;
                if (len < 0.2) continue; // (nearly) redundant constraint
                e[m] = (1 / len) * d;
                ev[m] = val / len;
                if (q == dieIdx) _dieAxis[n] = m;
                m++;
            }

            if (m == 0) { e[0] = Vec3.UnitX; e[1] = Vec3.UnitY; e[2] = Vec3.UnitZ; }
            else if (m == 1)
            {
                var helper = Math.Abs(e[0].X) < 0.8 ? Vec3.UnitX : Vec3.UnitY;
                e[1] = e[0].Cross(helper).Normalized();
                e[2] = e[0].Cross(e[1]);
            }
            else if (m == 2)
            {
                e[2] = e[0].Cross(e[1]).Normalized();
            }

            _ax0[n] = e[0];
            _ax1[n] = e[1];
            _ax2[n] = e[2];
            _nFix[n] = m;
            for (int q = 0; q < m; q++)
            {
                _fixed[Dof(n, q)] = true;
                _fixedVal[Dof(n, q)] = ev[q];
            }
        }

        // Element geometry (constant within the step).
        var t = _t.Tets;
        for (int el = 0; el < _ne; el++)
        {
            var p0 = _x[t[4 * el]];
            var a = _x[t[4 * el + 1]] - p0;
            var b = _x[t[4 * el + 2]] - p0;
            var c = _x[t[4 * el + 3]] - p0;
            double det = a.Dot(b.Cross(c));
            if (!(det > 0)) throw new MeshDistortionException(F($"Tetrahedron {el} is inverted at step {StepIndex}."));
            // rows of J⁻¹ (J columns a, b, c) are the gradients of N1..N3
            var g1 = (1 / det) * b.Cross(c);
            var g2 = (1 / det) * c.Cross(a);
            var g3 = (1 / det) * a.Cross(b);
            var g0 = -(g1 + g2 + g3);
            int o = 12 * el;
            foreach (var (gi, q) in new[] { (g0, 0), (g1, 1), (g2, 2), (g3, 3) })
            {
                _grad[o + 3 * q] = gi.X;
                _grad[o + 3 * q + 1] = gi.Y;
                _grad[o + 3 * q + 2] = gi.Z;
            }

            _vol[el] = det / 6;
        }

        BuildAnp();
        BuildFriction();
        LoadVelocityGuess();
    }

    /// <summary>Averaged nodal volumetric operator and the (rotated) penalty matrix K_vol = Σ κ V_n g_n g_nᵀ.</summary>
    private void BuildAnp()
    {
        var t = _t.Tets;
        Array.Clear(_vNode);
        for (int e = 0; e < _ne; e++)
        {
            for (int a = 0; a < 4; a++) _vNode[t[4 * e + a]] += _vol[e] / 4;
        }

        var start = new int[_nn + 1];
        var nodes = new List<int>();
        var coefs = new List<Vec3>();
        var map = new Dictionary<int, int>();
        for (int n = 0; n < _nn; n++)
        {
            map.Clear();
            foreach (int e in _t.TetsOf(n))
            {
                double w = _vol[e] / 4 / _vNode[n];
                for (int a = 0; a < 4; a++)
                {
                    int m = t[4 * e + a];
                    var gc = w * new Vec3(_grad[12 * e + 3 * a], _grad[12 * e + 3 * a + 1], _grad[12 * e + 3 * a + 2]);
                    if (map.TryGetValue(m, out int idx)) coefs[idx] += gc;
                    else
                    {
                        map[m] = nodes.Count;
                        nodes.Add(m);
                        coefs.Add(gc);
                    }
                }
            }

            start[n + 1] = nodes.Count;
        }

        _gStart = start;
        _gNode = nodes.ToArray();
        _gCoef = coefs.ToArray();

        Array.Clear(_kvol);
        var block = new double[9];
        for (int n = 0; n < _nn; n++)
        {
            double kv = _kPen * _vNode[n];
            for (int p = _gStart[n]; p < _gStart[n + 1]; p++)
            {
                int i = _gNode[p];
                var ci = ToLocal(i, _gCoef[p]);
                for (int q = _gStart[n]; q < _gStart[n + 1]; q++)
                {
                    int j = _gNode[q];
                    var cj = ToLocal(j, _gCoef[q]);
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++) block[3 * r + c] = ci[r] * cj[c];
                    }

                    AddBlock(_kvol, i, j, block, 3, 0, 0, kv);
                }
            }
        }
    }

    private Vec3 ToLocal(int n, Vec3 g) => new(g.Dot(_ax0[n]), g.Dot(_ax1[n]), g.Dot(_ax2[n]));

    private Vec3 ToGlobal(int n, double l0, double l1, double l2) => l0 * _ax0[n] + l1 * _ax1[n] + l2 * _ax2[n];

    private void BuildFriction()
    {
        Array.Clear(_aFric);
        var ksum = new double[_nn];
        var kcnt = new int[_nn];
        var f = _t.BoundaryFaces;
        for (int k = 0; k < _t.FaceIsSymmetry.Length; k++)
        {
            if (_t.FaceIsSymmetry[k]) continue;
            int a = f[3 * k], b = f[3 * k + 1], c = f[3 * k + 2];
            double area = 0.5 * (_x[b] - _x[a]).Cross(_x[c] - _x[a]).Length;
            double kk = _elSig[_t.BoundaryFaceOwner[k]] / Math.Sqrt(3);
            foreach (int n in new[] { a, b, c })
            {
                _aFric[n] += area / 3;
                ksum[n] += kk;
                kcnt[n]++;
            }
        }

        for (int n = 0; n < _nn; n++) _kFric[n] = kcnt[n] > 0 ? ksum[n] / kcnt[n] : 0;
    }

    private void LoadVelocityGuess()
    {
        for (int n = 0; n < _nn; n++)
        {
            var l = ToLocal(n, _vg[n]);
            _v[Dof(n, 0)] = l.X;
            _v[Dof(n, 1)] = l.Y;
            _v[Dof(n, 2)] = l.Z;
        }

        for (int d = 0; d < _ndof; d++)
        {
            if (_fixed[d]) _v[d] = _fixedVal[d];
        }
    }

    private Vec3 GlobalVelocity(double[] v, int n) => ToGlobal(n, v[3 * n], v[3 * n + 1], v[3 * n + 2]);

    // ------------------------------------------------------------------ nonlinear solve

    private int SolveVelocity(CancellationToken ct, out bool converged)
    {
        converged = false;
        _lastRel = double.PositiveInfinity;
        int it;
        for (it = 1; it <= _cfg.MaxIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            // After a converged increment on the same mesh the previous velocity is an excellent start: go straight to Newton.
            // Newton for fast convergence; if it stalls (typically during the final flash squeeze, where contact and
            // the rate cut-off switch state), fall back to Picard, which decreases the convex functional monotonically.
            bool newton = (_warm || it > 2) && it <= NewtonLimit;
            double e0 = Assemble(true, newton, _v, out _);
            double maxDiag = 0;
            for (int j = 0; j < _ndof; j++)
            {
                for (int p = _colPtr[j]; p < _colPtr[j + 1]; p++)
                {
                    int i = _rowIdx[p];
                    if (_fixed[i] || _fixed[j]) _val[p] = i == j ? 1 : 0;
                    else if (i == j) maxDiag = Math.Max(maxDiag, _val[p]);
                }
            }

            // Tiny absolute-velocity regularisation removes rigid-body modes not held by friction (m = 0).
            double reg = 1e-10 * maxDiag;
            for (int j = 0; j < _ndof; j++)
            {
                if (!_fixed[j]) _val[Pos(j / 3, j % 3, j / 3, j % 3)] += reg;
            }

            for (int d = 0; d < _ndof; d++)
            {
                if (_fixed[d]) _r[d] = 0;
            }

            double rnorm = Norm(_r);
            long t0 = Stopwatch.GetTimestamp();
            for (int d = 0; d < _ndof; d++) _r[d] = -_r[d];
            string how = SolveLinear(_r, _dv);
            double cholMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            double slope = 0;
            for (int d = 0; d < _ndof; d++) slope -= _r[d] * _dv[d];
            double beta = 1;
            for (int ls = 0; ls < 8; ls++)
            {
                for (int d = 0; d < _ndof; d++) _vTrial[d] = _v[d] + beta * _dv[d];
                double e1 = Assemble(false, false, _vTrial, out _);
                if (e1 <= e0 + 1e-4 * beta * slope + 1e-11 * Math.Abs(e0)) break;
                beta *= 0.5;
            }

            Array.Copy(_vTrial, _v, _ndof);
            double rel = beta * Norm(_dv) / Math.Max(Norm(_v), 1e-30);
            _lastRel = rel;
            _log(F($"    it {it,2} {(newton ? "NR" : "PI")}  |R| {rnorm,10:E3} N  |Δv|/|v| {rel,9:E2}  λ {beta,4:F2}  {how,-9} {cholMs,7:F1} ms"));
            if (double.IsNaN(rel)) throw new SolverDivergenceException(F($"Velocity solution became NaN at step {StepIndex}."));
            if (rel < _cfg.Tolerance)
            {
                converged = true;
                return it;
            }
        }

        return it - 1;
    }

    private bool _warm;

    /// <summary>Newton iterations before switching to Picard within one increment.</summary>
    private const int NewtonLimit = 20;

    private double[] _pr = [];
    private double[] _pz = [];
    private double[] _pp = [];
    private double[] _pq = [];

    /// <summary>
    /// Solves A·x = b. A Cholesky factor of an earlier matrix on the same mesh preconditions conjugate gradients (the
    /// dominant penalty block changes little between iterations and steps); the matrix is refactorised only when
    /// PCG stalls. Returns a short description for the log.
    /// </summary>
    private string SolveLinear(double[] b, double[] x)
    {
        if (_pr.Length != _ndof)
        {
            _pr = new double[_ndof];
            _pz = new double[_ndof];
            _pp = new double[_ndof];
            _pq = new double[_ndof];
        }

        if (_haveFactor)
        {
            // Keep iterating as long as it is cheaper than refactorising.
            int budget = (int)Math.Clamp(0.8 * _factorMs / Math.Max(_pcgIterMs, 1e-3), 6, 400);
            long p0 = Stopwatch.GetTimestamp();
            int its = Pcg(b, x, 1e-7, budget, out int done);
            _pcgIterMs = Stopwatch.GetElapsedTime(p0).TotalMilliseconds / Math.Max(done + 1, 1);
            if (its >= 0) return F($"pcg {its}");
        }

        long f0 = Stopwatch.GetTimestamp();
        try
        {
            if (_needAnalyze)
            {
                _lin.Analyze(_ndof, _colPtr, _rowIdx);
                _needAnalyze = false;
            }

            _lin.Factorize(_val);
            _lin.Solve(b, x);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SimulationException)
        {
            _haveFactor = false;
            throw new SolverDivergenceException(F($"Sparse factorisation failed at step {StepIndex} ({_lin.Name}): {ex.Message}"));
        }

        _haveFactor = true;
        _factorMs = Stopwatch.GetElapsedTime(f0).TotalMilliseconds;
        return _lin.FactorNonZeros > 0 ? F($"fact {_lin.FactorNonZeros / 1e6:F1}M") : "fact";
    }

    /// <summary>Releases native solver resources (GPU memory, PARDISO workspace).</summary>
    public void Dispose()
    {
        _linImpl?.Dispose();
        _linImpl = null;
    }

    /// <summary>Linear solver backend, created on first use (setup previews never touch the GPU).</summary>
    private ISpdSolver _lin
    {
        get
        {
            if (_linImpl is null)
            {
                _linImpl = SpdSolverFactory.Create(_cfg.LinearBackend, _log);
                _log($"Linear solver: {_linImpl.Name}");
            }

            return _linImpl;
        }
    }

    private double _factorMs = 1000;
    private double _pcgIterMs = 10;

    /// <summary>Preconditioned CG with the cached Cholesky factor; returns iterations or -1 if not converged.</summary>
    private int Pcg(double[] b, double[] x, double tol, int maxIt, out int done)
    {
        done = 0;
        double bnorm = Norm(b);
        if (bnorm == 0)
        {
            Array.Clear(x);
            return 0;
        }

        _lin.Solve(b, x); // x0 = M⁻¹ b
        MatVec(x, _pq);
        for (int i = 0; i < _ndof; i++) _pr[i] = b[i] - _pq[i];
        _lin.Solve(_pr, _pz);
        Array.Copy(_pz, _pp, _ndof);
        double rz = Dot(_pr, _pz);
        for (int it = 0; it < maxIt; it++)
        {
            done = it;
            if (Norm(_pr) <= tol * bnorm) return it;
            MatVec(_pp, _pq);
            double alpha = rz / Dot(_pp, _pq);
            for (int i = 0; i < _ndof; i++)
            {
                x[i] += alpha * _pp[i];
                _pr[i] -= alpha * _pq[i];
            }

            _lin.Solve(_pr, _pz);
            double rzNew = Dot(_pr, _pz);
            double beta = rzNew / rz;
            rz = rzNew;
            for (int i = 0; i < _ndof; i++) _pp[i] = _pz[i] + beta * _pp[i];
        }

        return Norm(_pr) <= tol * bnorm ? maxIt : -1;
    }

    private void MatVec(double[] v, double[] y)
    {
        Array.Clear(y);
        for (int j = 0; j < _ndof; j++)
        {
            double vj = v[j];
            if (vj == 0) continue;
            for (int p = _colPtr[j]; p < _colPtr[j + 1]; p++) y[_rowIdx[p]] += _val[p] * vj;
        }
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double Norm(double[] a)
    {
        double s = 0;
        foreach (double x in a) s += x * x;
        return Math.Sqrt(s);
    }

    /// <summary>Residual (and optionally matrix) at v in local nodal frames; returns Π(v).</summary>
    private double Assemble(bool matrix, bool newton, double[] v, out double power)
    {
        // Volumetric part: R = K_vol v, Π_vol = ½ vᵀ K_vol v.
        Array.Clear(_rvol);
        for (int j = 0; j < _ndof; j++)
        {
            double vj = v[j];
            if (vj == 0) continue;
            for (int p = _colPtr[j]; p < _colPtr[j + 1]; p++) _rvol[_rowIdx[p]] += _kvol[p] * vj;
        }

        double energy = 0;
        for (int d = 0; d < _ndof; d++) energy += 0.5 * v[d] * _rvol[d];
        power = 2 * energy;
        Array.Copy(_rvol, _r, _ndof);
        if (matrix) Array.Copy(_kvol, _val, _val.Length);

        var t = _t.Tets;
        Span<double> blk = stackalloc double[9];
        var tmp = new double[9];
        Span<double> s = stackalloc double[3];
        for (int e = 0; e < _ne; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                var g = GlobalVelocity(v, t[4 * e + a]);
                _ve[3 * a] = g.X;
                _ve[3 * a + 1] = g.Y;
                _ve[3 * a + 2] = g.Z;
            }

            Kernel(e, matrix, newton, ref energy, ref power);

            for (int a = 0; a < 4; a++)
            {
                int n = t[4 * e + a];
                var rl = ToLocal(n, new Vec3(_re[3 * a], _re[3 * a + 1], _re[3 * a + 2]));
                _r[3 * n] += rl.X;
                _r[3 * n + 1] += rl.Y;
                _r[3 * n + 2] += rl.Z;
            }

            if (!matrix) continue;
            for (int a = 0; a < 4; a++)
            {
                int i = t[4 * e + a];
                for (int b = 0; b < 4; b++)
                {
                    int j = t[4 * e + b];
                    // R_i K_ab R_jᵀ
                    for (int r = 0; r < 3; r++)
                    {
                        var ar = Axis(i, r);
                        for (int c = 0; c < 3; c++)
                        {
                            blk[3 * r + c] = ar.X * _ke[(3 * a) * 12 + 3 * b + c] + ar.Y * _ke[(3 * a + 1) * 12 + 3 * b + c] + ar.Z * _ke[(3 * a + 2) * 12 + 3 * b + c];
                        }
                    }

                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            var bc = Axis(j, c);
                            tmp[3 * r + c] = blk[3 * r] * bc.X + blk[3 * r + 1] * bc.Y + blk[3 * r + 2] * bc.Z;
                        }
                    }

                    AddBlock(_val, i, j, tmp, 3, 0, 0, 1);
                }
            }
        }

        // Vector shear friction on the free (tangential) axes of contact nodes.
        double m = _cfg.FrictionFactor;
        if (m > 0)
        {
            for (int n = 0; n < _nn; n++)
            {
                if (_contact[n] == NodeContact.Free || _nFix[n] >= 3) continue;
                double c = m * _kFric[n] * _aFric[n] * (2 / Math.PI);
                if (c <= 0) continue;
                var vd = ToLocal(n, DieVelocity(_contact[n], _ram));
                int k0 = _nFix[n];
                s.Clear();
                double s2 = 0;
                for (int q = k0; q < 3; q++)
                {
                    s[q] = v[3 * n + q] - vd[q];
                    s2 += s[q] * s[q];
                }

                double sl = Math.Sqrt(s2), z = sl / _u0, at = Math.Atan(z);
                energy += c * (sl * at - 0.5 * _u0 * Math.Log(1 + z * z));
                power += c * at * sl;
                double gs = sl > 1e-12 * _u0 ? at / sl : 1 / _u0; // secant coefficient f/|s|
                for (int q = k0; q < 3; q++) _r[3 * n + q] += c * gs * s[q];
                if (!matrix) continue;
                double tang = c / (_u0 * (1 + z * z));
                for (int q = k0; q < 3; q++)
                {
                    for (int p = k0; p < 3; p++)
                    {
                        double kqp;
                        if (newton && sl > 1e-12 * _u0)
                            kqp = c * gs * ((q == p ? 1 : 0) - s[q] * s[p] / s2) + tang * s[q] * s[p] / s2;
                        else
                            kqp = q == p ? c * gs : 0;
                        _val[Pos(n, q, n, p)] += kqp;
                    }
                }
            }
        }

        return energy;
    }

    private void Kernel(int e, bool matrix, bool newton, ref double energy, ref double power)
    {
        double vol = _vol[e];
        int o = 12 * e;
        Array.Clear(_b);
        for (int a = 0; a < 4; a++)
        {
            double bx = _grad[o + 3 * a], by = _grad[o + 3 * a + 1], bz = _grad[o + 3 * a + 2];
            int c = 3 * a;
            _b[0 * 12 + c] = bx;
            _b[1 * 12 + c + 1] = by;
            _b[2 * 12 + c + 2] = bz;
            _b[3 * 12 + c] = by; _b[3 * 12 + c + 1] = bx;
            _b[4 * 12 + c + 1] = bz; _b[4 * 12 + c + 2] = by;
            _b[5 * 12 + c] = bz; _b[5 * 12 + c + 2] = bx;
        }

        Span<double> eps = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            double s = 0;
            for (int k = 0; k < 12; k++) s += _b[i * 12 + k] * _ve[k];
            eps[i] = s;
        }

        double ev3 = (eps[0] + eps[1] + eps[2]) / 3;
        double d0 = eps[0] - ev3, d1 = eps[1] - ev3, d2 = eps[2] - ev3;
        double rate = Math.Sqrt(Math.Max(2.0 / 3.0 * (d0 * d0 + d1 * d1 + d2 * d2) + (eps[3] * eps[3] + eps[4] * eps[4] + eps[5] * eps[5]) / 3.0, 0));
        Span<double> me = stackalloc double[6];
        me[0] = 2.0 / 3.0 * d0; me[1] = 2.0 / 3.0 * d1; me[2] = 2.0 / 3.0 * d2;
        me[3] = eps[3] / 3; me[4] = eps[4] / 3; me[5] = eps[5] / 3;
        for (int k = 0; k < 12; k++)
        {
            double s = 0;
            for (int i = 0; i < 6; i++) s += _b[i * 12 + k] * me[i];
            _g[k] = s;
        }

        double strain = _eps[e];
        double sigCut = _law.Stress(strain, _rateCut);
        double mu0 = sigCut / _rateCut, mExp = _law.M;
        double mu, sig, wDens, coefN;
        if (rate > _rateCut)
        {
            sig = _law.Stress(strain, rate);
            mu = sig / rate;
            wDens = 0.5 * sigCut * _rateCut + (sig * rate - sigCut * _rateCut) / (mExp + 1);
            coefN = (mExp - 1) * mu / (rate * rate);
        }
        else
        {
            mu = mu0;
            sig = mu0 * rate;
            wDens = 0.5 * mu0 * rate * rate;
            coefN = 0;
        }

        energy += vol * wDens;
        power += vol * sig * rate;
        for (int k = 0; k < 12; k++) _re[k] = vol * mu * _g[k];
        _elSig[e] = sig;
        _elRate[e] = rate;
        if (!matrix) return;

        for (int k = 0; k < 12; k++)
        {
            double tr = (_b[k] + _b[12 + k] + _b[24 + k]) / 3;
            _mb[k] = 2.0 / 3.0 * (_b[k] - tr);
            _mb[12 + k] = 2.0 / 3.0 * (_b[12 + k] - tr);
            _mb[24 + k] = 2.0 / 3.0 * (_b[24 + k] - tr);
            _mb[36 + k] = _b[36 + k] / 3;
            _mb[48 + k] = _b[48 + k] / 3;
            _mb[60 + k] = _b[60 + k] / 3;
        }

        double wm = vol * mu, wn = newton ? vol * coefN : 0;
        for (int p = 0; p < 12; p++)
        {
            for (int q = p; q < 12; q++)
            {
                double s = 0;
                for (int i = 0; i < 6; i++) s += _b[i * 12 + p] * _mb[i * 12 + q];
                double val = wm * s + wn * _g[p] * _g[q];
                _ke[p * 12 + q] = val;
                _ke[q * 12 + p] = val;
            }
        }
    }

    private double FinalEvaluation(out double reaction)
    {
        Assemble(false, false, _v, out double power);
        reaction = 0;
        double maxB = 0, maxT = 0;
        for (int n = 0; n < _nn; n++)
        {
            _vg[n] = GlobalVelocity(_v, n);
            if (_contact[n] == NodeContact.Free) continue;
            var rg = ToGlobal(n, _r[3 * n], _r[3 * n + 1], _r[3 * n + 2]);
            if (_contact[n] == NodeContact.TopDie) reaction -= rg.Z;
            if (_dieAxis[n] < 0) continue;
            double comp = _r[3 * n + _dieAxis[n]];
            if (_contact[n] == NodeContact.TopDie) maxT = Math.Max(maxT, comp); else maxB = Math.Max(maxB, comp);
        }

        int keptB = 0, keptT = 0;
        for (int n = 0; n < _nn; n++)
        {
            if (_contact[n] == NodeContact.Free || _dieAxis[n] < 0) continue;
            double comp = _r[3 * n + _dieAxis[n]];
            double thr = 0.02 * (_contact[n] == NodeContact.TopDie ? maxT : maxB);
            if (comp < -thr) _release[n] = true;
            else if (_contact[n] == NodeContact.TopDie) keptT++;
            else keptB++;
        }

        if (keptB == 0 || keptT == 0)
        {
            for (int n = 0; n < _nn; n++)
            {
                if ((keptT == 0 && _contact[n] == NodeContact.TopDie) || (keptB == 0 && _contact[n] == NodeContact.BottomDie)) _release[n] = false;
            }
        }

        // Element mean stress from the nodal (ANP) volumetric strain rates.
        var nodal = new double[_nn];
        for (int n = 0; n < _nn; n++)
        {
            double ev = 0;
            for (int p = _gStart[n]; p < _gStart[n + 1]; p++) ev += _gCoef[p].Dot(_vg[_gNode[p]]);
            nodal[n] = _kPen * ev;
        }

        var t = _t.Tets;
        for (int e = 0; e < _ne; e++) _elMean[e] = 0.25 * (nodal[t[4 * e]] + nodal[t[4 * e + 1]] + nodal[t[4 * e + 2]] + nodal[t[4 * e + 3]]);
        return power;
    }

    // ------------------------------------------------------------------ geometry update

    private bool TryUpdate(double dt, out string why)
    {
        double gapT = Math.Max(0, _gap - _ram * dt);
        if (gapT < 1e-9) gapT = 0;
        var xT = new Vec3[_nn];
        var cT = new NodeContact[_nn];
        var pred = new Vec3[_nn];
        var nrm = SurfaceNormals(_t, _x);
        double tolSep = 0.05 * _h, maxPen = 0;
        for (int n = 0; n < _nn; n++)
        {
            var p = Sym(_x[n] + dt * _vg[n], n);
            pred[n] = p;
            var c = _contact[n];
            if (c != NodeContact.Free && !_release[n])
            {
                var surf = Surface(c);
                double off = Offset(c, gapT);
                if (surf.Clearance(p, off) > tolSep) cT[n] = NodeContact.Free;
                else
                {
                    p = Sym(surf.Project(p, off, out _), n);
                    cT[n] = c;
                }
            }
            else if (_t.IsSurfaceNode[n])
            {
                double cb = _die.Bottom.Clearance(p, 0), ctp = _die.TopClosed.Clearance(p, gapT);
                if (cb < 0 || ctp < 0)
                {
                    var hit = cb <= ctp ? NodeContact.BottomDie : NodeContact.TopDie;
                    var q = Sym(Surface(hit).Project(p, Offset(hit, gapT), out _), n);
                    maxPen = Math.Max(maxPen, (q - p).Length);
                    p = q;
                    cT[n] = hit;
                }
            }

            xT[n] = p;
        }

        if (maxPen > 0.5 * _h)
        {
            why = F($"die penetration {maxPen:F3} mm exceeds h/2");
            return false;
        }

        var t = _t.Tets;
        // Snapping nodes onto the die can flatten a tet whatever the step size, typically where several nodes are
        // pulled onto the same sharp die edge or corner (imported CAD dies have no fillets). Leave the nodes of such
        // tets at their predicted positions when the penetration is small; contact picks them up again next step.
        int reverted = ImprovePlacement(_t, xT, cT, pred, nrm, gapT, 0.05);
        if (reverted > 0) ProjectionReverts += reverted;
        SmoothInterior(_t, xT, 0.05, 6);
        for (int e = 0; e < _ne; e++)
        {
            if (TetMesh.Quality(xT[t[4 * e]], xT[t[4 * e + 1]], xT[t[4 * e + 2]], xT[t[4 * e + 3]]) <= 0.005)
            {
                why = F($"tetrahedron {e} collapsed");
                _failAt = 0.25 * (xT[t[4 * e]] + xT[t[4 * e + 1]] + xT[t[4 * e + 2]] + xT[t[4 * e + 3]]);
                if (Environment.GetEnvironmentVariable("IFEA_DEBUG") == "1")
                {
                    _log(F($"    q now {TetMesh.Quality(_x[t[4 * e]], _x[t[4 * e + 1]], _x[t[4 * e + 2]], _x[t[4 * e + 3]]):F4}, q pred {TetMesh.Quality(pred[t[4 * e]], pred[t[4 * e + 1]], pred[t[4 * e + 2]], pred[t[4 * e + 3]]):F4}"));
                    for (int a = 0; a < 4; a++)
                    {
                        int n = t[4 * e + a];
                        _log(F($"    node {n}: c {_contact[n]}→{cT[n]} rel {_release[n]} surf {_t.IsSurfaceNode[n]} x {_x[n].X:F3},{_x[n].Y:F3},{_x[n].Z:F3} pred {pred[n].X:F3},{pred[n].Y:F3},{pred[n].Z:F3} new {xT[n].X:F3},{xT[n].Y:F3},{xT[n].Z:F3} clrB {_die.Bottom.Clearance(pred[n], 0):F3} clrT {_die.TopClosed.Clearance(pred[n], gapT):F3} topZ {_die.TopClosed.SurfaceZ(pred[n].X, pred[n].Y) + gapT:F3} botZ {_die.Bottom.SurfaceZ(pred[n].X, pred[n].Y):F3}"));
                    }
                }

                return false;
            }
        }

        _x = xT;
        _contact = cT;
        _gap = gapT;
        _time += dt;
        why = "";
        return true;
    }

    /// <summary>Area-weighted outward unit normals of the workpiece surface at its surface nodes (zero elsewhere).</summary>
    private static Vec3[] SurfaceNormals(TetMesh mesh, Vec3[] x)
    {
        var n = new Vec3[mesh.NodeCount];
        var f = mesh.BoundaryFaces;
        for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
        {
            if (mesh.FaceIsSymmetry[k]) continue;
            int a = f[3 * k], b = f[3 * k + 1], c = f[3 * k + 2];
            var an = (x[b] - x[a]).Cross(x[c] - x[a]);
            n[a] += an;
            n[b] += an;
            n[c] += an;
        }

        for (int i = 0; i < n.Length; i++)
        {
            if (mesh.OnX0[i]) n[i] = n[i] with { X = 0 };
            if (mesh.OnY0[i]) n[i] = n[i] with { Y = 0 };
            if (n[i].LengthSquared > 0) n[i] = n[i].Normalized();
        }

        return n;
    }

    /// <summary>
    /// Moves a node that penetrates a die back to the die surface along the workpiece inward normal (−<paramref name="outward"/>)
    /// rather than to the closest die point. At a sharp convex die edge the closest point can lie on the far face and
    /// fold the elements; exiting along the inward normal lands next to the edge, so the surface wraps the corner.
    /// Falls back to the closest point when no exit is found within 2·h.
    /// </summary>
    private Vec3 PushOut(IRigidDie die, double offset, Vec3 p, Vec3 outward)
    {
        if (outward.LengthSquared == 0 || die.Clearance(p, offset) >= 0) return die.Project(p, offset, out _);
        var d = -outward;
        double step = 0.1 * _h, lo = 0, hi = -1;
        for (double t = step; t <= 2 * _h + 1e-12; t += step)
        {
            if (die.Clearance(p + t * d, offset) >= 0) { hi = t; break; }
            lo = t;
        }

        if (hi < 0) return die.Project(p, offset, out _);
        for (int i = 0; i < 25; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (die.Clearance(p + mid * d, offset) >= 0) hi = mid; else lo = mid;
        }

        return p + hi * d;
    }

    /// <summary>
    /// Penetration (× h) tolerated at sharp die corners to keep elements valid: strict normally, relaxed step by step
    /// only while the recovery ladder handles a failing increment (accuracy first, robustness when needed).
    /// </summary>
    private static readonly double[] CornerPenetrationLadder = [0.1, 0.2, 0.3, 0.3];

    private double MaxCornerPenetration => CornerPenetrationLadder[Math.Clamp(_recoveryPass, 0, CornerPenetrationLadder.Length - 1)];

    private int _recoveryPass;

    /// <summary>Node placements left slightly inside a die corner to keep elements valid (diagnostic).</summary>
    public int CornerPenetrations { get; private set; }

    /// <summary>Total nodes left unprojected to protect element shape (diagnostic).</summary>
    public int ProjectionReverts { get; private set; }

    /// <summary>
    /// Quality-aware contact placement. For every die-contact node of a tet below <paramref name="qMin"/>, tries the
    /// closest die point, the exit point along the workpiece inward normal (wraps sharp convex die edges) and — when
    /// it barely penetrates — the unprojected position <paramref name="pred"/>, keeping whichever gives its tets the
    /// best worst-case quality. Returns the number of nodes moved.
    /// </summary>
    private int ImprovePlacement(TetMesh mesh, Vec3[] x, NodeContact[] contact, Vec3[] pred, Vec3[] nrm, double gap, double qMin)
    {
        var t = mesh.Tets;
        int total = 0;
        Span<Vec3> cand = stackalloc Vec3[8];
        for (int sweep = 0; sweep < 4; sweep++)
        {
            int count = 0;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]) >= qMin) continue;
                for (int a = 0; a < 4; a++)
                {
                    int n = t[4 * e + a];
                    if (contact[n] == NodeContact.Free) continue;
                    var die = Surface(contact[n]);
                    double off = Offset(contact[n], gap);
                    int k = 0;
                    var closest = SymOn(mesh, die.Project(pred[n], off, out _), n);
                    cand[k++] = closest;
                    var push = SymOn(mesh, PushOut(die, off, pred[n], nrm[n]), n);
                    if ((push - pred[n]).Length <= 0.5 * _h) cand[k++] = push;
                    double pen = -die.Clearance(pred[n], off);
                    // Partial pushes: a node may stay slightly inside a sharp die corner (≤ MaxCornerPenetration·h)
                    // when every full projection would invert an element; it is pushed out over the next increments.
                    foreach (double f in (ReadOnlySpan<double>)[0.0, 0.25, 0.5, 0.75])
                    {
                        if (pen * (1 - f) <= MaxCornerPenetration * _h) cand[k++] = Vec3.Lerp(pred[n], closest, f);
                    }

                    var old = x[n];
                    double best = WorstAround(mesh, x, n);
                    int pick = -1;
                    for (int c = 0; c < k; c++)
                    {
                        if (cand[c] == old) continue;
                        x[n] = cand[c];
                        double q = WorstAround(mesh, x, n);
                        if (q > best + 1e-6) { best = q; pick = c; }
                    }

                    x[n] = pick >= 0 ? cand[pick] : old;
                    if (pick < 0) continue;
                    if (cand[pick] == pred[n] && pen < 0) contact[n] = NodeContact.Free;
                    if (die.Clearance(cand[pick], off) < -1e-9) CornerPenetrations++;
                    count++;
                }
            }

            total += count;
            if (count == 0) break;
        }

        return total;
    }

    private static Vec3 SymOn(TetMesh mesh, Vec3 p, int n) => new(mesh.OnX0[n] ? 0 : p.X, mesh.OnY0[n] ? 0 : p.Y, p.Z);

    private static double WorstAround(TetMesh mesh, Vec3[] x, int n)
    {
        var t = mesh.Tets;
        double q = double.MaxValue;
        foreach (int e in mesh.TetsOf(n)) q = Math.Min(q, TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]));
        return q;
    }

    /// <summary>
    /// Quality-driven smoothing of interior nodes: each interior node of a tet below <paramref name="qTarget"/> moves
    /// towards the centroid of its neighbours (full, half or quarter step) when that raises the worst quality of its
    /// tets. Surface nodes are untouched (they carry contact and the free surface); symmetry-plane nodes stay on their
    /// planes. Interior nodes carry no boundary condition, so this is a pure mesh (ALE-type) correction; element
    /// state stays with the elements. Returns the number of node moves.
    /// </summary>
    private static int SmoothInterior(TetMesh mesh, Vec3[] x, double qTarget, int sweeps)
    {
        var t = mesh.Tets;
        int moves = 0;
        var bad = new bool[mesh.NodeCount];
        for (int s = 0; s < sweeps; s++)
        {
            Array.Clear(bad);
            bool any = false;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]) >= qTarget) continue;
                for (int a = 0; a < 4; a++) bad[t[4 * e + a]] = true;
                any = true;
            }

            if (!any) break;
            int sweepMoves = 0;
            for (int n = 0; n < mesh.NodeCount; n++)
            {
                if (!bad[n] || mesh.IsSurfaceNode[n]) continue;
                var tets = mesh.TetsOf(n);
                Vec3 sum = default;
                int cnt = 0;
                double worst = double.MaxValue;
                foreach (int e in tets)
                {
                    for (int a = 0; a < 4; a++)
                    {
                        int m = t[4 * e + a];
                        if (m == n) continue;
                        sum += x[m];
                        cnt++;
                    }

                    worst = Math.Min(worst, TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]));
                }

                if (cnt == 0) continue;
                var old = x[n];
                var c = (1.0 / cnt) * sum;
                foreach (double lambda in (ReadOnlySpan<double>)[1.0, 0.5, 0.25])
                {
                    var p = old + lambda * (c - old);
                    x[n] = new Vec3(mesh.OnX0[n] ? 0 : p.X, mesh.OnY0[n] ? 0 : p.Y, p.Z);
                    double q = double.MaxValue;
                    foreach (int e in tets) q = Math.Min(q, TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]));
                    if (q > worst + 1e-6)
                    {
                        sweepMoves++;
                        break;
                    }

                    x[n] = old;
                }
            }

            moves += sweepMoves;
            if (sweepMoves == 0) break;
        }

        return moves;
    }

    private double MinQuality(Vec3[] x, TetMesh mesh)
    {
        double q = double.MaxValue;
        var t = mesh.Tets;
        for (int e = 0; e < mesh.ElementCount; e++) q = Math.Min(q, TetMesh.Quality(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]));
        return q;
    }

    // ------------------------------------------------------------------ remeshing

    private string? RemeshTrigger()
    {
        // Cheap first aid: interior-node smoothing often lifts the worst tets above the threshold without a remesh.
        if (MinQuality(_x, _t) < _cfg.RemeshQuality) SmoothInterior(_t, _x, _cfg.RemeshQuality, 6);
        double q = MinQuality(_x, _t);
        if (q < _cfg.RemeshQuality) return F($"element quality {q:F3}");
        var f = _t.BoundaryFaces;
        double maxEdge = 0;
        for (int k = 0; k < f.Length; k += 3)
        {
            maxEdge = Math.Max(maxEdge, Math.Max((_x[f[k + 1]] - _x[f[k]]).Length, Math.Max((_x[f[k + 2]] - _x[f[k + 1]]).Length, (_x[f[k]] - _x[f[k + 2]]).Length)));
        }

        return maxEdge > 2.8 * _h ? F($"surface stretched ({maxEdge:F2} mm edges)") : null;
    }

    private bool Remesh(string reason)
    {
        var sw = Stopwatch.StartNew();
        var oldT = _t;
        var oldX = _x;
        double qOld = MinQuality(_x, _t);
        var locator = new TetLocator(oldT, oldX, 2 * _h);
        Vec3 min = oldX[0], max = oldX[0];
        foreach (var p in oldX)
        {
            min = new Vec3(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new Vec3(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        IsosurfaceStuffing.Result res;
        TetMesh mesh;
        try
        {
            res = IsosurfaceStuffing.Stuff(locator.Contains, min, max, _h, _symX, _symY);
        }
        catch (InvalidOperationException ex)
        {
            _log(F($"  remesh ({reason}) rejected: {ex.Message}"));
            return false;
        }

        var x = res.Points;
        var tetsNew = res.Tets;
        int splits = 0;
        if (_cfg.RefinementLevels > 0)
        {
            (x, tetsNew, splits) = TetRefiner.Refine(x, tetsNew, TargetSize, _cfg.MaxNodeCount);
        }

        try
        {
            mesh = BuildMesh(x, tetsNew);
        }
        catch (InvalidOperationException ex)
        {
            _log(F($"  remesh ({reason}) rejected: {ex.Message}"));
            return false;
        }

        var stuffed = (Vec3[])x.Clone();
        var contact = new NodeContact[mesh.NodeCount];
        var newNrm = SurfaceNormals(mesh, x);
        double tolC = 0.12 * _h;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            if (!mesh.IsSurfaceNode[n]) continue;
            double cb = _die.Bottom.Clearance(x[n], 0), ctp = _die.TopClosed.Clearance(x[n], _gap);
            if (cb < tolC && cb <= ctp) contact[n] = NodeContact.BottomDie;
            else if (ctp < tolC) contact[n] = NodeContact.TopDie;
            if (contact[n] != NodeContact.Free)
            {
                var surf = Surface(contact[n]);
                double off = Offset(contact[n], _gap);
                var q = surf.Project(x[n], off, out _);
                x[n] = new Vec3(mesh.OnX0[n] ? 0 : q.X, mesh.OnY0[n] ? 0 : q.Y, q.Z);
            }
        }

        CompensateVolume(mesh, x, contact);
        ImprovePlacement(mesh, x, contact, stuffed, newNrm, _gap, 0.1);

        // In very thin sections (flash one tet thick) projecting onto both dies can flatten or invert a tet: put the
        // nodes of such tets back at their stuffed positions (on the old surface) and let contact pick them up again.
        var tt = mesh.Tets;
        for (int sweep = 0; sweep < 4; sweep++)
        {
            int reverted = 0;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                if (TetMesh.Quality(x[tt[4 * e]], x[tt[4 * e + 1]], x[tt[4 * e + 2]], x[tt[4 * e + 3]]) >= 0.03) continue;
                for (int a = 0; a < 4; a++)
                {
                    int n = tt[4 * e + a];
                    if (x[n] == stuffed[n]) continue;
                    // Never put a node back inside a die (corner-cutting chords of the old surface).
                    if (Math.Min(_die.Bottom.Clearance(stuffed[n], 0), _die.TopClosed.Clearance(stuffed[n], _gap)) < -0.05 * _h) continue;
                    x[n] = stuffed[n];
                    contact[n] = NodeContact.Free;
                    reverted++;
                }
            }

            if (reverted == 0) break;
        }

        SmoothInterior(mesh, x, 0.2, 10);
        double qNew = MinQuality(x, mesh);
        if (qNew < 0.02)
        {
            _log(F($"  remesh ({reason}) rejected: new mesh quality {qNew:F3}."));
            return false;
        }

        var t = mesh.Tets;
        var newEps = new double[mesh.ElementCount];
        var newSig = new double[mesh.ElementCount];
        var newRate = new double[mesh.ElementCount];
        var newMean = new double[mesh.ElementCount];
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var c = 0.25 * (x[t[4 * e]] + x[t[4 * e + 1]] + x[t[4 * e + 2]] + x[t[4 * e + 3]]);
            int found = locator.FindNearest(c);
            newEps[e] = found >= 0 ? _eps[found] : 0;
            newSig[e] = found >= 0 ? _elSig[found] : _sigRef;
            newRate[e] = found >= 0 ? _elRate[found] : 0;
            newMean[e] = found >= 0 ? _elMean[found] : 0;
        }

        var newVg = new Vec3[mesh.NodeCount];
        for (int n = 0; n < mesh.NodeCount; n++) newVg[n] = locator.Interpolate(x[n], _vg);

        Allocate(mesh, x);
        _eps = newEps;
        _elSig = newSig;
        _elRate = newRate;
        _elMean = newMean;
        _vg = newVg;
        _contact = contact;
        _warm = false;
        RemeshCount++;
        _log(F($"  REMESH #{RemeshCount} ({reason}): q_min {qOld:F3} → {qNew:F3}, {_ne} tets, {_nn} nodes{(splits > 0 ? F($" ({splits} local refinements)") : "")}, {_colPtr[^1]:N0} nnz, {sw.Elapsed.TotalMilliseconds:F0} ms"));
        return true;
    }

    /// <summary>Moves free-surface nodes along their normals to restore the initial volume (≤ 0.25·h per remesh).</summary>
    private void CompensateVolume(TetMesh mesh, Vec3[] x, NodeContact[] contact)
    {
        double vol = 0;
        var t = mesh.Tets;
        for (int e = 0; e < mesh.ElementCount; e++) vol += TetMesh.Volume(x[t[4 * e]], x[t[4 * e + 1]], x[t[4 * e + 2]], x[t[4 * e + 3]]);
        double deficit = _vol0 / _symFactor - vol;
        var normals = new Vec3[mesh.NodeCount];
        var f = mesh.BoundaryFaces;
        for (int k = 0; k < mesh.FaceIsSymmetry.Length; k++)
        {
            if (mesh.FaceIsSymmetry[k]) continue;
            int a = f[3 * k], b = f[3 * k + 1], c = f[3 * k + 2];
            var an = 0.5 * (x[b] - x[a]).Cross(x[c] - x[a]);
            normals[a] += an;
            normals[b] += an;
            normals[c] += an;
        }

        double rate = 0;
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            if (!mesh.IsSurfaceNode[n] || contact[n] != NodeContact.Free) { normals[n] = default; continue; }
            var nv = normals[n];
            if (mesh.OnX0[n]) nv = nv with { X = 0 };
            if (mesh.OnY0[n]) nv = nv with { Y = 0 };
            rate += nv.Length / 3;
            normals[n] = nv.Normalized();
        }

        if (rate <= 0) return;
        double shift = Math.Clamp(deficit / rate, -0.25 * _h, 0.25 * _h);
        for (int n = 0; n < mesh.NodeCount; n++)
        {
            if (normals[n] == default) continue;
            var moved = x[n] + shift * normals[n];
            if (_die.Bottom.Clearance(moved, 0) > 0 && _die.TopClosed.Clearance(moved, _gap) > 0) x[n] = moved;
        }
    }

    // ------------------------------------------------------------------ telemetry

    private double Volume()
    {
        double v = 0;
        var t = _t.Tets;
        for (int e = 0; e < _ne; e++) v += TetMesh.Volume(_x[t[4 * e]], _x[t[4 * e + 1]], _x[t[4 * e + 2]], _x[t[4 * e + 3]]);
        return v * _symFactor;
    }

    private Vec3[] BuildCavitySamples()
    {
        double x0 = _symX ? 0 : _die.XMin, x1 = _die.XMax, y0 = _symY ? 0 : -_die.YMax, y1 = _die.YMax;
        double area = (x1 - x0) * (y1 - y0);
        double d = Math.Sqrt(area / 2500);
        var list = new List<Vec3>();
        for (double x = x0 + d / 2; x < x1; x += d)
        {
            for (double y = y0 + d / 2; y < y1; y += d)
            {
                if (_die.Outline(x, y) >= 0) continue;
                double zb = _die.Bottom.SurfaceZ(x, y), zt = _die.TopClosed.SurfaceZ(x, y);
                if (!(zt > zb)) continue;
                int nz = Math.Max(1, (int)Math.Round((zt - zb) / d));
                for (int k = 0; k < nz; k++) list.Add(new Vec3(x, y, zb + (k + 0.5) * (zt - zb) / nz));
            }
        }

        return list.ToArray();
    }

    private double FillPercent()
    {
        if (_cavity.Length == 0) return 0;
        var loc = new TetLocator(_t, _x, 2 * _h);
        int inside = 0;
        foreach (var p in _cavity)
        {
            if (loc.Contains(p)) inside++;
        }

        return 100.0 * inside / _cavity.Length;
    }

    private Snapshot3D MakeSnapshot(int step, double load, int iterations)
    {
        var xs = new float[_nn];
        var ys = new float[_nn];
        var zs = new float[_nn];
        for (int n = 0; n < _nn; n++)
        {
            xs[n] = (float)_x[n].X;
            ys[n] = (float)_x[n].Y;
            zs[n] = (float)_x[n].Z;
        }

        bool initial = step == 0;
        var vm = new float[_ne];
        var ep = new float[_ne];
        var er = new float[_ne];
        var sm = new float[_ne];
        for (int e = 0; e < _ne; e++)
        {
            vm[e] = initial ? 0f : (float)_elSig[e];
            ep[e] = (float)_eps[e];
            er[e] = initial ? 0f : (float)_elRate[e];
            sm[e] = initial ? 0f : (float)_elMean[e];
        }

        return new Snapshot3D
        {
            Step = step,
            Time = _time,
            Stroke = _gap0 - _gap,
            DieGap = _gap,
            LoadNewton = load,
            FillPercent = FillPercent(),
            VolumeChangePercent = 100.0 * (Volume() - _vol0) / _vol0,
            Iterations = iterations,
            Mesh = _t,
            X = xs,
            Y = ys,
            Z = zs,
            VonMises = vm,
            EffectiveStrain = ep,
            EffectiveStrainRate = er,
            MeanStress = sm,
            Contact = (NodeContact[])_contact.Clone(),
            VelocityX = initial ? [] : _vg.Select(v => (float)v.X).ToArray(),
            VelocityY = initial ? [] : _vg.Select(v => (float)v.Y).ToArray(),
            VelocityZ = initial ? [] : _vg.Select(v => (float)v.Z).ToArray(),
        };
    }
}
