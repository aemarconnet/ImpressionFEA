<p align="center"><img src="assets/ImpressionFEA-256.png" width="160" alt="ImpressionFEA logo"></p>

# ImpressionFEA — Closed-Die Impression Forging Simulator

**by Marconnet Technologies LLC** · [CC BY-NC 4.0](LICENSE) · see [NOTICE.md](NOTICE.md) for attribution and third-party licenses

Windows desktop application (WPF, .NET 10) for closed-die impression forging simulation with built-in native
finite-element solvers: a fast **2D** solver (axisymmetric / plane strain) and a full **3D** solver (tetrahedra) with
GPU (NVIDIA cuDSS) or multithreaded CPU (Intel MKL) acceleration. No external solver is needed. 3D jobs can come
from the built-in die presets or from a **STEP assembly of three bodies** (bottom die, workpiece, top die).

```
powershell -ExecutionPolicy Bypass -File native/fetch-cuda.ps1   # optional: GPU solver libraries (~880 MB)
powershell -ExecutionPolicy Bypass -File native/cad/build-cad.ps1 # optional: STEP import (needs OCCT 8.0.1 + VS C++)
dotnet build -c Release
dotnet run --project src/ImpressionFEA.App -c Release
dotnet test -c Release --filter "Category!=Slow"                 # fast suite; drop the filter for full forging runs
```

The solution is built with `TreatWarningsAsErrors=true` and compiles with zero warnings.

## Layout

```
src/ImpressionFEA.Core/            solver library (no UI dependencies)
  Geometry/   Vec2, DieSurface (rigid die profile, projection, feature corners), DieSet, DiePresets
  Materials/  FlowStressLaw, MaterialPresets (4140, 6061, C11000, plasticine)
  Mesh/       QuadMesh (topology, RCM numbering, boundary loop), TriangleMesher (Delaunay remesher)
  Solver/     ForgingSolver (FEM), SymmetricBandMatrix (band Cholesky), SolverExceptions
  Solver/Linear/ ISpdSolver + backends: CudssSolver (GPU), MklPardisoSolver (CPU), ManagedSpdSolver,
              AutoTuningSolver, SpdSolverFactory (probe + fallback)
  ThreeD/     Forging3DSolver (3D FEM), TetMesh/TetLocator, IsosurfaceStuffing (tet mesher), HeightFieldDie,
              DieSet3D + DiePresets3D (connecting rod, gear blank, upset block), SimulationConfig3D
  Simulation/ SimulationConfig (input deck + validation), ForgingSnapshot (2D StepSnapshot / Snapshot3D),
              ISimulationEngine + NativeFemEngine (background batch job, 2D and 3D)
native/                        GPU solver redistributables (fetched by fetch-cuda.ps1), licences
src/ImpressionFEA.App/             WPF dashboard
  ViewModels/MainViewModel     inputs, run orchestration, thread marshalling, telemetry chart (OxyPlot)
  Controls/FieldViewport       2D contour renderer (WriteableBitmap rasteriser + WPF vectors)
  Controls/SolidViewport3D     3D renderer (WPF Viewport3D): revolved/extruded 2D results and true 3D results,
                               banded contour textures, cut-away sections, translucent dies, SolidWorks-style camera
  Controls/NumericField        labelled numeric entry
tests/ImpressionFEA.Core.Tests/    analytical benchmarks, numerics, engine error paths, preset runs
```

## STEP import (3 bodies)

**Import STEP…** (configuration panel or bottom bar) reads a STEP file through Open CASCADE (`native/cad`):

1. The file must contain exactly **three solid bodies**. Units are converted to mm.
2. The body whose centre of gravity lies **between** the other two is the deforming **workpiece**. Assign its
   material in the Material panel. The two outer bodies are **rigid dies** (no internal stresses).
3. You pick which outer body is the **top (moving) die**. The press axis runs through the two die centres of
   gravity and is snapped to the nearest file axis when within 3°. Z-up and Y-up (SolidWorks) models both work.
4. The workpiece is dropped onto the bottom die, the top die starts at first contact, and the simulation runs
   until the **top die touches the bottom die**.

Imported jobs are full 3D models with no symmetry assumed. Cavity fill counts the region where the closed dies
leave more than 15 % of the deepest gap, so thin flash lands are excluded. `samples/` holds three generated test
assemblies (upset, gear blank, hex flange).

To enable STEP import, download the OCCT 8.0.1 Windows packages (`opencascade-release-no-pch.zip` and
`3rdparty-vc14-64.zip`) from the Open CASCADE GitHub releases. Extract them as described at the top of
`native/cad/build-cad.ps1`, then run that script with Visual Studio 2022 C++ tools installed. Without it, the app
runs normally and the Import button explains that STEP support is missing.

## Physics

* **Formulation**: rigid-viscoplastic flow formulation (Kobayashi–Oh–Altan). Axisymmetric (gear blank, flanged
  shaft, upset block) or plane-strain (connecting-rod shank section) half-models.
* **Material**: isothermal `σ̄ = C(T)·(ε₀+ε̄)ⁿ·ε̄̇ᵐ` with `C(T) = C_ref·exp(−β(T−T_ref))`. The presets are
  representative handbook-level coefficients, not certified material data. A warning is logged outside each
  preset's calibrated temperature window.
* **Elements**: 4-node quads with 2×2 Gauss for the deviatoric term and 1-point reduced integration for the
  volumetric penalty, so there is no volumetric locking and no hourglassing.
* **Solution**: the convex functional Π is minimised by two Picard iterations, then Newton–Raphson with an Armijo
  line search. The stiffness matrix is factorised by band Cholesky with reverse Cuthill–McKee numbering.
