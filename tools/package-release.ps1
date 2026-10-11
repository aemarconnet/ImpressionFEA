# Builds the Windows release package: a self-contained win-x64 publish of ImpressionFEA with Intel MKL (CPU solver),
# the Open CASCADE runtime (STEP import), samples, demos and every licence, zipped as
# artifacts\ImpressionFEA-<version>-win-x64.zip. NVIDIA GPU libraries are not bundled: Get-GpuSolver.ps1 in the package
# downloads them from NVIDIA into the app folder.
#   powershell -ExecutionPolicy Bypass -File tools/package-release.ps1 [-Version 0.5.0]
# Requires native\cad\bin (native/cad/build-cad.ps1) so the package can import STEP files.
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }
if (-not (Test-Path (Join-Path $root 'native\cad\bin\ImpressionFeaCad.dll'))) { throw 'native\cad\bin is missing: run native/cad/build-cad.ps1 first (STEP import).' }

$name = "ImpressionFEA-$Version-win-x64"
$artifacts = Join-Path $root 'artifacts'
$out = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
if (Test-Path $zip) { Remove-Item -Force $zip }

dotnet publish src\ImpressionFEA.App\ImpressionFEA.App.csproj -c Release -r win-x64 --self-contained true `
    -p:IncludeCudaRuntime=false -p:DebugType=none -p:DebugSymbols=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
foreach ($f in 'cudss64_0.dll', 'cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll', 'cudss_mtlayer_vcomp14064_0.dll') {
    if (Test-Path (Join-Path $out $f)) { throw "NVIDIA library $f ended up in the package." }
}
if (-not (Test-Path (Join-Path $out 'cad\ImpressionFeaCad.dll'))) { throw 'STEP import runtime (cad\) missing from the publish output.' }
# MKL parts PARDISO never loads: the TBB / sequential threading layers (the default OpenMP layer is used), the VML
# vector-math kernels, and debug symbols. The CPU-specific kernels (def, mc3, avx2, avx512, avx10) are all kept:
# MKL dispatches between them at run time.
Get-ChildItem $out -File | Where-Object { $_.Name -in 'mkl_tbb_thread.3.dll', 'mkl_sequential.3.dll' -or $_.Name -like 'mkl_vml_*.dll' -or $_.Extension -eq '.pdb' } | Remove-Item
if (-not (Test-Path (Join-Path $out 'samples\demos'))) { throw 'samples\demos missing from the publish output.' }

# Licences: ImpressionFEA, OCCT + its third-party runtimes, NVIDIA (for the optional download), Intel MKL / OpenMP / TBB.
Copy-Item LICENSE, NOTICE.md, CHANGELOG.md $out
$lic = New-Item -ItemType Directory -Force (Join-Path $out 'licenses')
Copy-Item native\licenses\* $lic -Recurse -Force
$nuget = (dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', ''
foreach ($pkg in 'intelmkl.redist.win-x64', 'intelopenmp.redist.win', 'inteltbb.redist.win') {
    $l = Get-ChildItem (Join-Path $nuget $pkg) -Recurse -Filter license.txt | Select-Object -First 1
    if ($l) { Copy-Item $l.FullName (Join-Path $lic "$pkg-license.txt") }
}

# Optional GPU solver: the fetch script plus a one-click wrapper that installs into the app folder.
$tools = New-Item -ItemType Directory -Force (Join-Path $out 'tools')
Copy-Item native\fetch-cuda.ps1 $tools
Set-Content -Encoding UTF8 (Join-Path $out 'Get-GpuSolver.ps1') @'
# Downloads the optional NVIDIA GPU solver libraries (cuDSS 0.8, CUDA 12.9 runtime, cuBLAS; ~880 MB) from
# developer.download.nvidia.com, verifies their SHA-256 and installs them next to ImpressionFEA.App.exe.
# Needs an NVIDIA GPU with a CUDA 12 driver (R525 or newer). Run from this folder:
#   powershell -ExecutionPolicy Bypass -File Get-GpuSolver.ps1
& (Join-Path $PSScriptRoot 'tools\fetch-cuda.ps1') -Destination $PSScriptRoot
'@

Set-Content -Encoding UTF8 (Join-Path $out 'README.txt') @"
ImpressionFEA $Version - closed-die impression forging simulator (Windows x64)
Copyright (c) 2026 Marconnet Technologies LLC. Licensed under CC BY-NC 4.0 (see LICENSE and NOTICE.md).
https://github.com/aemarconnet/ImpressionFEA

Start:      ImpressionFEA.App.exe   (no installation or .NET runtime needed)
Try first:  Demos tab -> pick a demo -> Load demo (or Run its multi-run...)
Your parts: Import STEP... with three bodies: bottom die, workpiece, top die (see README on GitHub)

Solvers:    Intel MKL (multithreaded CPU) is included. For NVIDIA GPU acceleration run
            powershell -ExecutionPolicy Bypass -File Get-GpuSolver.ps1
            (downloads ~880 MB from NVIDIA), then restart the app.
Samples:    samples\*.step (3-body assemblies), samples\demos\*.json (editable demos)
Licences:   licenses\ (Open CASCADE LGPL-2.1, Intel, NVIDIA, FreeType, FreeImage, TBB, ...)
"@

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content -Encoding ASCII "$zip.sha256" "$hash  $name.zip"
'{0}: {1:N0} MB (unpacked {2:N0} MB), SHA-256 {3}' -f $zip, ((Get-Item $zip).Length / 1MB), ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $hash
