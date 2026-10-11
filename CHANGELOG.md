# ImpressionFEA release history

The release number lives in `Directory.Build.props` (`<Version>`) and is shown in the app title bar together with
the build stamp, e.g. `ImpressionFEA v0.4.0 · build 20261011.0215`.

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
