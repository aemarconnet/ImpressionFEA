# Future work

Candidate features for ImpressionFEA, ranked by value for a **fast, isothermal design tool**. Difficulty is a rough
estimate of the work it takes in this code base.

| Difficulty | Meaning |
|---|---|
| Low | Post-processing of data the solver already produces; a few days. |
| Medium | New result fields, solver hooks or UI workflows; one to three weeks. |
| High | New physics, data hand-off between analyses or major refactoring; a month or more. |

## Defects and die fill

### Thin-flash resolution in 3D — Medium
Very thin flash (under about 1 mm at 2.5–3 mm elements) loses a few per cent of volume in 3D remeshing, which
understates the load difference between flash thicknesses (demo 8: 0.8 mm vs 3.2 mm flash gives +8 % peak load,
6.7 % volume lost on the thin side). Anisotropic or boundary-layer refinement in the flash gap, plus a volume
correction on remesh, would make flash studies quantitative.

### Fold / lap detection — Medium
Flag where free surface folds back onto itself or two free surfaces meet: boundary nodes going from free surface into
self-contact, and sharp reversals of the outline between increments. Show the fold as a marker on the part and in the
report, with the increment where it formed. This is the defect forgers worry about most after underfill. The grain-flow
lines already make laps visible; this would detect them automatically.

### Underfill map and die-contact history — Low–Medium
Fill is reported as one percentage. A map of the cavity regions that never touched the die, and of *when* each region
filled, shows the die designer where to add stock or change the preform. It could be drawn on the die surface in the
3D view and in reports.

### Flash and material-yield reporting — Low
Flash volume, flash-land thickness and material utilisation (part volume ÷ billet volume) per run, in the read-outs,
the comparison table and the reports. Often the deciding number when quoting.

## Process design

### Billet volume optimiser — Low–Medium
Find the smallest billet that just fills the cavity: bisection on billet volume (or length for a given diameter) with
coarse meshes, refined at the end. Builds on the simulation queue.

### Multi-operation sequences (preform → blocker → finisher, with trimming) — High
Most production forgings take two or three hits. Carry the deformed mesh with its strain and damage history into the
next die set, with a simple trim that removes material outside the parting line between operations. Mesh hand-off is
moderate work; robust trimming in 3D is the hard part.

### Press models — Medium
Today the ram speed is constant. Add:
- mechanical (crank) press: ram speed follows the crank kinematics;
- screw press and hammer: the blow ends when the energy runs out (stroke-end search).

This gives a better strain-rate history and answers "is my press big enough?", while staying isothermal.

## Dies

### Die pressure map and die stress estimate — Low (pressure map) / Medium–High (elastic die analysis)
Map the contact pressure of the last increment (or its maximum over the stroke) onto the die surface. Optionally run a
one-shot linear-elastic analysis of the die with those pressures. Even the pressure map flags die cracking and wear
hot spots.

### Die wear index (Archard) — Low–Medium
Wear ≈ k · contact pressure · sliding distance, summed over the stroke at the die surface. All inputs are already at
the contact nodes; a cheap and useful die-life ranking between design variants.

## Results and post-processing

### Point tracking and history plots — Low–Medium
Pick a material point; plot its strain, strain rate, stress and damage against stroke. The grain-flow tracer already
carries material points through the mesh and across remeshes.

### Approximate temperature (keeping the fast isothermal solve) — Low / Medium
- *Adiabatic temperature rise estimate* (Low): a field computed from the work of deformation, without feeding it back
  into the flow stress. Shows where heating is significant.
- *Die-chill correction* (Medium): a contact-time-based cooling layer near the die surfaces.

Both flag where the isothermal assumption is weakest, for very little run time.

### Microstructure: recrystallised grain size — Medium
Post-process strain, strain rate and temperature with recrystallisation (JMAK-type) models to predict grain size.
Valuable for titanium and nickel alloys; only as good as the material data behind it.

## Materials

### Flow-curve fitting from test data — Low–Medium
Import measured flow curves (CSV: strain, stress at several strain rates and temperatures) and fit C_ref, n, m and β
directly into the user materials file. The user materials file (Documents\ImpressionFEA\materials.json) already
accepts hand-fitted coefficients.

### Larger certified material sets — Low (code) / Medium (data)
More alloys, with sources and validity ranges shown in the material list and the reports. Families held back from
the first library: titanium (Ti-6Al-4V α-β and β forging, CP grades), nickel superalloys (718, Waspaloy), magnesium
(AZ31B, AZ80A), alloy and carburising steels (4340, 8620), hot-work tool steels (H13), and cold-forging grades
(strain-hardening dominated, room temperature).

## Simulation queue — next phase

The queue runs complete setups one after the other with the configuration panel locked. Next steps:

- **Edit while running** (Medium): build the solver input from the setup record instead of from the configuration
  panel, so items can be added and edited while the queue runs. This also enables a headless batch runner (command
  line, CAD add-in).
- **Results on disk** (Medium): save each run's increments to a results file so any run can be reloaded and
  reported later. Today only the ten most recent runs keep their full results in memory, which limits long queues.
- **Geometry cache** (Low): keep imported STEP geometry by file and hash so queue items sharing a STEP file never
  re-import it (already reused within a session when the path matches).
- **Time estimates** (Low): estimated time per item from its expected increments and recent time per increment.
- **Automatic outputs** (Low–Medium): optional per-item report or telemetry folder, and a comparison report when the
  queue finishes.

## Out of scope (they conflict with fast results with moderate assumptions)

- Fully coupled thermo-mechanical analysis with heat transfer into the dies: roughly doubles the solve time and
  needs reliable heat-transfer coefficients. The approximate temperature options above give most of the insight.
- Elasto-plastic analysis (springback, residual stress), heat treatment and phase transformation: different physics
  and a different solver.
- Specialised processes (ring rolling, cogging, extrusion, machining) and user subroutines.
- Deformable dies coupled into the forging analysis: the one-way die check above is enough for design comparison.
- Fracture by element deletion: the Cockcroft–Latham damage field already ranks tearing risk without the numerical
  instability.
