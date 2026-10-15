# ImpressionFEA release history

The release number lives in `Directory.Build.props` (`<Version>`) and is shown in the app title bar together with
the build stamp, e.g. `ImpressionFEA v0.4.0 · build 20261011.0215`.

## 0.9.0
- **Windows installer** (`…-win-x64-full-setup.exe` / `…-light-setup.exe`, next to the zip packages): installs to
  Program Files with a Start Menu entry (desktop shortcut optional) and an uninstaller. The optional **SOLIDWORKS
  add-in** (ticked when SOLIDWORKS is found) is registered by the installer itself, machine-wide, without PowerShell;
  uninstalling removes it. The light installer can download the NVIDIA GPU solver on its last page.
- **Automatic element size**: in 3D, h is chosen for about **8,000 nodes** in the initial mesh (Advanced ▸ *Target
  nodes*, remembered between sessions), calibrated on the actual mesh, within the node budget and the four-elements-
  across limit. It follows every change of the workpiece, so a resized die-only workpiece no longer disappears for
  having too few or too many elements. Automatic values are grey; typing a value makes it a bold **override** that
  stays; clearing the box returns to automatic. The stroke increment follows h the same way (0.2·h). Setups record
  whether h and Δs were automatic; older setups and the demos keep their stored values as overrides.
- **Input tooltips** show every field's allowed range in the display units; the element-size tooltip also gives the
  valid h range for the current workpiece and the h for the target node count. The mesh read-out shows the actual
  node and tetrahedron count of the preview mesh.
- **Errors and warnings** moved from the bottom of the configuration panel to a box at the top left of the view:
  errors (red: the run cannot start and the workpiece is not drawn, with the reason) and warnings (amber), click to
  fold. A new message lists both limits when no element size fits the workpiece.
- **ETA** in the execution bar while solving, weighted towards the recent increments (they slow down near die
  closure); the tooltip gives the finish time and, while a queue runs, the time for the remaining items.
- **3D contact accuracy**: contact tolerances now scale with the local element size instead of the global h, so
  refined elements no longer sit inside the dies. On the hex bolt head the deepest node penetration at closure fell
  from 0.45 mm (41 nodes) to under 0.2 mm, and runs got slightly faster. Only the last recovery passes of an increment
  that would otherwise fail fall back to the old, looser tolerances.
- **3D solver about 2.4× faster**: the element work of each Newton iteration runs on all CPU cores (results summed in a
  fixed order, so they do not depend on the core count), and the preconditioned iterative solve backs off after
  repeated failures instead of costing nearly a factorisation each time. The Balanced hex bolt head went from about
  6.4 to 2.7 minutes. Every 3D log ends with a time profile (assembly, factorisation, iterative solve, contact,
  remeshing).
- **Stroke increment follows the flash**: each increment compresses the thinnest layer held between the dies by at
  most 10 %, so the step shrinks ahead of the flash instead of failing and being cut back.
- **Hex bolt head preset is a quarter model** (it is symmetric about x and y) with a 1 mm radius on the die edge into
  the flash land. At the same run time the mesh is much finer: the workpiece volume is now conserved within about 1 %
  (the full model lost 6–7 % into the thin flash, which understated the load by about a third to a half). Demos 4 and
  8 use the automatic element size.
- **Mesh quality** (Advanced ▸ Mesh quality 3D): Fast (about 4,000 nodes), Balanced (8,000, default) or Accurate
  (20,000, node budget 40,000) for your own parts when run time matters less; remembered between sessions. A 3D run
  that changes the workpiece volume by more than 3 % ends with a coarse-mesh warning.
- **Volume conserved on remeshing**: the free surface (in the end the flash rim) may now move up to one element per
  remesh to restore the lost volume, instead of a quarter: the coarse hex bolt head now ends within about 1 % instead of
  −6 %. Volume was not what made coarse meshes low on load, though:
- **Flash-resolution check**: when the dies close with less than about one element through the thinnest flash, the run
  ends with an accuracy note (the flash pressure, and so the load, is likely understated) and a suggestion to use the
  Accurate mesh quality or a smaller element size.
- **Handbook load estimate** (Core): the simplified slab method of Altan, Ngaile & Shen (Eqs 14.8–14.11, with the
  sliding and disk-shearing cavity modes of Table 15.1), with benchmark tests against the book's worked examples.
- Crash log moved to `%LOCALAPPDATA%\ImpressionFEA`; your own and edited demos go in `Documents\ImpressionFEA\demos`
  (a file there replaces the built-in demo of the same name) — the install folder can be read-only.

