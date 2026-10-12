using System.Diagnostics;
using System.Globalization;
using ImpressionFEA.Core.Geometry;
using ImpressionFEA.Core.Materials;
using ImpressionFEA.Core.Mesh;
using ImpressionFEA.Core.Simulation;

namespace ImpressionFEA.Core.Solver;

/// <summary>
/// Rigid-viscoplastic flow-formulation FEM (Kobayashi–Oh–Altan) for axisymmetric / plane-strain closed-die forging.
/// <list type="bullet">
/// <item>4-node isoparametric quads, 2×2 Gauss for the deviatoric power, 1-point reduced integration for the
/// volumetric penalty (no locking, no hourglassing).</item>
/// <item>Functional Π = ∫W(ε̄̇)dV + ½K∫ε̇ᵥ²dV + Σ friction potential, minimised by Picard → Newton–Raphson with an
/// Armijo line search on Π. The stiffness is factorised by band Cholesky (reverse Cuthill–McKee numbering).</item>
/// <item>Node-to-surface contact on rigid dies with local (t, n) nodal frames, constant shear friction τ = m·k
/// smoothed by the arctan law, tensile-reaction release and fold-over capture.</item>
/// <item>Updated-Lagrangian increments with automatic remeshing (Delaunay triangulation of the current outline,
/// split into quads) and remapping of effective strain, flow stress and velocity.</item>
/// </list>
/// Units: mm, s, MPa (N/mm²), N.
/// </summary>
public sealed class ForgingSolver : IForgingStepper
{
    private const double Gp = 0.57735026918962576;
    private static readonly double[] GXi = [-Gp, Gp, Gp, -Gp];
    private static readonly double[] GEta = [-Gp, -Gp, Gp, Gp];
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Minimum corner scaled Jacobian below which the billet is remeshed.</summary>
    private const double RemeshQuality = 0.15;

    private readonly SimulationConfig _cfg;
    private readonly DieSet _die;
    private readonly FlowStressLaw _law;
    private readonly Action<string> _log;
    private readonly bool _axi;

    // mesh-sized state (reallocated on remesh)
    private QuadMesh _t = null!;
    private int _nn;
    private int _ne;
    private int _ndof;
    private int[] _eq = null!;
    private Vec2[] _x = null!;
    private Vec2[] _vg = null!;
    private double[] _v = null!;
    private double[] _eps = null!;
    private NodeContact[] _contact = null!;
    private Vec2[] _tan = null!;
    private Vec2[] _nrm = null!;
    private bool[] _rotated = null!;
    private bool[] _fixed = null!;
    private double[] _fixedVal = null!;
    private double[] _kFric = null!;
    private double[] _aFric = null!;
    private bool[] _release = null!;
    private double[] _elSig = null!;
    private double[] _elRate = null!;
    private double[] _elMean = null!;
    private double[] _dmg = null!;
    private double[] _elDev = null!; // element-average deviatoric stress: r/x, z/y, hoop / out-of-plane, shear (MPa)
    private SymmetricBandMatrix _k = null!;
    private double[] _r = null!;
    private double[] _dv = null!;
    private double[] _vTrial = null!;

    // element scratch
    private readonly double[] _ke = new double[64];
    private readonly double[] _re = new double[8];
    private readonly double[] _ve = new double[8];
    private readonly double[] _ex = new double[4];
    private readonly double[] _ey = new double[4];
    private readonly double[] _sn = new double[4];
    private readonly double[] _dnx = new double[4];
    private readonly double[] _dny = new double[4];
    private readonly double[] _dxi = new double[4];
    private readonly double[] _deta = new double[4];
    private readonly double[][] _b = [new double[8], new double[8], new double[8], new double[8]];
    private readonly double[][] _mb = [new double[8], new double[8], new double[8], new double[8]];
    private readonly double[] _g8 = new double[8];
    private readonly double[] _c8 = new double[8];
    private readonly int[] _edof = new int[8];

    private readonly double _ram;
    private readonly double _rateRef;
    private readonly double _rateCut;
    private readonly double _kPen;
    private readonly double _u0;
    private readonly double _h;
    private readonly double _gap0;
    private readonly double _vol0;
    private readonly double _sigRef;
    private double _gap;
    private double _time;
    private double _lastRel;

    private readonly double[] _cavX;
    private readonly double[] _cavY;
    private readonly double[] _cavW;
    private readonly double _cavTotal;

    public ForgingSolver(SimulationConfig config, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = config.Validate();
        if (errors.Count > 0) throw new InputValidationException(errors);

        _cfg = config;
        _die = config.Die;
        _axi = _die.Symmetry == Symmetry.Axisymmetric;
        _law = config.Material.At(config.Temperature);
        _log = log ?? (_ => { });

        double r0 = config.BilletRadius, h0 = config.BilletHeight;
        double seat = _die.SeatHeight(r0);
        var (mesh, x) = QuadMesh.Structured(config.ElementsX, config.ElementsZ, r0, seat, h0);
        _h = Math.Min(r0 / config.ElementsX, h0 / config.ElementsZ);
        _gap0 = _gap = _die.InitialGap(r0, h0);
        _ram = config.RamSpeed;
        _rateRef = _ram / h0;
        _rateCut = 1e-3 * _rateRef;
        _sigRef = _law.Stress(0, _rateRef);
        _kPen = 1e5 * _sigRef / _rateRef;
        _u0 = 5e-3 * _ram;

        Allocate(mesh, x);
        Array.Fill(_elSig, _sigRef);
        double tol = 1e-3 * _h;
        foreach (int n in _t.BoundaryLoop)
        {
            if (_die.Bottom.Clearance(_x[n], 0) < tol) _contact[n] = NodeContact.BottomDie;
            else if (_die.TopClosed.Clearance(_x[n], _gap) < tol) _contact[n] = NodeContact.TopDie;
        }

        double sx = _axi ? 0.5 : 1.0;
        for (int n = 0; n < _nn; n++)
            _vg[n] = new Vec2(sx * _rateRef * _x[n].X, -_rateRef * (_x[n].Y - seat));

        InitialMesh = mesh;
        _vol0 = Volume(_x);
        (_cavX, _cavY, _cavW, _cavTotal) = BuildCavitySamples();
        LogBanner();
    }

    public QuadMesh InitialMesh { get; }

    public QuadMesh Mesh => _t;

    public double TotalStroke => _gap0;

    public int StepIndex { get; private set; }

    public int RemeshCount { get; private set; }

    public bool IsFinished => _gap <= 1e-9;

    public int NominalSteps => (int)Math.Ceiling(_gap0 / _cfg.StrokeIncrement);

    private static string F(FormattableString s) => s.ToString(Inv);

    private void Allocate(QuadMesh mesh, Vec2[] x)
    {
        _t = mesh;
        _nn = mesh.NodeCount;
        _ne = mesh.ElementCount;
        _ndof = mesh.DofCount;
        _eq = mesh.NodeEquation;
        _x = x;
        _vg = new Vec2[_nn];
        _v = new double[_ndof];
        _eps = new double[_ne];
        _dmg = new double[_ne];
        _elDev = new double[4 * _ne];
        _contact = new NodeContact[_nn];
        _tan = new Vec2[_nn];
        _nrm = new Vec2[_nn];
        _rotated = new bool[_nn];
        _fixed = new bool[_ndof];
        _fixedVal = new double[_ndof];
        _kFric = new double[_nn];
        _aFric = new double[_nn];
        _release = new bool[_nn];
        _elSig = new double[_ne];
        _elRate = new double[_ne];
        _elMean = new double[_ne];
        _k = new SymmetricBandMatrix(_ndof, mesh.HalfBandwidth);
        _r = new double[_ndof];
        _dv = new double[_ndof];
        _vTrial = new double[_ndof];
    }

