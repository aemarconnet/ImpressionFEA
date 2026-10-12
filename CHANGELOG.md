# ImpressionFEA release history

The release number lives in `Directory.Build.props` (`<Version>`) and is shown in the app title bar together with
the build stamp, e.g. `ImpressionFEA v0.4.0 · build 20261011.0215`.

## Unreleased
- **Grain flow lines** (2D section, View ▾ → Grain flow): material lines seeded along the fibre of the bar stock
  (vertical or horizontal, 4–60 lines) and carried through every increment with the metal flow, like an etched
  macro-section. Shows fibre following the contour, laps (lines folding over) and fibre running out through the flash.
  Computed from the stored results, live during a run and during playback / video export.
- **Die edges** (3D view, View ▾ → Display): thin dark lines on the sharp edges of the die geometry (and the cut-away
  faces), so the dies read even when the shading is flat or translucent.
- Iso-clip handles move in steps of 1/120 of the range (Shift: whole colour bands) and show their value.
- **Export ▾** moved to the bottom-right corner of the window.
- 2D results store the nodal velocity that moved the material in increments that remesh (it was saved as zero).

## 0.7.1
- 2D folds at die fillets and in thin flash: when every cutback folds an element (the contact projection lays all
  nodes of a boundary element on the die), the update is accepted and the workpiece is remeshed on the new outline
  instead of failing. Remesh volume compensation no longer pushes nodes across the axis (negative radius,
  "stiffness matrix not positive definite"). The flanged shaft in 6061 now forges with 1.0, 1.25 and 1.5 mm meshes.

## 0.7.0
- **SOLIDWORKS add-in**: an **ImpressionFEA** CommandManager tab for parts and assemblies with **Simulate**
  (export the active configuration to STEP and open it in ImpressionFEA), **Send Update** (re-export; settings
  kept), **All Configurations** (export and solve every configuration as a run) and **Job Folder**. The solver
  runs out of process in the app; setups are saved per configuration beside the exported STEP files. Release
  packages include it in `SolidWorks\` with register / unregister scripts.
- 3D remeshing near closure: flat or inverted surface tets in pinched thin sections are untangled (surface nodes
  moved off the die to better positions), and a new mesh is accepted whenever it is clearly better than the one in
  use. Die-only jobs that stalled at ~92 % stroke with "tetrahedron collapsed" now run to closure.
- Demo 3 (ram speed) fixed: the 2D solver now retries a stalled Newton solve with Picard fallback (it diverged at
  step 1 for 6061 in the flanged shaft), and the demo uses a 1.25 mm mesh; every speed forges to 100 % fill.
- RUNS: right-click a run → **Delete run** (or select it and press Del) removes its telemetry, chart overlay and
  results; the view moves to the next stored run, or back to the setup preview.
- App: single instance; `--job <step|setup>` and `--configs <folder> [--template <setup>]` command lines, forwarded
  to a running instance.

## 0.6.1
- Four-view layout behaves like an engineering drawing: Top, Front and Right share one scale and one centre (Top
  aligned with Front, Front with Right) and are framed on the whole job, so the bottom die stays fixed at the bottom
  of Front and Right while the top die and workpiece move.

## 0.6.0
- Results: **damage** field (normalized Cockcroft–Latham, ductile tearing risk) in 2D and 3D; nodal **flow
  velocity** stored per increment with 2D and 3D arrow plots.
- Views: **View ▾** panel (workpiece / top die / bottom die visibility and opacity, lighting, section plane with
  offset and flip, edges, die-contact highlight, colour map on/off, flow-arrow density and size; all live);
  **four-view layout** (Top, Front, Right + isometric); XYZ triad in every 3D view; **iso-clipping** by dragging
  the colour bar; **playback** controls (play / pause / step / loop / speed); F fits the visible bodies.
  New 3D defaults: Shaded lighting, no section, top die 15 % opaque, bottom die opaque.
- Exports: **WebM video** of the ram descent (FFmpeg) with optional telemetry, **report generator** (self-contained
  HTML with assumptions, setup, results, figures, telemetry, run comparison + CSV, setup, log, STL / PLY, video),
  deformed part as **STL / PLY**.
- **Die-only STEP import** (2 bodies) with a user-defined workpiece (shape, size, rotation, position); demo 7.
- **Compare runs**: click a run to show its full results again; **Reset** clears runs and telemetry.
- **US / Metric units** (US default: in, ksi, tonf, °F to 3 decimals; metric: mm, MPa, t, °C to 2 decimals).
- **Esc** aborts solves, multi-runs and exports; dialogs remember their last folders; resizable panels with drag
  handles (the button bar keeps its height; the handle above it resizes the log).
- Hex-flange demo fixed: close-fit shank pocket and +10 % bar, fills to 99.9 %.
- Releases ship as two builds: **full** (with NVIDIA GPU libraries) and **light** (CPU only).

## 0.5.0
- 3D robustness: quality-aware contact placement at sharp CAD die edges, interior-node smoothing, auto
  time-stepping (keep the reduced increment, grow back), recovery ladder with local refinement (LEPP bisection);
  sample assemblies that used to stop with "tetrahedron collapsed" now forge to die closure.
- Configuration panel split into Basic / Advanced / Demos tabs; advanced robustness and solver settings.
- Run history with chart overlays, telemetry CSV export (per run and all runs + summary).
- Multi-run parameter sweeps (temperature, friction, ram speed, element size, stroke increment, material).
- Setups (*.ifea.json): save / open every input, including STEP file and top die.
- Re-import: detects changes to the STEP file (watcher + SHA-256), keeps every setting, optional re-solve.
- Six curated demos with step-by-step instructions (samples/demos/*.json, editable without rebuilding).

## 0.4.0
- STEP import of a 3-body assembly (bottom die, workpiece, top die) via Open CASCADE; orientation from the bodies'
  centres of gravity, user picks the moving top die; runs until the top die touches the bottom die.
- SolidWorks-style 3D viewport controls (middle-mouse rotate, Ctrl pan, Shift zoom, Alt roll, F fit,
  Ctrl+1–7 standard views, arrow keys).
- Version number in the title bar.

## 0.3.0
- Sparse direct solver backends: NVIDIA cuDSS (GPU), Intel MKL PARDISO (multithreaded CPU), managed fallback;
  auto-selection by benchmark.

## 0.2.0
- 3D viewer of 2D results (revolved / extruded) and true 3D tetrahedral solver with 3D die presets.

## 0.1.0
- 2D axisymmetric / plane-strain closed-die forging simulator (rigid-viscoplastic FEM), material and die presets,
  telemetry charts, live solver log.