## 0.8.2
- **Simulation queue** (Telemetry ▸ Queue): add the current setup (**+ Queue** or **+ Add current setup**), change any
  input and add again, then **▶ Run queue**. Items can be loaded back into the configuration, edited and updated,
  duplicated, reordered, re-run or removed; Multi-run can add its values to the queue instead of running now; setup
  files can be added (or dropped on the window). Stop after the current item, skip the current item; Abort / Esc
  stops the queue. Finished items appear in RUNS. The queue is saved automatically and can be saved / opened as
  `*.ifea-queue.json`.
- **Drag and drop**: drop a STEP assembly to import it, a setup to open it, several setups or a queue file to queue
  them.
- **Material library**: 25 materials in families (carbon steels and AISI 4140, stainless steels, D2 tool steel,
  wrought aluminium incl. 6061 / 6063, coppers and brasses, modelling plasticine), grouped in the material list.
  Where the grade or a close grade is listed in Altan, Ngaile & Shen, *Cold and Hot Forging* (ASM 2005), Tables
  4.7–4.9, the constants are fitted to those tables; the rest are representative. Every material states its data
  source (material tooltip, and a "Material data" row in reports). Changes against 0.8.1: AISI 4140 flow stress
  about −5 % at 1150 °C; 6061-T6 and C11000 unchanged.
  Choosing a material sets the temperature to its nominal forging temperature (the field now updates too).
  Your own materials in Documents\ImpressionFEA\materials.json (**My materials…**, **Reload**; optional `source`).
- **Layout**: fits a 1344 × 756 window (70 % of 1080p). Units moved to the configuration panel; Open / Save setup and
  STEP import are under **File ▾**; **Export results** is a green button at the right of the execution bar; Runs and
  Queue are tabs under the chart; read-outs in two columns. Double-click a panel's drag handle to collapse or expand
  it (configuration, telemetry, solver log, run lists).
- **Pop-out chart**: the load / fill chart in its own resizable window; close it to dock it back.
- **Telemetry chart**: x axis is the die position from closure (0 = dies closed, on the right), so overlaid runs line
  up at closure. Double-click a curve to load that run at that increment and select it in RUNS.
- **Comparison report** (Export ▸, or Compare… in RUNS): a comparison page (one column per run, the inputs that
  differ, best values highlighted, overlaid curves, a sweep chart when one input varies, final shapes side by side)
  followed by each run's report, each in its own folder with a standalone report. Reports print compactly (about two
  pages per run) and save every figure as a high-resolution PNG.
- 2D results in the 3D view: workpiece and die visibility / opacity work (the top die is translucent by default, as
  for 3D results, so the part is no longer hidden under an opaque die); the cut-away follows the section axis and
  Flip. The top die is drawn as its impression surface (no grey block over the part) and the lighting defaults to
  Soft for 2D results (each kind keeps its own lighting). Options that only apply to 3D meshes (element edges, die
  contact, section offset) are greyed out with a note.
- **Hex bolt head 1" (3D)** preset: 1"-8 hex bolt (1-1/2" across flats × 39/64" head, Ø1" × 1-1/2" shank in a
  bottom-die bore, 3/16" flash land), full model, dies built as exact flat and cylindrical faces. Its **flash-land
  thickness** is an input (Geometry), saved in setups, and a Multi-run parameter. Demo 4 now uses it (replaces the
  STEP hex flange); new **demo 8** sweeps the flash thickness (fill against load).
- Grain flow never shows the element mesh (2D and 3D).

## 0.8.1
- **Nodal / Element** colouring toggle next to the results list (as in SOLIDWORKS Simulation): Nodal averages the
  element values to the nodes (smooth contours); Element paints every element in one flat colour from its own value,
  showing the jumps between elements. 2D section, 3D results and 2D results in the 3D view; applies to images and
  videos.

## 0.8.0
- **Grain flow** result (results list, next to stress and strain), like an etched macro-section: fibre following the
  contour, laps (lines folding over) and fibre running out through the flash. Fibre direction (Auto / vertical /
  horizontal) and 4–60 lines under View ▾. Computed from the stored results, live during a run and in playback,
  videos and reports.
  - 2D: material lines seeded along the billet fibre and carried through every increment with the metal flow; also
    drawn on the cut faces of the revolved (cut-away) or extruded part in the 3D view.
  - 3D: the billet position of the material is carried through the run (across remeshes); section the part
    (Cut-away) and the fibre lines are drawn on an exactly flat cut face. A cut across the fibre shows end grain.
    Selecting Grain flow switches the section on.
- Input limits: every numeric input has an allowed range (in solver units, converted to the display units). A value
  outside it is set to the nearest limit and flagged in red with the allowed range. Temperature is limited to the
  selected material's calibrated window.
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