    private void LogBanner()
    {
        _log(F($"ImpressionFEA native FEM · rigid-viscoplastic flow formulation · {(_axi ? "axisymmetric" : "plane strain")}"));
        _log(F($"Die set      : {_die.Name}  (impression R {_die.CavityOuterRadius:F2} mm)"));
        _log(F($"Material     : {_cfg.Material.Name} @ {_cfg.Temperature:F0} °C  →  {_law}"));
        if (!_cfg.Material.InRange(_cfg.Temperature))
            _log(F($"WARNING      : {_cfg.Temperature:F0} °C is outside the calibrated window {_cfg.Material.TMin:F0}–{_cfg.Material.TMax:F0} °C; flow stress is extrapolated."));
        _log(F($"Billet       : {(_axi ? "R" : "W/2")} {_cfg.BilletRadius:F2} × H {_cfg.BilletHeight:F2} mm{(_axi ? "" : F($" × L {_cfg.BilletLength:F1} mm"))}, volume {_vol0 / 1000:F2} cm³ (cavity {_die.CavityVolume(_cfg.BilletLength) / 1000:F2} cm³)"));
        _log(F($"Mesh         : {_cfg.ElementsX} × {_cfg.ElementsZ} Q4 = {_ne} elements, {_nn} nodes, {_ndof} DOF, half-bandwidth {_k.Bandwidth}"));
        _log(F($"Press        : ram {_ram:F1} mm/s, stroke {_gap0:F3} mm, Δs {_cfg.StrokeIncrement:F3} mm (Δt {1000 * _cfg.TimeIncrement:F2} ms), ~{NominalSteps} increments"));
        _log(F($"Numerics     : penalty K {_kPen:E2} MPa·s, ε̇₀ cut-off {_rateCut:E2} 1/s, friction m {_cfg.FrictionFactor:F2} (u₀ {_u0:E2} mm/s), tol {_cfg.Tolerance:E1}"));
    }

    // ------------------------------------------------------------------ public stepping API

    public StepSnapshot InitialSnapshot() => MakeSnapshot(0, 0.0, 0);

    ForgingSnapshot IForgingStepper.Initial() => InitialSnapshot();

    ForgingSnapshot IForgingStepper.Step(CancellationToken ct) => Advance(ct);

    public StepSnapshot Advance(CancellationToken ct)
    {
        if (IsFinished) throw new InvalidOperationException("Simulation already reached the closed-die position.");
        StepIndex++;
        var sw = Stopwatch.StartNew();

        string? trigger = RemeshTrigger();
        if (trigger is not null) Remesh(trigger, requireImprovement: trigger.StartsWith("element quality", StringComparison.Ordinal));

        int iterations = 0;
        double load = 0, reactionLoad = 0, dt = 0;
        bool ok = false;
        string why = "";
        for (int pass = 0; pass < 2 && !ok; pass++)
        {
            PrepareConstraints();
            LoadVelocityGuess();
            iterations = SolveVelocity(ct, out bool converged, picardFallback: false);
            if (!converged && _lastRel >= 1e-3)
            {
                _log(F($"  step {StepIndex}: Newton stalled (|Δv|/|v| = {_lastRel:E2}); retrying with Picard fallback."));
                LoadVelocityGuess();
                iterations += SolveVelocity(ct, out converged, picardFallback: true);
            }

            if (!converged)
            {
                if (_lastRel < 1e-3)
                    _log(F($"  WARNING step {StepIndex}: not fully converged (|Δv|/|v| = {_lastRel:E2}); continuing."));
                else
                    throw new SolverDivergenceException(F($"Newton–Raphson diverged at step {StepIndex} (stroke {_gap0 - _gap:F3} mm): |Δv|/|v| = {_lastRel:E2} after {iterations} iterations."));
            }

            double power = FinalEvaluation(out reactionLoad);
            load = power / _ram;

            dt = Math.Min(_cfg.StrokeIncrement, _gap) / _ram;
            if (_gap - _ram * dt < 0.05 * _cfg.StrokeIncrement) dt = _gap / _ram;
            for (int attempt = 0; attempt < 4 && !ok; attempt++)
            {
                double dta = dt / (1 << attempt);
                ok = TryUpdate(dta, out why);
                if (ok) dt = dta;
                else _log(F($"  cutback {attempt + 1}: {why}"));
            }

            if (!ok && (pass > 0 || !Remesh("fold during update")))
                break;
        }

        bool remeshedOnUpdate = false;
        if (!ok)
        {
            remeshedOnUpdate = RemeshOnFoldedUpdate(dt, out double dtf);
            if (!remeshedOnUpdate)
                throw new MeshDistortionException(F($"Mesh distortion at step {StepIndex} (stroke {_gap0 - _gap:F3} mm): {why}. Results up to the previous increment are kept; refine h or reduce Δs."));
            dt = dtf;
        }

        if (!remeshedOnUpdate)
        {
            for (int e = 0; e < _ne; e++) _eps[e] += _elRate[e] * dt;
            AccumulateDamage(dt);
        }
        var beforeCapture = ((Vec2[])_x.Clone(), (NodeContact[])_contact.Clone());
        int captured = CaptureFoldOver();
        if (captured > 0 && (MinQuality(_x) <= 0.002 || !Remesh(F($"fold-over, {captured} node(s) laid on die"))))
        {
            // Remesh rejected: undo the capture (it would leave a flattened element) unless the new mesh is in place.
            if (_x.Length == beforeCapture.Item1.Length && MinQuality(_x) <= 0.05) (_x, _contact) = beforeCapture;
        }

        var snap = MakeSnapshot(StepIndex, load, iterations);
        _log(F($"step {StepIndex,4} | stroke {snap.Stroke,8:F3} mm | it {iterations,2} | load {load / 9806.65,9:F2} t (reaction {reactionLoad / 9806.65:F2} t) | fill {snap.FillPercent,6:F2} % | ΔV {snap.VolumeChangePercent,6:F3} % | {_ne} el | {sw.Elapsed.TotalMilliseconds,6:F0} ms"));
        return snap;
    }

    // ------------------------------------------------------------------ constraints, frames, friction areas

    private int Dof(int n, int c) => 2 * _eq[n] + c;

    private static Vec2 DieVelocity(NodeContact c, double ram) => c == NodeContact.TopDie ? new Vec2(0, -ram) : default;

    private DieSurface Surface(NodeContact c) => c == NodeContact.TopDie ? _die.TopClosed : _die.Bottom;

    private double Offset(NodeContact c, double gap) => c == NodeContact.TopDie ? gap : 0.0;

    private double Weight(double x) => _axi ? 2 * Math.PI * x : 2 * _cfg.BilletLength;