* **Contact/friction**: node-to-surface contact on rigid dies with local nodal frames. Friction is constant shear
  `τ = m·k`, arctan-regularised. Nodes are released when the die reaction turns tensile, and a free surface that
  folds onto a die is captured.
* **Large deformation**: updated-Lagrangian increments with automatic remeshing. The current outline is
  triangulated (Delaunay + refinement) and each triangle is split into three quads. Die corners are pinned to
  nodes, effective strain, flow stress and velocity are remapped, and the volume is compensated.
* **Outputs**: press load (power-based, cross-checked against the die reaction in the log), cavity fill (volume
  fraction of the closed impression), and von Mises stress, effective strain, strain rate and mean stress.

### 3D solver

* Same functional, material law, friction law and Newton scheme as 2D, on **linear tetrahedra** with an
  **averaged-nodal-pressure** (Bonet–Burton ANP) penalty for incompressibility, which removes the volumetric locking
  of linear tets.
* **Dies** are height fields z = h(x, y) for the presets, or imported triangulated CAD solids (inside test by ray
  parity, closest-point contact on a bucket grid). **Symmetry planes** give half or quarter models; nodal frames from Gram–Schmidt combine
  symmetry and die constraints. Friction is applied as a vector on the tangential axes.
* **Remeshing** uses isosurface stuffing (Labelle & Shewchuk 2007) on a BCC lattice, driven by inside tests on the
  deformed mesh. This guarantees well-shaped tets and keeps symmetry planes exactly flat. State is remapped and the
  volume compensated.
* **Linear algebra**: one sparse SPD factorisation per Newton iteration on a fixed pattern per mesh, plus
  preconditioned CG reuse of the last factor when that is cheaper. Backends: **NVIDIA cuDSS (GPU)**, **Intel MKL
  PARDISO (all CPU cores)** or managed CSparse. *Auto* benchmarks GPU vs CPU on the first factorisation and keeps the
  faster one (see `native/README.md`). Measured on a 17 k-DOF gear-blank system: managed 14 s, cuDSS (RTX 4000 Ada)
  ≈ 0.1–0.35 s, MKL (32-core Threadripper) ≈ 0.09–0.18 s.

## Verification (see `tests/`)

| Case | Expected | Result |
|---|---|---|
| Frictionless axisymmetric upset | `F = σ̄(ε̇)·πR²` | within 0.5 % |
| Frictionless plane-strain upset | `F = (2/√3)·σ̄·2W·L` | within 0.5 % |
| Upset with m = 0.5 | above frictionless, below the Avitzur upper bound | passes |
| Full upset to shut height | volume change < 0.5 % | ≈ −0.07 % |
| All four presets to die closure | fill > 90 %, volume change < 2 % | 97–100 % fill, ≤ 0.3 % |
| Isosurface stuffing (cylinder, all symmetries) | manifold, volume ±3 %, no slivers | q_min ≈ 0.32 |
| 3D frictionless upset | `F = σ̄·A` | within 1 % |
| GPU / MKL vs managed backend | identical solution | agree to 10⁻⁶ |
| 3D vs 2D upset (m = 0.3), full stroke | same peak load | 34.8 t vs 35.5 t (2 %) |

## 3D viewport controls (SolidWorks-style)

Middle-drag rotate · Ctrl+middle-drag pan · Shift+middle-drag or wheel zoom (about the cursor) · Alt+middle-drag roll ·
**F** fit (or double-click middle) · **Z** / **Shift+Z** zoom out / in · **Ctrl+1…7** Front, Back, Left, Right, Top,
Bottom, Isometric · **Space** view menu · arrow keys rotate 15° (Shift 90°, Ctrl pan, Alt+←/→ roll). Click the viewport
first so it has keyboard focus.

## Versioning

The release number is `<Version>` in `Directory.Build.props`. It appears in the title bar with a build stamp. See
[CHANGELOG.md](CHANGELOG.md).

## License

ImpressionFEA © 2026 **Marconnet Technologies LLC**, licensed under the
[Creative Commons Attribution-NonCommercial 4.0 International License](https://creativecommons.org/licenses/by-nc/4.0/).
You may share and adapt it for non-commercial purposes, provided you credit Marconnet Technologies LLC. Commercial
use requires written permission. Third-party libraries keep their own licenses ([NOTICE.md](NOTICE.md)).

## Threading and error handling

* The solver runs on a background thread (`Task.Run`). Log lines go to a `ConcurrentQueue`, which a
  `DispatcherTimer` drains on the UI thread. Increment snapshots are marshalled with `Dispatcher.InvokeAsync`.
* `NativeFemEngine.RunAsync` never throws for solver failures. Invalid input, Newton divergence, non-SPD
  stiffness, mesh distortion and operator abort are all returned as a `SimulationResult` with a status and
  message, and partial results stay available for scrubbing.
* Unhandled UI exceptions are written to `impressionfea-crash.log` next to the executable.

## Extending with an external solver

Implement `ISimulationEngine` (for example a FreeFem++ or CalculiX wrapper around `System.Diagnostics.Process`).
`Probe()` should check that the binary exists and report a missing dependency. `RunAsync` should generate the
input deck, stream stdout to `ISimulationObserver.OnLog`, and parse the exported tables into `StepSnapshot`s.

## Limitations

Isothermal (no die chill or heat generation), rigid dies, constant shear friction. Imported models run as full
models; automatic symmetry detection is not implemented yet. 3D runs take minutes and are
mesh-size limited (≈12 k nodes). Load and fill are engineering estimates, not certified predictions.

## Headless capture

`ImpressionFEA.App.exe --capture out.png [--solve] [--die N] [--3d] [--analysis3d --die3d N] [--import file.step]` renders the dashboard to
a PNG (optionally after a full solve) and exits. It's useful as a smoke test.
