# Native solver libraries

The 3D solver factorises one large sparse SPD system per Newton iteration. Three interchangeable backends are
selected at run time (`Linear solver (3D)` in the UI, `SimulationConfig3D.LinearBackend` in code):

| Backend | Library | Source | Notes |
|---|---|---|---|
| **GPU** | NVIDIA cuDSS 0.8 (CUDA 12) + cudart / cuBLAS 12.9 | `native/cuda/*.dll`, fetched by `fetch-cuda.ps1` from developer.download.nvidia.com | Needs an NVIDIA GPU and a driver supporting CUDA 12 (R525+). ~880 MB. |
| **CPU** | Intel MKL PARDISO 2026.1 | NuGet `intelmkl.redist.win-x64` (+ Intel OpenMP / TBB) | Multithreaded on all cores. ~460 MB unpacked. |
| **Managed** | CSparse.NET 4.4 | NuGet `CSparse` (LGPL-2.1) | Single-threaded; always available. |

**Auto** (default) probes the GPU and MKL at start-up, then benchmarks both on the first real factorisation of each
run. It keeps the CPU unless the GPU is at least 1.5× faster, because MKL results are bit-reproducible and cuDSS results
are not: its parallel reductions vary from run to run, and near die closure that grows to about 1 % in the load. FP64
throughput differs enormously between GPUs: workstation and consumer cards run double
precision at 1/32–1/64 rate, while A100/H100-class cards run it at 1/2. So measuring is the only reliable rule. If a
library is missing or a device is absent, the app falls back silently to the next backend and says so in the log.

The CUDA DLLs are not committed. To (re)create `native/cuda`:

```bash
powershell -ExecutionPolicy Bypass -File native/fetch-cuda.ps1
```

`cudss64_0.dll` also needs the Microsoft Visual C++ 2015–2022 runtime (`msvcp140.dll`), which is present on most
Windows installations.

Licences: NVIDIA cuDSS and CUDA redistributables are under the NVIDIA licences in `native/licenses/`; Intel MKL is
under the Intel Simplified Software License; CSparse.NET is LGPL-2.1, used unmodified as a NuGet package.