    private void PrepareConstraints()
    {
        Array.Clear(_fixed);
        Array.Clear(_fixedVal);
        Array.Clear(_aFric);
        Array.Clear(_release);
        for (int n = 0; n < _nn; n++)
        {
            _rotated[n] = false;
            _tan[n] = new Vec2(1, 0);
            _nrm[n] = new Vec2(0, 1);
            bool axis = _t.IsAxisNode(n);
            var c = _contact[n];
            if (c != NodeContact.Free)
            {
                var dv = DieVelocity(c, _ram);
                if (axis)
                {
                    Fix(Dof(n, 0), 0);
                    Fix(Dof(n, 1), dv.Y);
                }
                else
                {
                    Surface(c).Project(_x[n], Offset(c, _gap), out var nrm, out _);
                    _nrm[n] = nrm;
                    _tan[n] = new Vec2(nrm.Y, -nrm.X);
                    _rotated[n] = true;
                    Fix(Dof(n, 1), dv.Dot(nrm));
                }
            }
            else if (axis)
            {
                Fix(Dof(n, 0), 0);
            }
        }

        // Tributary friction areas and lagged shear yield stress k = σ̄/√3 of the adjacent boundary elements.
        var loop = _t.BoundaryLoop;
        var kSum = new double[_nn];
        var kCnt = new int[_nn];
        for (int k = 0; k < loop.Length; k++)
        {
            int a = loop[k], b = loop[(k + 1) % loop.Length];
            double len = (_x[b] - _x[a]).Length;
            double area = len * Weight(0.5 * (_x[a].X + _x[b].X));
            _aFric[a] += 0.5 * area;
            _aFric[b] += 0.5 * area;
            double kk = _elSig[_t.BoundaryEdgeOwner[k]] / Math.Sqrt(3);
            kSum[a] += kk; kCnt[a]++;
            kSum[b] += kk; kCnt[b]++;
        }

        for (int n = 0; n < _nn; n++)
            _kFric[n] = kCnt[n] > 0 ? kSum[n] / kCnt[n] : 0;
    }

    private void Fix(int dof, double value)
    {
        _fixed[dof] = true;
        _fixedVal[dof] = value;
    }

    private void LoadVelocityGuess()
    {
        for (int n = 0; n < _nn; n++)
        {
            var g = _vg[n];
            int d0 = Dof(n, 0), d1 = Dof(n, 1);
            if (_rotated[n])
            {
                _v[d0] = g.Dot(_tan[n]);
                _v[d1] = g.Dot(_nrm[n]);
            }
            else
            {
                _v[d0] = g.X;
                _v[d1] = g.Y;
            }
        }

        for (int d = 0; d < _ndof; d++)
        {
            if (_fixed[d]) _v[d] = _fixedVal[d];
        }
    }

    private Vec2 GlobalVelocity(double[] v, int n)
    {
        double a = v[Dof(n, 0)], b = v[Dof(n, 1)];
        return _rotated[n] ? a * _tan[n] + b * _nrm[n] : new Vec2(a, b);
    }

    // ------------------------------------------------------------------ nonlinear velocity solve

    /// <param name="picardFallback">
    /// Retry mode: when Newton keeps needing damped steps (stick–slip friction or near-rigid zones make the tangent a
    /// poor guide, e.g. a long bar touching the dies at a few points), drop back to a few robust Picard iterations and
    /// retry Newton from there.
    /// </param>
    private int SolveVelocity(CancellationToken ct, out bool converged, bool picardFallback)
    {
        converged = false;
        _lastRel = double.PositiveInfinity;
        int it;
        int damped = 0, picardUntil = 2;
        for (it = 1; it <= _cfg.MaxIterations; it++)
        {
            ct.ThrowIfCancellationRequested();
            bool newton = it > picardUntil; // Picard first, then Newton
            double e0 = Assemble(true, newton, _v, out _);
            for (int d = 0; d < _ndof; d++)
            {
                if (!_fixed[d]) continue;
                _k.ConstrainDof(d);
                _r[d] = 0;
            }

            double rnorm = Norm(_r);
            long t0 = Stopwatch.GetTimestamp();
            _k.Factorize();
            for (int d = 0; d < _ndof; d++) _dv[d] = -_r[d];
            _k.Solve(_dv);
            double cholMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            double slope = 0;
            for (int d = 0; d < _ndof; d++) slope += _r[d] * _dv[d];

            double beta = 1.0;
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
            _log(F($"    it {it,2} {(newton ? "NR" : "PI")}  |R| {rnorm,10:E3} N  |Δv|/|v| {rel,9:E2}  λ {beta,4:F2}  chol {cholMs,6:F1} ms"));
            if (double.IsNaN(rel))
                throw new SolverDivergenceException(F($"Velocity solution became NaN at step {StepIndex}."));
            if (rel < _cfg.Tolerance)
            {
                converged = true;
                return it;
            }

            damped = newton && beta < 0.5 ? damped + 1 : 0;
            if (picardFallback && damped >= 3)
            {
                picardUntil = it + 4;
                damped = 0;
            }
        }

        return it - 1;
    }

    private static double Norm(double[] a)
    {
        double s = 0;
        foreach (double x in a) s += x * x;
        return Math.Sqrt(s);
    }

    /// <summary>Assembles residual (and optionally the Picard secant or Newton tangent matrix) at velocity v; returns Π(v).</summary>
    private double Assemble(bool matrix, bool newton, double[] v, out double power)
    {
        if (matrix) _k.Clear();
        Array.Clear(_r);
        double energy = 0;
        power = 0;
        var conn = _t.Connectivity;

        for (int e = 0; e < _ne; e++)
        {
            for (int a = 0; a < 4; a++)
            {
                int n = conn[4 * e + a];
                _ex[a] = _x[n].X;
                _ey[a] = _x[n].Y;
                var g = GlobalVelocity(v, n);
                _ve[2 * a] = g.X;
                _ve[2 * a + 1] = g.Y;
                _edof[2 * a] = Dof(n, 0);
                _edof[2 * a + 1] = Dof(n, 1);
            }

            ElementKernel(e, matrix, newton, ref energy, ref power);

            for (int a = 0; a < 4; a++)
            {
                int n = conn[4 * e + a];
                if (_rotated[n]) RotateElementBlock(a, _tan[n], _nrm[n], matrix);
            }

            for (int p = 0; p < 8; p++)
            {
                _r[_edof[p]] += _re[p];
                if (!matrix) continue;
                for (int q = 0; q <= p; q++)
                {
                    double kv = _ke[8 * p + q];
                    if (kv != 0) _k.Add(_edof[p], _edof[q], kv);
                }
            }
        }

        // Constant shear friction, arctan-regularised (Chen & Kobayashi).
        double m = _cfg.FrictionFactor;
        if (m > 0)
        {
            for (int n = 0; n < _nn; n++)
            {
                if (!_rotated[n]) continue;
                double c = m * _kFric[n] * _aFric[n] * (2 / Math.PI);
                if (c <= 0) continue;
                int d = Dof(n, 0);
                double s = v[d] - DieVelocity(_contact[n], _ram).Dot(_tan[n]);
                double z = s / _u0;
                double f = c * Math.Atan(z);
                _r[d] += f;
                energy += c * (s * Math.Atan(z) - 0.5 * _u0 * Math.Log(1 + z * z));
                power += f * s;
                if (matrix)
                {
                    double kt = newton ? c / (_u0 * (1 + z * z)) : (Math.Abs(z) > 1e-9 ? f / s : c / _u0);
                    _k.Add(d, d, kt);
                }
            }
        }

        return energy;
    }

    /// <summary>Transforms rows/columns of node a from global (x, y) to its local (t, n) frame.</summary>
    private void RotateElementBlock(int a, Vec2 t, Vec2 n, bool matrix)
    {
        int r0 = 2 * a, r1 = r0 + 1;
        double f0 = _re[r0], f1 = _re[r1];
        _re[r0] = t.X * f0 + t.Y * f1;
        _re[r1] = n.X * f0 + n.Y * f1;
        if (!matrix) return;
        for (int c = 0; c < 8; c++)
        {
            double k0 = _ke[8 * r0 + c], k1 = _ke[8 * r1 + c];
            _ke[8 * r0 + c] = t.X * k0 + t.Y * k1;
            _ke[8 * r1 + c] = n.X * k0 + n.Y * k1;
        }

        for (int r = 0; r < 8; r++)
        {
            double k0 = _ke[8 * r + r0], k1 = _ke[8 * r + r1];
            _ke[8 * r + r0] = t.X * k0 + t.Y * k1;
            _ke[8 * r + r1] = n.X * k0 + n.Y * k1;
        }
    }

