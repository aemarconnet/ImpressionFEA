<p align="center"><img src="assets/ImpressionFEA-256.png" width="160" alt="ImpressionFEA logo"></p>

# ImpressionFEA — Closed-Die Impression Forging Simulator

**by Marconnet Technologies LLC** · [CC BY-NC 4.0](LICENSE) · see [NOTICE.md](NOTICE.md) for attribution and third-party licenses

Windows desktop application (WPF, .NET 10) for closed-die impression forging simulation with built-in native
finite-element solvers: a fast **2D** solver (axisymmetric / plane strain) and a full **3D** solver (tetrahedra) with
GPU (NVIDIA cuDSS) or multithreaded CPU (Intel MKL) acceleration. No external solver is needed. 3D jobs can come
from the built-in die presets, from a **STEP assembly of three bodies** (bottom die, workpiece, top die), or from a
**STEP file of the two dies only** with a workpiece you define in the app.

## Download

Every [release](https://github.com/aemarconnet/ImpressionFEA/releases) has two Windows x64 builds, each as an
**installer** (`…-setup.exe`: Program Files, Start Menu entry, uninstaller in Apps & Features, optional SOLIDWORKS
add-in) and as a portable **zip** (unzip and run). Both are self-contained (no .NET install), include Intel MKL, STEP
import, the samples and demos:

| Build | Contents | Use it when |
|---|---|---|
| `…-win-x64-full-setup.exe` / `.zip` | + NVIDIA GPU solver (cuDSS, CUDA 12 runtime, cuBLAS) | you have an NVIDIA GPU (driver R525+) |
| `…-win-x64-light-setup.exe` / `.zip` | CPU only, much smaller | no NVIDIA GPU, or a smaller download; the installer's last page (or `Get-GpuSolver.ps1`) adds the GPU libraries later |

The installer is not code-signed yet, so Windows SmartScreen may show "unknown publisher" (*More info ▸ Run anyway*).
It needs administrator rights once (Program Files). Settings, logs and your own materials and demos live in your user
folders (`%APPDATA%`, `%LOCALAPPDATA%`, `Documents\ImpressionFEA`), so uninstalling or upgrading keeps them.

## Assumptions

Every report repeats these at the top:

* **Adiabatic walls**: no heat is lost from the workpiece into the dies (no die chill).
* **No heat generated** by plastic deformation or friction.
* Hence **isothermal**: the workpiece stays at the set forging temperature throughout the stroke.
* **Rigid-viscoplastic**, incompressible, isotropic material (von Mises flow, `σ̄ = C(T)·(ε₀+ε̄)ⁿ·ε̄̇ᵐ`): no
  elasticity, so no springback and no residual stresses.
* **Rigid dies**: no die deflection, wear or temperature change.
* **Constant shear friction** `τ = m·k` over the whole contact.
* **Quasi-static** forming at constant ram speed: inertia and gravity are neglected.
* Material data are fitted to published hot-flow tables (Altan, Ngaile & Shen, *Cold and Hot Forging*, ASM 2005,
  Tables 4.7–4.9) where the grade or a close grade is listed, otherwise representative values; each material states
  its source. Load and fill are engineering estimates for design comparison,
  not certified predictions.

## Build from source

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
src/ImpressionFEA.SolidWorks/       SOLIDWORKS add-in (CommandManager tab, STEP export, launches the app)
tests/ImpressionFEA.Core.Tests/    analytical benchmarks, numerics, engine error paths, preset runs
```

## STEP import (3 bodies, or 2 dies + your workpiece)

**Import STEP…** (configuration panel or bottom bar) reads a STEP file through Open CASCADE (`native/cad`):

1. The file must contain **three solid bodies** (bottom die, workpiece, top die) or **two** (the dies only; see
   *Die-only import* below). Units are converted to mm.
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

### Die-only import

A STEP file with just the **two dies** is accepted too. After you pick the moving die, the Basic tab shows a
**Workpiece** editor: shape (round bar on end, round bar lying flat, rectangular block), diameter / length or block
sizes, rotation about the press axis and plan position X / Y. The preview updates live, the workpiece is always
dropped onto the bottom die, and **Size to cavity +10 %** sets the length for the usual flash allowance. The workpiece
is saved with the setup and kept on re-import. Multi-run can sweep the workpiece position (`WorkpieceOffsetX`) to
study off-centre placement. Demo 7 walks through it with the gear-blank dies (`samples/sample_gear_dies_only.step`).

### Re-import (CAD loop)

**Re-import** re-reads the selected imported STEP file, keeping the same top die and every setting (material,
temperature, mesh, press, advanced). If the file hasn't changed (SHA-256), nothing is rebuilt. While a STEP-based
job is selected, the app watches the file: when you export again from CAD, a yellow banner offers
**Re-import & solve**. Shift+click **Re-import** does the same.

## Workflow: setups, runs, multi-run, demos

* **Basic / Advanced / Demos** tabs. Basic holds geometry, material, mesh size and press. Advanced holds the stroke
  increment, robustness controls (auto time-stepping, cutbacks, local refinement, remesh threshold, node budget),
  nonlinear tolerances and the linear-solver backend, with **Reset to recommended**.
* **File ▾** (execution bar): open / save setups, import or re-import STEP. **Drag and drop** works too: drop a STEP
  assembly to import it, a setup to open it, several setups or a `*.ifea-queue.json` to queue them.
* **Simulation queue** (Telemetry ▸ Queue): **+ Queue** adds every current input as an item; change anything
  (geometry, material, mesh, press, advanced) and add again, then **▶ Run queue**. Double-click a pending item to load
  it into the configuration, edit it and **Update**; duplicate, reorder, re-run or remove items from their menu.
  Multi-run can add its values to the queue instead of running them. **⋯** stops after the current item, skips it,
  adds setup files, saves / opens a queue. Finished items are listed in RUNS like any other run; the configuration is
  locked while the queue runs and put back afterwards. The queue is saved automatically
  (`%AppData%\ImpressionFEA\queue.ifea-queue.json`).
* **Layout**: four panels (configuration, results, telemetry, solver log). Drag a handle to resize; double-click it
  to collapse or expand the panel. **Pop out chart** opens the load / fill chart in its own window. Double-click a
  curve on the chart to load that run at that increment. Fits a 1344 × 756 window (70 % of 1080p).
* **Setups** (`*.ifea.json`, see `RunSettings`): **Save setup… / Open setup…** store every input, including the
  STEP path (relative when it sits beside the setup) and the chosen top die. They are plain JSON, so they are easy
  to version, diff or generate.
* **Run history**: every finished run is listed under RUNS with its own colour. Tick runs to overlay their load and
  fill curves on the telemetry chart. **Save CSV…** writes the selected run's telemetry (one row per increment:
  stroke, gap, load, fill, volume change, max stress / strain, mesh size) plus its setup. **Save all…** writes one
  CSV per run and a `summary.csv`.
* **Multi-run**: runs the current setup once per value of temperature, friction, ram speed, element size, stroke
  increment or material. Every run is added to the history and can optionally be saved to a folder with a
  summary. The original value is restored afterwards, and Abort stops the batch.
* **Demos**: curated, self-describing examples in `samples/demos/*.json`. Each has a setup, step-by-step
  instructions shown in the app, and a suggested multi-run. Instructors can add their own or edited demos without
  rebuilding in `Documents\ImpressionFEA\demos` (a file there replaces the built-in demo of the same name; Demos tab →
  Reload). The eight shipped demos cover an upset benchmark, the temperature window, ram speed / strain
  rate sensitivity, 3D die fill of a 1"-8 hex bolt head, a material comparison including plasticine, the CAD re-import
  loop, die-only import with a user-defined workpiece, and the flash-thickness trade-off (fill against load) on the
  hex bolt head. Demo values are written in mm, °C and mm/s.
* **Compare runs**: click a run in RUNS to bring its full results back into the viewport, slider and read-outs (the
  10 most recent runs keep full results; older ones keep their telemetry). Its curve is drawn bold on the chart.
  **Reset** clears all runs, overlays, results and the log for an unrelated job.
* **Units**: US (in, ksi, tonf, °F; lengths and stresses to 3 decimals) is the default; Metric (mm, MPa, t, °C; 2
  decimals) is one click away at the top of the configuration panel. The solver, setups and CSV files always use mm,
  MPa, t and °C.
* **Esc** aborts whatever is running: a solve, a multi-run, or a video / report export.
* Save and export dialogs remember the last folder used for each kind of file.

## Results and views

* **Fields**: von Mises stress σ̄, effective strain ε̄, effective strain rate, mean (hydrostatic) stress σm, and
  **damage** (normalized Cockcroft–Latham, `D = ∫ max(σ₁, 0)/σ̄ dε̄`). Damage measures the risk of ductile tearing:
  only tension accumulates it, compression does not. Compare it with the critical value for the alloy and
  temperature (typically about 0.3–0.6 for hot steels, from tests); regions approaching it are where cracks start,
  usually at free surfaces that bulge or stretch.
* **Iso-clipping**: drag the top or bottom handle of any colour bar to hide elements outside a value range (the
  exposed faces show the interior); double-click the bar to reset.
* **View ▾** (live): workpiece visibility and opacity, top and bottom die visibility and opacity (defaults: top 15 %,
  bottom opaque), lighting (Shaded by default; Uniform = no shading, colours exactly as the legend), section plane
  (X or Y, offset, flip), element edges, die contact (amber = bottom die, magenta = top die), colour map on/off
  (plain deforming solid), and **flow arrows** (material velocity, with density and size sliders; the part becomes a
  ghost in 3D so interior arrows are visible). **Four views** shows Top, Front and Right (orthographic) with the
  interactive isometric view top-right. Every 3D view has an XYZ triad (X red, Y green, Z blue; Z = press axis).
* **Playback**: first / previous / play-pause / next / last, loop and speed beside the increment slider.
* **Export results ▾** (green, right of the execution bar): PNG image; **WebM video** of the ram descent (view only, view + telemetry, or the whole window;
  optionally with the telemetry CSV) via FFmpeg if installed (`winget install BtbN.FFmpeg.LGPL.Shared.9.0`), else a
  PNG frame sequence; **deformed part** as binary STL or PLY with result colours (full part: symmetric 3D models are
  mirrored, 2D sections revolved or extruded); and a **report**: a folder with a self-contained `report.html`
  (assumptions, setup, key results, chart, every field at die closure, descent stages, telemetry table, run
  comparison), plus `telemetry.csv`, the setup, the solver log, STL / PLY and optionally the video. A **comparison
  report** of several runs adds a comparison page (one column per run, overlaid curves, sweep chart, final shapes)
  before each run's report. Reports print compactly; every figure is also saved as a high-resolution PNG.

### Mesh size (automatic or override)

The element size **h** is automatic by default (grey). In 3D it is chosen so that the initial mesh of the modelled
workpiece has about **8,000 nodes** (Advanced ▸ *Mesh quality*: Fast 4,000 / Balanced 8,000 / Accurate 20,000, or
any *Target nodes*; kept between sessions), within the node budget and
with at least four elements across the thinnest part of the workpiece. It follows every change of the workpiece, so
a resized or moved workpiece stays meshed and visible. Typing a value makes it an **override** (bold), which stays
until you clear the box (Enter on an empty box returns to automatic). The stroke increment **Δs** works the same way
(automatic 0.2·h). Hover over any input for its allowed range; the element-size tooltip also gives the valid range of
h for the current workpiece. 2D uses the preset's recommended h.

Input errors (the run cannot start; the workpiece is not drawn) and warnings appear in a box at the top left of the
view, which says why; click its title to fold it. While a run is solving, the execution bar shows an **ETA**
(remaining time, weighted towards the recent increments because they slow down near die closure); its tooltip gives
the finish time and, in a queue, the time for the remaining items.

### Mesh terminology

* **2D**: 4-node **quadrilaterals** (Q4). The remesher triangulates the outline (Delaunay) and splits every
  triangle into three quadrilaterals, so the mesh mixes quad shapes around shared triangle centres.
* **3D**: 4-node linear **tetrahedra**. On the surface (and on section planes) you see their triangular faces.
  Because the mesher fills space on a body-centred cubic lattice, the surface triangles usually appear in groups of
  four around a shared vertex. That looks like small square pyramids (four corners plus an apex, five vertices),
  but each is four triangular faces of four different tetrahedra.

## Physics

* **Formulation**: rigid-viscoplastic flow formulation (Kobayashi–Oh–Altan). Axisymmetric (gear blank, flanged
  shaft, upset block) or plane-strain (connecting-rod shank section) half-models.
* **Material**: isothermal `σ̄ = C(T)·(ε₀+ε̄)ⁿ·ε̄̇ᵐ` with `C(T) = C_ref·exp(−β(T−T_ref))`. 25 built-in materials in
  families (carbon steels and AISI 4140, stainless steels, D2 tool steel, wrought aluminium including 6061 / 6063,
  coppers and brasses, modelling plasticine), each starting at a nominal forging temperature; add your own fits in `Documents\ImpressionFEA\materials.json`
  (**My materials…** under the material list creates it with an example, **Reload** reads it). Where a grade (or the
  closest listed grade) is in Altan, Ngaile & Shen (ASM 2005) Tables 4.7–4.9, its constants are fitted to those
  tables (β from C against temperature, m at the nominal temperature, n from C against strain, C_ref matching the
  table at ε ≈ 0.5); the others are
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
* **Dies** are height fields z = h(x, y) for most presets, exact closed solids of flat and cylindrical faces for the
  **hex bolt head** preset (quarter model, 1 mm edge radius at the flash land; its flash-land thickness is an input and
  a multi-run parameter), or imported
  triangulated CAD solids (inside test by ray
  parity, closest-point contact on a bucket grid). **Symmetry planes** give half or quarter models; nodal frames from Gram–Schmidt combine
  symmetry and die constraints. Friction is applied as a vector on the tangential axes.
* **Robustness**: contact placement is quality-aware. Nodes of degenerating tets choose between the closest die
  point, an exit along the workpiece normal (wraps sharp CAD die edges) and a small temporary corner penetration
  (0.1·h, relaxed to 0.3·h only while recovering a failing increment). Interior nodes are smoothed for quality.
  A failing increment goes through a recovery ladder: cutbacks (auto time-stepping keeps the smaller step and
  grows it back), then a remesh, then local refinement around the failure by conforming longest-edge bisection
  (LEPP) down to h/2ⁿ. Thin-flash refinement is optional.
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
* Unhandled UI exceptions are written to `%LOCALAPPDATA%\ImpressionFEA\impressionfea-crash.log`.

## Extending with an external solver

Implement `ISimulationEngine` (for example a FreeFem++ or CalculiX wrapper around `System.Diagnostics.Process`).
`Probe()` should check that the binary exists and report a missing dependency. `RunAsync` should generate the
input deck, stream stdout to `ISimulationObserver.OnLog`, and parse the exported tables into `StepSnapshot`s.

## Limitations

Isothermal (no die chill or heat generation), rigid dies, constant shear friction. Imported models run as full
models; automatic symmetry detection is not implemented yet. 3D runs take from tens of seconds to minutes and
are limited by the node budget (12 k by default, adjustable under Advanced). Sharp CAD die edges are handled with
small, bounded corner penetrations; fillets on the dies give cleaner results. When a wide flash gets thinner than
about one element (h) in the last millimetres of the stroke, the 3D solution loses volume (several % in the sample
hex flange); a finer mesh or thin-flash refinement (Advanced) reduces it at the cost of run time. Load and fill are
engineering estimates, not certified predictions.

## Headless capture

`ImpressionFEA.App.exe --capture out.png [--solve] [--die N] [--3d] [--analysis3d --die3d N] [--set Property=Value] [--show-run N] [--import file.step
[--replace-with other.step]] [--demo N [--sweep]] [--tab N]` renders the dashboard to a PNG and exits. It can run
after a full solve, a demo's multi-run, or a re-import of a changed file, which makes it useful as a smoke test.

## SolidWorks add-in

`src/ImpressionFEA.SolidWorks` is a thin SOLIDWORKS add-in (.NET Framework 4.8, x64) that adds an
**ImpressionFEA** tab to the CommandManager for parts and assemblies. The solver never loads into SOLIDWORKS: the
add-in exports the model to STEP and hands it to the ImpressionFEA app, which runs out of process (one app instance;
later jobs are forwarded to it).

| Command | What it does |
|---|---|
| **Simulate** | Exports the active configuration to `<configuration>.step` and opens it in ImpressionFEA. The setup (top die, material, settings) is saved beside it as `<configuration>.ifea.json` and reused next time. |
| **Send Update** | Re-exports after editing; ImpressionFEA re-imports with every setting kept. |
| **All Configurations** | Exports every configuration and solves each one as a run (using the active configuration's setup as the template), for comparison in the Runs list. Results go to `results\`. |
| **Job Folder** | Opens `Documents\ImpressionFEA\SolidWorks\<model>` with the STEP files, setups and results. |

Install: tick **Register the SOLIDWORKS add-in** in the installer (ticked automatically when SOLIDWORKS is found).
The installer writes the add-in's registry keys itself, machine-wide, and removes them on uninstall; close SOLIDWORKS
first. With the portable zip, register from the unpacked folder instead:

```
powershell -ExecutionPolicy Bypass -File SolidWorks\Register-ImpressionFEA-SolidWorks.ps1
```

With the script the COM class is registered per user; SOLIDWORKS only lists add-ins under `HKLM\SOFTWARE\SolidWorks\AddIns`, so the
script asks once for administrator permission to write that key. Restart SOLIDWORKS; Tools > Add-Ins toggles the
add-in. `Unregister-ImpressionFEA-SolidWorks.ps1` removes everything. Load problems are logged to
`%LOCALAPPDATA%\ImpressionFEA\SolidWorks\addin.log`.

Build from source (needs SOLIDWORKS installed for its API interop assemblies, which are embedded, never shipped):

```
dotnet build src/ImpressionFEA.SolidWorks -c Release
powershell -ExecutionPolicy Bypass -File src/ImpressionFEA.SolidWorks/bin/Release/net48/Register-ImpressionFEA-SolidWorks.ps1 -AppPath <path to ImpressionFEA.App.exe>
```

The app can also be driven directly: `ImpressionFEA.App.exe --job <file.step|setup.ifea.json>` or
`--configs <folder> [--template <setup.ifea.json>]`.
