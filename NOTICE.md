# Notice

**ImpressionFEA** © 2026 Marconnet Technologies LLC, licensed under
[CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/) (see `LICENSE`).

## Required attribution

When you use, share or adapt ImpressionFEA, credit it like this:

> ImpressionFEA by Marconnet Technologies LLC, licensed under CC BY-NC 4.0
> (https://creativecommons.org/licenses/by-nc/4.0/). Changes were made: *(describe, if any)*.

Commercial use (selling the software or services built on it, or using it in paid work) requires prior written
permission from Marconnet Technologies LLC.

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
