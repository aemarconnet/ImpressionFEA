# ImpressionFEA release history

The release number lives in `Directory.Build.props` (`<Version>`) and is shown in the app title bar together with
the build stamp, e.g. `ImpressionFEA v0.4.0 · build 20261011.0215`.

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