    private double Shape(double xi, double eta, int e)
    {
        _dxi[0] = -(1 - eta) / 4; _dxi[1] = (1 - eta) / 4; _dxi[2] = (1 + eta) / 4; _dxi[3] = -(1 + eta) / 4;
        _deta[0] = -(1 - xi) / 4; _deta[1] = -(1 + xi) / 4; _deta[2] = (1 + xi) / 4; _deta[3] = (1 - xi) / 4;
        _sn[0] = (1 - xi) * (1 - eta) / 4;
        _sn[1] = (1 + xi) * (1 - eta) / 4;
        _sn[2] = (1 + xi) * (1 + eta) / 4;
        _sn[3] = (1 - xi) * (1 + eta) / 4;
        double j11 = 0, j12 = 0, j21 = 0, j22 = 0;
        for (int a = 0; a < 4; a++)
        {
            j11 += _dxi[a] * _ex[a];
            j12 += _dxi[a] * _ey[a];
            j21 += _deta[a] * _ex[a];
            j22 += _deta[a] * _ey[a];
        }

        double det = j11 * j22 - j12 * j21;
        if (!(det > 0))
            throw new MeshDistortionException(F($"Element {e} has a non-positive Jacobian ({det:E2}) at step {StepIndex}."));
        for (int a = 0; a < 4; a++)
        {
            _dnx[a] = (j22 * _dxi[a] - j12 * _deta[a]) / det;
            _dny[a] = (-j21 * _dxi[a] + j11 * _deta[a]) / det;
        }

        return det;
    }

