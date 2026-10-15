# Notice

**ImpressionFEA** · Copyright © 2026 Marconnet Technologies LLC.

This work is licensed under the Creative Commons Attribution-NonCommercial 4.0 International License
([CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)); the full legal text is in `LICENSE`.
Any use, copy, adaptation or redistribution must credit "Marconnet Technologies LLC" as the original author, link
to the license, and indicate if changes were made. Use of the application is also governed by the End User License
Agreement in `EULA.txt` (free for personal and educational use; commercial use requires a Commercial License;
not intended for professional engineering use).

## Required attribution

**Sharing or adapting the software** (CC BY-NC 4.0): credit it like this:

> ImpressionFEA by Marconnet Technologies LLC, licensed under CC BY-NC 4.0
> (https://creativecommons.org/licenses/by-nc/4.0/). Changes were made: *(describe, if any)*.

**Publishing or presenting results** made under the free licence (personal and educational use), in a paper, thesis,
poster, report, course material, web page or video: credit them like this (EULA Section 2.5):

> Simulated with ImpressionFEA (Marconnet Technologies LLC), www.marconnet.tech/projects/impressionfea

**Commercial Licensees** need no credit for use within their business. Results shared outside it must identify
ImpressionFEA as their source and state that they are preliminary estimates. The app's reports do both in their footer
and statement of assumptions.

Commercial use (in or for a business, in paid work, or selling the software or services built on it) requires a
Commercial License from Marconnet Technologies LLC (`EULA.txt`).

## Third-party components

The CC BY-NC 4.0 license covers the ImpressionFEA source code in this repository only. These components are used
under their own licenses and are **not** relicensed:

| Component | Use | License |
|---|---|---|
| [Open CASCADE Technology 8.0.1](https://dev.opencascade.org/) | STEP import (native shim `native/cad`) | LGPL-2.1 with the OCCT exception (`native/licenses/`) |
| OCCT third-party runtimes (TBB, FreeType, FreeImage, FFmpeg, jemalloc, OpenVR) | loaded by OCCT | their own licenses (`native/licenses/`) |
| [CSparse.NET](https://github.com/wo80/CSparse.NET) | managed sparse Cholesky | LGPL-2.1 (NuGet, unmodified) |
| Intel oneMKL, OpenMP, TBB redistributables | CPU sparse solver (PARDISO) | Intel Simplified Software License (NuGet) |
| NVIDIA cuDSS, CUDA runtime, cuBLAS | GPU sparse solver | NVIDIA license agreements (`native/licenses/`); not committed, fetched by `native/fetch-cuda.ps1` |
| [OxyPlot](https://github.com/oxyplot/oxyplot) | telemetry charts | MIT (NuGet) |

Native binaries (CUDA, OCCT) aren't stored in this repository. They are downloaded or built by the scripts in
`native/`.