    private void ElementKernel(int e, bool matrix, bool newton, ref double energy, ref double power)
    {
        if (matrix) Array.Clear(_ke);
        Array.Clear(_re);
        double eps = _eps[e];
        double sigCut = _law.Stress(eps, _rateCut);
        double mu0 = sigCut / _rateCut;
        double mExp = _law.M;
        double sigSum = 0, rateSum = 0, s0Sum = 0, s1Sum = 0, s2Sum = 0, tSum = 0;
        var b0 = _b[0];
        var b1 = _b[1];
        var b2 = _b[2];
        var b3 = _b[3];

        for (int g = 0; g < 4; g++)
        {
            double det = Shape(GXi[g], GEta[g], e);
            double r = 0;
            for (int a = 0; a < 4; a++) r += _sn[a] * _ex[a];
            double w = det * Weight(r);
            for (int a = 0; a < 4; a++)
            {
                b0[2 * a] = _dnx[a]; b0[2 * a + 1] = 0;
                b1[2 * a] = 0; b1[2 * a + 1] = _dny[a];
                b2[2 * a] = _axi ? _sn[a] / r : 0; b2[2 * a + 1] = 0;
                b3[2 * a] = _dny[a]; b3[2 * a + 1] = _dnx[a];
            }

            double e0 = 0, e1 = 0, e2 = 0, e3 = 0;
            for (int k = 0; k < 8; k++)
            {
                e0 += b0[k] * _ve[k];
                e1 += b1[k] * _ve[k];
                e2 += b2[k] * _ve[k];
                e3 += b3[k] * _ve[k];
            }

            double ev3 = (e0 + e1 + e2) / 3;
            double d0 = e0 - ev3, d1 = e1 - ev3, d2 = e2 - ev3;
            double rate = Math.Sqrt(Math.Max(2.0 / 3.0 * (d0 * d0 + d1 * d1 + d2 * d2) + e3 * e3 / 3.0, 0));
            double m0 = 2.0 / 3.0 * d0, m1 = 2.0 / 3.0 * d1, m2 = 2.0 / 3.0 * d2, m3 = e3 / 3.0;
            for (int k = 0; k < 8; k++) _g8[k] = b0[k] * m0 + b1[k] * m1 + b2[k] * m2 + b3[k] * m3;

            double mu, sig, wDens, coefN;
            if (rate > _rateCut)
            {
                sig = _law.Stress(eps, rate);
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

            energy += w * wDens;
            power += w * sig * rate;
            sigSum += sig;
            rateSum += rate;
            s0Sum += mu * m0;
            s1Sum += mu * m1;
            s2Sum += mu * m2;
            tSum += mu * m3;
            double wm = w * mu;
            for (int k = 0; k < 8; k++) _re[k] += wm * _g8[k];
            if (!matrix) continue;

            for (int k = 0; k < 8; k++)
            {
                double tr = (b0[k] + b1[k] + b2[k]) / 3;
                _mb[0][k] = 2.0 / 3.0 * (b0[k] - tr);
                _mb[1][k] = 2.0 / 3.0 * (b1[k] - tr);
                _mb[2][k] = 2.0 / 3.0 * (b2[k] - tr);
                _mb[3][k] = b3[k] / 3.0;
            }

            double wn = newton ? w * coefN : 0;
            for (int p = 0; p < 8; p++)
            {
                for (int q = 0; q <= p; q++)
                {
                    double s = b0[p] * _mb[0][q] + b1[p] * _mb[1][q] + b2[p] * _mb[2][q] + b3[p] * _mb[3][q];
                    _ke[8 * p + q] += wm * s + wn * _g8[p] * _g8[q];
                }
            }
        }

        // Volumetric penalty, 1-point reduced integration.
        double det0 = Shape(0, 0, e);
        double rc = 0;
        for (int a = 0; a < 4; a++) rc += _sn[a] * _ex[a];
        double w0 = 4 * det0 * Weight(rc);
        double evol = 0;
        for (int a = 0; a < 4; a++)
        {
            _c8[2 * a] = _dnx[a] + (_axi ? _sn[a] / rc : 0);
            _c8[2 * a + 1] = _dny[a];
        }

        for (int k = 0; k < 8; k++) evol += _c8[k] * _ve[k];
        double kw = _kPen * w0;
        for (int k = 0; k < 8; k++) _re[k] += kw * evol * _c8[k];
        energy += 0.5 * kw * evol * evol;
        power += kw * evol * evol;
        if (matrix)
        {
            for (int p = 0; p < 8; p++)
            {
                for (int q = 0; q <= p; q++) _ke[8 * p + q] += kw * _c8[p] * _c8[q];
            }

            // mirror lower → upper so the nodal rotation sees the full matrix
            for (int p = 0; p < 8; p++)
            {
                for (int q = p + 1; q < 8; q++) _ke[8 * p + q] = _ke[8 * q + p];
            }
        }

        _elSig[e] = sigSum / 4;
        _elRate[e] = rateSum / 4;
        _elDev[4 * e] = s0Sum / 4;
        _elDev[4 * e + 1] = s1Sum / 4;
        _elDev[4 * e + 2] = s2Sum / 4;
        _elDev[4 * e + 3] = tSum / 4;
        _elMean[e] = _kPen * evol;
    }

    /// <summary>Normalized Cockcroft–Latham damage increment D += max(σ₁, 0)/σ̄ · ε̄̇ · Δt (σ₁ from the in-plane tensor and the hoop / out-of-plane stress).</summary>
    private void AccumulateDamage(double dt)
    {
        for (int e = 0; e < _ne; e++)
        {
            double sig = _elSig[e];
            if (!(sig > 0) || _elRate[e] <= _rateCut) continue;
            double m = _elMean[e];
            double s1 = Principal.Max2D(_elDev[4 * e] + m, _elDev[4 * e + 1] + m, _elDev[4 * e + 3], _elDev[4 * e + 2] + m);
            if (s1 > 0) _dmg[e] += s1 / sig * _elRate[e] * dt;
        }
    }

    /// <summary>Residual at the converged velocity: element fields, reaction load and tensile contact release.</summary>
    private double FinalEvaluation(out double reactionLoad)
    {
        Assemble(false, false, _v, out double power);
        reactionLoad = 0;
        double maxCompBottom = 0, maxCompTop = 0;
        for (int n = 0; n < _nn; n++)
        {
            _vg[n] = GlobalVelocity(_v, n);
            if (_contact[n] == NodeContact.Free) continue;
            double r0 = _r[Dof(n, 0)], r1 = _r[Dof(n, 1)];
            var rg = _rotated[n] ? r0 * _tan[n] + r1 * _nrm[n] : new Vec2(r0, r1);
            // R = ∂Π/∂v equals the external (die) force on the workpiece; the top die pushes downward.
            if (_contact[n] == NodeContact.TopDie) reactionLoad -= rg.Y;
            if (!_rotated[n]) continue;
            double comp = r1;
            if (_contact[n] == NodeContact.TopDie) maxCompTop = Math.Max(maxCompTop, comp);
            else maxCompBottom = Math.Max(maxCompBottom, comp);
        }

        // Release nodes where the die would have to pull on the workpiece; keep at least one node per die.
        int keptBottom = 0, keptTop = 0;
        for (int n = 0; n < _nn; n++)
        {
            if (_contact[n] == NodeContact.Free) continue;
            if (!_rotated[n])
            {
                if (_contact[n] == NodeContact.TopDie) keptTop++; else keptBottom++;
                continue;
            }

            double comp = _r[Dof(n, 1)];
            double thr = 0.02 * (_contact[n] == NodeContact.TopDie ? maxCompTop : maxCompBottom);
            if (comp < -thr) _release[n] = true;
            else if (_contact[n] == NodeContact.TopDie) keptTop++;
            else keptBottom++;
        }

        if (keptTop == 0 || keptBottom == 0)
        {
            for (int n = 0; n < _nn; n++)
            {
                if ((keptTop == 0 && _contact[n] == NodeContact.TopDie) || (keptBottom == 0 && _contact[n] == NodeContact.BottomDie))
                    _release[n] = false;
            }
        }

        return power;
    }

    // ------------------------------------------------------------------ geometry update and contact

    /// <param name="allowFold">Accept an update that folds elements (only the outline is kept, see <see cref="RemeshOnFoldedUpdate"/>).</param>
    private bool TryUpdate(double dt, out string why, bool allowFold = false)
    {
        double gapT = Math.Max(0, _gap - _ram * dt);
        if (gapT < 1e-9) gapT = 0;
        var xT = new Vec2[_nn];
        var cT = new NodeContact[_nn];
        double tolSep = 0.05 * _h;
        double maxPen = 0;

        for (int n = 0; n < _nn; n++)
        {
            bool axis = _t.IsAxisNode(n);
            var p = _x[n] + dt * _vg[n];
            if (axis) p = new Vec2(0, p.Y);
            var c = _contact[n];
            if (c != NodeContact.Free && !_release[n])
            {
                var surf = Surface(c);
                double off = Offset(c, gapT);
                if (surf.Clearance(p, off) > tolSep)
                {
                    cT[n] = NodeContact.Free;
                }
                else
                {
                    p = axis ? new Vec2(0, surf.HeightAt(0) + off) : surf.Project(p, off, out _, out _);
                    cT[n] = c;
                }
            }
            else
            {
                double cb = _die.Bottom.Clearance(p, 0);
                double ctp = _die.TopClosed.Clearance(p, gapT);
                if (cb < 0 || ctp < 0)
                {
                    var hit = cb <= ctp ? NodeContact.BottomDie : NodeContact.TopDie;
                    var surf = Surface(hit);
                    double off = Offset(hit, gapT);
                    var q = axis ? new Vec2(0, surf.HeightAt(0) + off) : surf.Project(p, off, out _, out _);
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

        if (allowFold && xT.Any(p => p.X < 0))
        {
            why = "node crossed the axis";
            return false;
        }

        for (int e = 0; e < _ne && !allowFold; e++)
        {
            if (Quality(xT, e) <= 0.002)
            {
                why = F($"element {e} folded");
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

    /// <summary>
    /// Last resort when every cutback folds an element: in a thin section or a die fillet the contact projection can
    /// lay all nodes of a boundary element on the die, which no step size avoids. The update is then accepted with
    /// the folded element, the strain and damage are integrated on the old connectivity, and the workpiece is
    /// remeshed on the new outline (remeshing only uses the boundary loop). Tries the full increment first, then
    /// halves it; everything is rolled back when no remesh is accepted.
    /// </summary>
    private bool RemeshOnFoldedUpdate(double dt, out double dtUsed)
    {
        var savedX = _x;
        var savedContact = _contact;
        double savedGap = _gap, savedTime = _time;
        var savedEps = (double[])_eps.Clone();
        var savedDmg = (double[])_dmg.Clone();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            dtUsed = dt / (1 << attempt);
            if (!TryUpdate(dtUsed, out _, allowFold: true)) continue;
            for (int e = 0; e < _ne; e++) _eps[e] += _elRate[e] * dtUsed;
            AccumulateDamage(dtUsed);
            if (Remesh(F($"fold during update, remeshed on the updated outline (Δt/{1 << attempt})"))) return true;
            _x = savedX;
            _contact = savedContact;
            _gap = savedGap;
            _time = savedTime;
            Array.Copy(savedEps, _eps, _ne);
            Array.Copy(savedDmg, _dmg, _ne);
        }

        dtUsed = 0;
        return false;
    }

    /// <summary>Minimum corner scaled Jacobian of element e (1 = rectangle, ≤ 0 = folded).</summary>
    private double Quality(Vec2[] x, int e)
    {
        var conn = _t.Connectivity;
        double q = double.MaxValue;
        for (int a = 0; a < 4; a++)
        {
            var p = x[conn[4 * e + a]];
            var nx = x[conn[4 * e + (a + 1) % 4]] - p;
            var pv = x[conn[4 * e + (a + 3) % 4]] - p;
            double l = nx.Length * pv.Length;
            q = Math.Min(q, l > 0 ? nx.Cross(pv) / l : -1);
        }

        return q;
    }

    private double MinQuality(Vec2[] x)
    {
        double q = double.MaxValue;
        for (int e = 0; e < _ne; e++) q = Math.Min(q, Quality(x, e));
        return q;
    }

    /// <summary>
    /// Fold-over capture: a free-surface node that has rotated to within ~25° of the die next to a contact node is
    /// laid onto the die. Node-to-surface contact alone would only catch it after the adjacent element collapsed.
    /// </summary>
    private int CaptureFoldOver()
    {
        var loop = _t.BoundaryLoop;
        int m = loop.Length;
        double sinLimit = Math.Sin(25 * Math.PI / 180);
        int captured = 0;
        bool changed = true;
        for (int pass = 0; pass < 4 && changed; pass++)
        {
            changed = false;
            for (int k = 0; k < m; k++)
            {
                int n = loop[k];
                var c = _contact[n];
                if (c == NodeContact.Free) continue;
                foreach (int f in new[] { loop[(k + m - 1) % m], loop[(k + 1) % m] })
                {
                    if (_contact[f] != NodeContact.Free || _t.IsAxisNode(f)) continue;
                    var surf = Surface(c);
                    double off = Offset(c, _gap);
                    surf.Project(_x[n], off, out var nrm, out _);
                    var u = _x[f] - _x[n];
                    double len = u.Length;
                    if (len <= 0 || u.Dot(nrm) / len > sinLimit) continue;
                    var q = surf.Project(_x[f], off, out _, out _);
                    if ((q - _x[f]).Length > 0.5 * _h) continue;
                    _x[f] = q;
                    _contact[f] = c;
                    captured++;
                    changed = true;
                }
            }
        }

        return captured;
    }

    // ------------------------------------------------------------------ remeshing

    private string? RemeshTrigger()
    {
        double q = MinQuality(_x);
        if (q < RemeshQuality) return F($"element quality {q:F2}");
        if (DieCornerPierces(out double depth)) return F($"die corner penetration {depth:F3} mm");
        var loop = _t.BoundaryLoop;
        double maxEdge = 0;
        for (int k = 0; k < loop.Length; k++) maxEdge = Math.Max(maxEdge, (_x[loop[(k + 1) % loop.Length]] - _x[loop[k]]).Length);
        if (maxEdge > 2.5 * _h) return F($"boundary edge stretched to {maxEdge:F2} mm");
        return null;
    }

    /// <summary>True when a die feature corner has entered the workpiece outline deeper than 0.15·h.</summary>
    private bool DieCornerPierces(out double depth)
    {
        depth = 0;
        var poly = _t.BoundaryLoop.Select(n => _x[n]).ToArray();
        foreach (var side in new[] { NodeContact.BottomDie, NodeContact.TopDie })
        {
            double off = Offset(side, _gap);
            foreach (var corner in Surface(side).Corners)
            {
                var v = new Vec2(corner.X, corner.Y + off);
                if (!TriangleMesher.Inside(poly, v)) continue;
                depth = Math.Max(depth, TriangleMesher.DistanceToPolygon(poly, v));
            }
        }

        return depth > 0.15 * _h;
    }

    /// <summary>
    /// Rebuilds the mesh on the current workpiece outline: the boundary is resampled at spacing 2h with contact
    /// transitions, axis ends and sharp turns kept as anchors (die-contact samples re-projected and pinned to die
    /// corners), triangulated by Delaunay, split into quads, and the state is remapped from the old mesh.
    /// </summary>
    private bool Remesh(string reason, bool requireImprovement = false)
    {
        var savedX = (Vec2[])_x.Clone();
        var savedContact = (NodeContact[])_contact.Clone();
        double qBefore = MinQuality(_x);
        CaptureFoldOver();
        if (!RemeshCore(reason, requireImprovement ? qBefore : double.NegativeInfinity))
        {
            _x = savedX;
            _contact = savedContact;
            return false;
        }

        return true;
    }

    private bool RemeshCore(string reason, double mustBeat)
    {
        var loop = _t.BoundaryLoop;
        int m = loop.Length;
        var p = loop.Select(n => _x[n]).ToArray();
        var st = loop.Select(n => _contact[n]).ToArray();
        var ax = loop.Select(n => _t.IsAxisNode(n)).ToArray();

        var anchor = new bool[m];
        for (int k = 0; k < m; k++)
        {
            int kp = (k + m - 1) % m, kn = (k + 1) % m;
            var d1 = p[k] - p[kp];
            var d2 = p[kn] - p[k];
            double turn = Math.Abs(Math.Atan2(d1.Cross(d2), d1.Dot(d2)));
            anchor[k] = turn > 35 * Math.PI / 180 || st[k] != st[kp] || st[k] != st[kn] || ax[k] != ax[kp] || ax[k] != ax[kn];
        }

        int first = Array.IndexOf(anchor, true);
        if (first < 0) first = 0;
        anchor[first] = true;

        double hb = 1.2 * _h;
        var bp = new List<Vec2>();
        var bs = new List<NodeContact>();
        var ba = new List<bool>();
        int k0 = first;
        do
        {
            int k1 = (k0 + 1) % m;
            while (!anchor[k1]) k1 = (k1 + 1) % m;
            // chain k0 → k1 (exclusive of k1)
            var chain = new List<int> { k0 };
            for (int k = (k0 + 1) % m; k != k1; k = (k + 1) % m) chain.Add(k);
            chain.Add(k1);
            var s = new double[chain.Count];
            for (int i = 1; i < chain.Count; i++) s[i] = s[i - 1] + (p[chain[i]] - p[chain[i - 1]]).Length;
            int count = Math.Max(1, (int)Math.Round(s[^1] / hb));
            for (int q = 0; q < count; q++)
            {
                double target = s[^1] * q / count;
                int seg = 0;
                while (seg < chain.Count - 2 && s[seg + 1] < target) seg++;
                double len = s[seg + 1] - s[seg];
                double t = len > 0 ? Math.Clamp((target - s[seg]) / len, 0, 1) : 0;
                int ka = chain[seg], kb = chain[seg + 1];
                bp.Add(Vec2.Lerp(p[ka], p[kb], t));
                bs.Add(q == 0 ? st[ka] : t < 1e-9 ? st[ka] : t > 1 - 1e-9 ? st[kb] : st[ka] == st[kb] ? st[ka] : NodeContact.Free);
                ba.Add(q == 0 ? ax[ka] : ax[ka] && ax[kb]);
            }

            k0 = k1;
        }
        while (k0 != first);

        // Merge points closer than 0.35·spacing (anchors next to resampled points create slivers). Axis-transition
        // points survive; otherwise the free-surface point of a close pair is dropped.
        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = bp.Count - 1; i >= 0 && bp.Count > 6; i--)
            {
                int j = (i + 1) % bp.Count;
                if ((bp[j] - bp[i]).Length >= 0.35 * hb) continue;
                int prevI = (i + bp.Count - 1) % bp.Count, nextJ = (j + 1) % bp.Count;
                bool iKey = ba[i] && (!ba[prevI] || !ba[j]);
                bool jKey = ba[j] && (!ba[i] || !ba[nextJ]);
                int drop = iKey && !jKey ? j : jKey && !iKey ? i : bs[i] == NodeContact.Free ? i : j;
                if (iKey && jKey) continue;
                bp.RemoveAt(drop);
                bs.RemoveAt(drop);
                ba.RemoveAt(drop);
                if (drop < i) i--;
            }
        }

        if (bp.Count < 6)
        {
            _log(F($"  remesh ({reason}) rejected: outline too coarse ({bp.Count} points)."));
            return false;
        }

        // Contact projection, penetration capture and die-corner pinning.
        for (int i = 0; i < bp.Count; i++)
        {
            if (bs[i] == NodeContact.Free)
            {
                if (_die.Bottom.Clearance(bp[i], 0) < 0) bs[i] = NodeContact.BottomDie;
                else if (_die.TopClosed.Clearance(bp[i], _gap) < 0) bs[i] = NodeContact.TopDie;
            }

            bp[i] = Place(bp[i], bs[i], ba[i]);
        }

        foreach (var side in new[] { NodeContact.BottomDie, NodeContact.TopDie })
        {
            double off = Offset(side, _gap);
            foreach (var corner in Surface(side).Corners)
            {
                // A contact chord spanning a die corner would cut through the die: put a node on the corner,
                // either by snapping a close endpoint or by inserting a new boundary point.
                var v = new Vec2(corner.X, corner.Y + off);
                int n = bp.Count;
                int bestEdge = -1;
                double bestD = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (bs[i] != side || bs[j] != side || ba[i] || ba[j]) continue;
                    var ab = bp[j] - bp[i];
                    double t = Math.Clamp((v - bp[i]).Dot(ab) / Math.Max(ab.Dot(ab), 1e-30), 0, 1);
                    double dd = (v - (bp[i] + t * ab)).Length;
                    if (dd < bestD) { bestD = dd; bestEdge = i; }
                }

                if (bestEdge < 0) continue;
                int e0 = bestEdge, e1 = (bestEdge + 1) % n;
                double len = (bp[e1] - bp[e0]).Length;
                if (bestD > 0.5 * len) continue;
                double d0 = (bp[e0] - v).Length, d1 = (bp[e1] - v).Length;
                if (Math.Min(d0, d1) < 0.3 * len) bp[d0 <= d1 ? e0 : e1] = v;
                else
                {
                    bp.Insert(e0 + 1, v);
                    bs.Insert(e0 + 1, side);
                    ba.Insert(e0 + 1, false);
                }
            }
        }

        TriangleMesher.Result tri;
        try
        {
            tri = TriangleMesher.Triangulate(bp, 2 * _h, at =>
            {
                int n = bs.Count + 1;
                int a = (at - 1 + n) % n, b = at % n; // neighbours after insertion are at-1 and at (old index)
                var sa = bs[a];
                var sb = bs[b % bs.Count];
                bs.Insert(at, sa == sb ? sa : NodeContact.Free);
                ba.Insert(at, ba[a] && ba[b % ba.Count]);
            });
        }
        catch (InvalidOperationException ex)
        {
            _log(F($"  remesh ({reason}) rejected: {ex.Message}"));
            return false;
        }

        var (mesh, x) = TriangleMesher.ToQuads(tri, v => v.X <= 1e-9 * Math.Max(1, _h));
        var contact = new NodeContact[mesh.NodeCount];
        int nb = tri.BoundaryCount;
        for (int i = 0; i < nb; i++) contact[i] = bs[i];

        // Midside boundary nodes inherit the contact of their two boundary vertices.
        var nloop = mesh.BoundaryLoop;
        for (int k = 0; k < nloop.Length; k++)
        {
            int n = nloop[k];
            if (n < nb) continue;
            int a = nloop[(k + nloop.Length - 1) % nloop.Length], b = nloop[(k + 1) % nloop.Length];
            if (a < nb && b < nb && contact[a] == contact[b] && contact[a] != NodeContact.Free)
            {
                // Project only if the chord midpoint is close to the die (no die corner between the vertices).
                var placed = Place(x[n], contact[a], mesh.IsAxisNode(n));
                if ((placed - x[n]).Length < 0.1 * (x[a] - x[b]).Length)
                {
                    contact[n] = contact[a];
                    x[n] = placed;
                }
            }
            else if (mesh.IsAxisNode(n))
            {
                x[n] = new Vec2(0, x[n].Y);
            }
        }

        // Volume compensation: offset free-surface nodes along their outward normals to restore the initial volume
        // lost to chordal resampling and contact projection (capped per remesh, never into a die).
        double deficit = _vol0 - Volume(mesh, x);
        var normals = new Vec2[nloop.Length];
        double rate = 0;
        for (int k = 0; k < nloop.Length; k++)
        {
            int n = nloop[k];
            if (contact[n] != NodeContact.Free || mesh.IsAxisNode(n)) continue;
            var tng = x[nloop[(k + 1) % nloop.Length]] - x[nloop[(k + nloop.Length - 1) % nloop.Length]];
            normals[k] = new Vec2(tng.Y, -tng.X).Normalized();
            rate += 0.5 * tng.Length * Weight(x[n].X);
        }

        if (rate > 0)
        {
            double shift = Math.Clamp(deficit / rate, -0.25 * _h, 0.25 * _h);
            for (int k = 0; k < nloop.Length; k++)
            {
                if (normals[k] == default) continue;
                var moved = x[nloop[k]] + shift * normals[k];
                // Never across the axis: a node at x < 0 has a negative axisymmetric weight 2πr (indefinite stiffness).
                if (moved.X > 0 && _die.Bottom.Clearance(moved, 0) > 0 && _die.TopClosed.Clearance(moved, _gap) > 0) x[nloop[k]] = moved;
            }
        }

        // Validate the new mesh.
        var oldT = _t;
        var oldX = _x;
        double qOld = MinQuality(_x);
        _t = mesh;
        _ne = mesh.ElementCount;
        double qNew = MinQuality(x);
        _t = oldT;
        _ne = oldT.ElementCount;
        if (qNew < 0.05 || qNew <= mustBeat)
        {
            _t = mesh;
            _ne = mesh.ElementCount;
            int worst = Enumerable.Range(0, _ne).MinBy(e => Quality(x, e));
            var wc = mesh.Connectivity;
            var wcen = 0.25 * (x[wc[4 * worst]] + x[wc[4 * worst + 1]] + x[wc[4 * worst + 2]] + x[wc[4 * worst + 3]]);
            _t = oldT;
            _ne = oldT.ElementCount;
            _log(F($"  remesh ({reason}) rejected: new mesh quality {qNew:F2} at ({wcen.X:F2}, {wcen.Y:F2})."));
            return false;
        }

        // Remap state from the old mesh.
        var locator = new ElementLocator(oldT, oldX, 2 * _h);
        var oldEps = _eps;
        var oldSig = _elSig;
        var oldVg = _vg;
        var newEps = new double[mesh.ElementCount];
        var newDmg = new double[mesh.ElementCount];
        var oldDmg = _dmg;
        var newSig = new double[mesh.ElementCount];
        var newRate = new double[mesh.ElementCount];
        var newMean = new double[mesh.ElementCount];
        var newVg = new Vec2[mesh.NodeCount];
        var mc = mesh.Connectivity;
        for (int e = 0; e < mesh.ElementCount; e++)
        {
            var cen = 0.25 * (x[mc[4 * e]] + x[mc[4 * e + 1]] + x[mc[4 * e + 2]] + x[mc[4 * e + 3]]);
            int found = locator.Find(cen);
            newEps[e] = found >= 0 ? oldEps[found] : 0;
            newDmg[e] = found >= 0 ? oldDmg[found] : 0;
            newSig[e] = found >= 0 ? oldSig[found] : _sigRef;
            newRate[e] = found >= 0 ? _elRate[found] : 0;
            newMean[e] = found >= 0 ? _elMean[found] : 0;
        }

        for (int n = 0; n < mesh.NodeCount; n++) newVg[n] = locator.Interpolate(x[n], oldVg);

        Allocate(mesh, x);
        _eps = newEps;
        _dmg = newDmg;
        _elSig = newSig;
        _elRate = newRate;
        _elMean = newMean;
        _vg = newVg;
        _contact = contact;
        RemeshCount++;
        _log(F($"  REMESH #{RemeshCount} ({reason}): q_min {qOld:F2} → {qNew:F2}, {_ne} elements, {_nn} nodes, half-band {_k.Bandwidth}, contacts {_contact.Count(c => c == NodeContact.BottomDie)}/{_contact.Count(c => c == NodeContact.TopDie)} (was {st.Count(c => c == NodeContact.BottomDie)}/{st.Count(c => c == NodeContact.TopDie)}, samples {bs.Count(c => c == NodeContact.BottomDie)}/{bs.Count(c => c == NodeContact.TopDie)} of {bs.Count})"));
        return true;
    }

    private Vec2 Place(Vec2 p, NodeContact c, bool axis)
    {
        if (c != NodeContact.Free)
        {
            var surf = Surface(c);
            double off = Offset(c, _gap);
            return axis ? new Vec2(0, surf.HeightAt(0) + off) : surf.Project(p, off, out _, out _);
        }

        return axis ? new Vec2(0, p.Y) : p;
    }

    /// <summary>Bucket grid for point location in a quad mesh (remapping).</summary>
    private sealed class ElementLocator
    {
        private readonly QuadMesh _mesh;
        private readonly Vec2[] _x;
        private readonly double _cell;
        private readonly double _x0;
        private readonly double _y0;
        private readonly int _nx;
        private readonly int _ny;
        private readonly List<int>[] _buckets;

        public ElementLocator(QuadMesh mesh, Vec2[] x, double cell)
        {
            _mesh = mesh;
            _x = x;
            _cell = cell;
            _x0 = x.Min(p => p.X) - cell;
            _y0 = x.Min(p => p.Y) - cell;
            _nx = (int)((x.Max(p => p.X) - _x0) / cell) + 2;
            _ny = (int)((x.Max(p => p.Y) - _y0) / cell) + 2;
            _buckets = new List<int>[_nx * _ny];
            var c = mesh.Connectivity;
            for (int e = 0; e < mesh.ElementCount; e++)
            {
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                for (int a = 0; a < 4; a++)
                {
                    var q = x[c[4 * e + a]];
                    minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X);
                    minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
                }

                for (int j = Cy(minY); j <= Cy(maxY); j++)
                {
                    for (int i = Cx(minX); i <= Cx(maxX); i++) (_buckets[j * _nx + i] ??= []).Add(e);
                }
            }
        }

        private int Cx(double v) => Math.Clamp((int)((v - _x0) / _cell), 0, _nx - 1);

        private int Cy(double v) => Math.Clamp((int)((v - _y0) / _cell), 0, _ny - 1);

        public int Find(Vec2 p)
        {
            var list = _buckets[Cy(p.Y) * _nx + Cx(p.X)];
            if (list is null) return -1;
            var c = _mesh.Connectivity;
            foreach (int e in list)
            {
                var a = _x[c[4 * e]];
                var b = _x[c[4 * e + 1]];
                var cc = _x[c[4 * e + 2]];
                var d = _x[c[4 * e + 3]];
                if (InTriangle(a, b, cc, p) || InTriangle(a, cc, d, p)) return e;
            }

            return -1;
        }

        public Vec2 Interpolate(Vec2 p, Vec2[] field)
        {
            int e = Find(p);
            var c = _mesh.Connectivity;
            if (e < 0)
            {
                int best = 0;
                double dmin = double.MaxValue;
                for (int n = 0; n < _x.Length; n++)
                {
                    double dd = (_x[n] - p).Dot(_x[n] - p);
                    if (dd < dmin) { dmin = dd; best = n; }
                }

                return field[best];
            }

            int n0 = c[4 * e], n1 = c[4 * e + 1], n2 = c[4 * e + 2], n3 = c[4 * e + 3];
            return InTriangle(_x[n0], _x[n1], _x[n2], p)
                ? Barycentric(_x[n0], _x[n1], _x[n2], field[n0], field[n1], field[n2], p)
                : Barycentric(_x[n0], _x[n2], _x[n3], field[n0], field[n2], field[n3], p);
        }

        private static bool InTriangle(Vec2 a, Vec2 b, Vec2 c, Vec2 p)
        {
            double d1 = (b - a).Cross(p - a), d2 = (c - b).Cross(p - b), d3 = (a - c).Cross(p - c);
            bool neg = d1 < -1e-12 || d2 < -1e-12 || d3 < -1e-12;
            bool pos = d1 > 1e-12 || d2 > 1e-12 || d3 > 1e-12;
            return !(neg && pos);
        }

        private static Vec2 Barycentric(Vec2 a, Vec2 b, Vec2 c, Vec2 fa, Vec2 fb, Vec2 fc, Vec2 p)
        {
            double area = (b - a).Cross(c - a);
            if (Math.Abs(area) < 1e-300) return fa;
            double wb = (p - a).Cross(c - a) / area;
            double wc = (b - a).Cross(p - a) / area;
            return (1 - wb - wc) * fa + wb * fb + wc * fc;
        }
    }

    // ------------------------------------------------------------------ telemetry

    private double Volume(Vec2[] x) => Volume(_t, x);

    private double Volume(QuadMesh mesh, Vec2[] x)
    {
        var loop = mesh.BoundaryLoop;
        double area = 0, moment = 0;
        for (int k = 0; k < loop.Length; k++)
        {
            var a = x[loop[k]];
            var b = x[loop[(k + 1) % loop.Length]];
            double cr = a.X * b.Y - b.X * a.Y;
            area += 0.5 * cr;
            moment += (a.X + b.X) * cr / 6.0;
        }

        return _axi ? 2 * Math.PI * moment : 2 * _cfg.BilletLength * area;
    }

    private (double[] X, double[] Y, double[] W, double Total) BuildCavitySamples()
    {
        double rc = _die.CavityOuterRadius;
        var (bMin, _) = _die.Bottom.HeightRange(0, rc);
        var (_, tMax) = _die.TopClosed.HeightRange(0, rc);
        const int nx = 160, ny = 120;
        var xs = new List<double>();
        var ys = new List<double>();
        var ws = new List<double>();
        double total = 0;
        for (int i = 0; i < nx; i++)
        {
            double x = (i + 0.5) * rc / nx;
            double zb = _die.Bottom.HeightAt(x), zt = _die.TopClosed.HeightAt(x);
            for (int j = 0; j < ny; j++)
            {
                double y = bMin + (j + 0.5) * (tMax - bMin) / ny;
                if (y <= zb || y >= zt) continue;
                double w = Weight(x);
                xs.Add(x);
                ys.Add(y);
                ws.Add(w);
                total += w;
            }
        }

        return (xs.ToArray(), ys.ToArray(), ws.ToArray(), total);
    }

    private double FillPercent()
    {
        if (_cavTotal <= 0) return 0;
        var poly = _t.BoundaryLoop.Select(n => _x[n]).ToArray();
        double minX = poly.Min(q => q.X), maxX = poly.Max(q => q.X), minY = poly.Min(q => q.Y), maxY = poly.Max(q => q.Y);
        double inside = 0;
        for (int s = 0; s < _cavX.Length; s++)
        {
            double x = _cavX[s], y = _cavY[s];
            if (x < minX || x > maxX || y < minY || y > maxY) continue;
            if (TriangleMesher.Inside(poly, new Vec2(x, y))) inside += _cavW[s];
        }

        return 100.0 * inside / _cavTotal;
    }

    private StepSnapshot MakeSnapshot(int step, double load, int iterations)
    {
        var xs = new float[_nn];
        var ys = new float[_nn];
        for (int n = 0; n < _nn; n++)
        {
            xs[n] = (float)_x[n].X;
            ys[n] = (float)_x[n].Y;
        }

        bool initial = step == 0;
        var vm = new float[_ne];
        var ep = new float[_ne];
        var dm = new float[_ne];
        var er = new float[_ne];
        var sm = new float[_ne];
        for (int e = 0; e < _ne; e++)
        {
            vm[e] = initial ? 0f : (float)_elSig[e];
            ep[e] = (float)_eps[e];
            dm[e] = (float)_dmg[e];
            er[e] = initial ? 0f : (float)_elRate[e];
            sm[e] = initial ? 0f : (float)_elMean[e];
        }

        return new StepSnapshot
        {
            Step = step,
            Time = _time,
            Stroke = _gap0 - _gap,
            DieGap = _gap,
            LoadNewton = load,
            FillPercent = FillPercent(),
            VolumeChangePercent = 100.0 * (Volume(_x) - _vol0) / _vol0,
            Iterations = iterations,
            Mesh = _t,
            X = xs,
            Y = ys,
            VonMises = vm,
            EffectiveStrain = ep,
            Damage = dm,
            EffectiveStrainRate = er,
            MeanStress = sm,
            Contact = (NodeContact[])_contact.Clone(),
            VelocityX = initial ? [] : Enumerable.Range(0, _nn).Select(n => (float)GlobalVelocity(_v, n).X).ToArray(),
            VelocityY = initial ? [] : Enumerable.Range(0, _nn).Select(n => (float)GlobalVelocity(_v, n).Y).ToArray(),
        };
    }
}
