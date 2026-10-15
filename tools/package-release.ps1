# Builds the two Windows release packages of ImpressionFEA (self-contained win-x64, no .NET install needed):
#   artifacts\ImpressionFEA-<version>-win-x64-full.zip   with the NVIDIA GPU solver (cuDSS, CUDA 12 runtime, cuBLAS)
#   artifacts\ImpressionFEA-<version>-win-x64-light.zip  CPU only (Intel MKL), much smaller; GPU libraries can be added
#                                                         later with Get-GpuSolver.ps1
# Both include the Open CASCADE runtime (STEP import), samples, demos and every licence.
# Each package also gets a Windows installer, artifacts\ImpressionFEA-<version>-win-x64-<flavor>-setup.exe (Program
# Files, Start Menu, uninstaller, optional SOLIDWORKS add-in registration), when Inno Setup 6 is installed
# (winget install JRSoftware.InnoSetup); -NoInstaller skips it.
#   powershell -ExecutionPolicy Bypass -File tools/package-release.ps1 [-Version 0.6.0] [-Flavor full|light|both] [-NoInstaller]
# Requires native\cad\bin (native/cad/build-cad.ps1) and, for the full package, native\cuda (native/fetch-cuda.ps1).
param([string]$Version, [ValidateSet('both', 'full', 'light')][string]$Flavor = 'both', [switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version }
if (-not (Test-Path (Join-Path $root 'native\cad\bin\ImpressionFeaCad.dll'))) { throw 'native\cad\bin is missing: run native/cad/build-cad.ps1 first (STEP import).' }
$cudaDlls = 'cudss64_0.dll', 'cudss_mtlayer_vcomp14064_0.dll', 'cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll'
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null

function Build-Package([bool]$gpu) {
    $name = "ImpressionFEA-$Version-win-x64-$(if ($gpu) { 'full' } else { 'light' })"
    $out = Join-Path $artifacts $name
    $zip = Join-Path $artifacts "$name.zip"
    if (Test-Path $out) {
        # Refuse to start while anything in the old package is in use (ImpressionFEA running from it, or SOLIDWORKS with
        # the add-in registered from it): a partial delete would leave a broken package, and a broken CAD loop, behind.
        $locked = @(Get-ChildItem $out -Recurse -File | Where-Object {
                try { [IO.File]::Open($_.FullName, 'Open', 'ReadWrite', 'None').Dispose(); $false } catch { $true }
            } | Select-Object -First 3)
        if ($locked.Count -gt 0) {
            throw "Files in $out are in use; nothing was deleted. Close ImpressionFEA if it runs from this folder, and unload the ImpressionFEA add-in in SOLIDWORKS (Tools > Add-Ins) if it was registered from it: $($locked.FullName -join ', ')"
        }
        Remove-Item -Recurse -Force $out
    }
    if (Test-Path $zip) { Remove-Item -Force $zip }
    if ($gpu) {
        foreach ($f in $cudaDlls) { if (-not (Test-Path (Join-Path $root "native\cuda\$f"))) { throw "native\cuda\$f is missing: run native/fetch-cuda.ps1 first." } }
    }

    dotnet publish src\ImpressionFEA.App\ImpressionFEA.App.csproj -c Release -r win-x64 --self-contained true `
        "-p:IncludeCudaRuntime=$($gpu.ToString().ToLower())" -p:DebugType=none -p:DebugSymbols=false -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }
    foreach ($f in $cudaDlls) {
        $present = Test-Path (Join-Path $out $f)
        if ($gpu -and -not $present) { throw "NVIDIA library $f is missing from the full package." }
        if (-not $gpu -and $present) { throw "NVIDIA library $f ended up in the light package." }
    }
    if (-not (Test-Path (Join-Path $out 'cad\ImpressionFeaCad.dll'))) { throw 'STEP import runtime (cad\) missing from the publish output.' }
    if (-not (Test-Path (Join-Path $out 'samples\demos'))) { throw 'samples\demos missing from the publish output.' }
    # MKL parts PARDISO never loads: TBB / sequential threading layers (the default OpenMP layer is used), the VML
    # vector-math kernels, and debug symbols. CPU-specific kernels (def, mc3, avx2, avx512, avx10) are all kept.
    Get-ChildItem $out -File | Where-Object { $_.Name -in 'mkl_tbb_thread.3.dll', 'mkl_sequential.3.dll' -or $_.Name -like 'mkl_vml_*.dll' -or $_.Extension -eq '.pdb' } | Remove-Item
    # Local, unpublished models must never ship.
    Get-ChildItem (Join-Path $out 'samples') -File | Where-Object { $_.Name -notlike 'sample_*' } | Remove-Item

    # Licences: ImpressionFEA, OCCT + third-party runtimes, NVIDIA, Intel MKL / OpenMP / TBB.
    Copy-Item EULA.txt, LICENSE, NOTICE.md, CHANGELOG.md $out
    $lic = New-Item -ItemType Directory -Force (Join-Path $out 'licenses')
    Copy-Item native\licenses\* $lic -Recurse -Force
    $nuget = (dotnet nuget locals global-packages -l) -replace '^global-packages:\s*', ''
    foreach ($pkg in 'intelmkl.redist.win-x64', 'intelopenmp.redist.win', 'inteltbb.redist.win') {
        $l = Get-ChildItem (Join-Path $nuget $pkg) -Recurse -Filter license.txt | Select-Object -First 1
        if ($l) { Copy-Item $l.FullName (Join-Path $lic "$pkg-license.txt") }
    }

    # SOLIDWORKS add-in (ImpressionFEA CommandManager tab): built when the SOLIDWORKS API is installed on this machine.
    if ($script:addinDir -and (Test-Path $script:addinDir)) {
        $sw = New-Item -ItemType Directory -Force (Join-Path $out 'SolidWorks')
        Copy-Item (Join-Path $script:addinDir 'ImpressionFEA.SolidWorks.dll'), (Join-Path $script:addinDir '*.ps1') $sw
    }

    if (-not $gpu) {
        # Optional GPU solver for the light package: fetch script plus a one-click wrapper.
        $tools = New-Item -ItemType Directory -Force (Join-Path $out 'tools')
        Copy-Item native\fetch-cuda.ps1 $tools
        Set-Content -Encoding UTF8 (Join-Path $out 'Get-GpuSolver.ps1') @'
# Downloads the optional NVIDIA GPU solver libraries (cuDSS 0.8, CUDA 12.9 runtime, cuBLAS; ~880 MB) from
# developer.download.nvidia.com, verifies their SHA-256 and installs them next to ImpressionFEA.App.exe.
# Needs an NVIDIA GPU with a CUDA 12 driver (R525 or newer). Run from this folder:
#   powershell -ExecutionPolicy Bypass -File Get-GpuSolver.ps1
& (Join-Path $PSScriptRoot 'tools\fetch-cuda.ps1') -Destination $PSScriptRoot
'@
    }

    $solvers = if ($gpu) {
        "Solvers:    NVIDIA GPU (cuDSS, needs a CUDA 12 driver R525+) and Intel MKL (multithreaded CPU) are included;`r`n            Linear solver 'Auto' benchmarks both and keeps the faster. Without an NVIDIA GPU the CPU solver is used."
    } else {
        "Solvers:    Intel MKL (multithreaded CPU) is included. For NVIDIA GPU acceleration run`r`n            powershell -ExecutionPolicy Bypass -File Get-GpuSolver.ps1`r`n            (downloads ~880 MB from NVIDIA), then restart the app - or download the 'full' package."
    }

    Set-Content -Encoding UTF8 (Join-Path $out 'README.txt') @"
ImpressionFEA $Version ($(if ($gpu) { 'full: GPU + CPU' } else { 'light: CPU only' })) - closed-die impression forging simulator (Windows x64)
Copyright (c) 2026 Marconnet Technologies LLC. Use is governed by EULA.txt: free for personal and educational use;
commercial use requires a Commercial License. An educational and conceptual tool, not intended for professional
engineering use. Source code under CC BY-NC 4.0 (LICENSE); third-party notices in NOTICE.md.
https://github.com/aemarconnet/ImpressionFEA

Start:      ImpressionFEA.App.exe   (no installation or .NET runtime needed)
Try first:  Demos tab -> pick a demo -> Load demo (or Run its multi-run...)
Your parts: Import STEP... with 3 bodies (bottom die, workpiece, top die) or 2 bodies (dies only; you define the workpiece)
$solvers
$(if ($script:addinDir) { "SOLIDWORKS: powershell -ExecutionPolicy Bypass -File SolidWorks\Register-ImpressionFEA-SolidWorks.ps1`r`n            (one administrator prompt), restart SOLIDWORKS: an 'ImpressionFEA' CommandManager tab appears.`r`n" })Videos:     WebM export uses FFmpeg if installed (e.g. winget install BtbN.FFmpeg.LGPL.Shared.9.0); otherwise PNG frames.
Samples:    samples\*.step, samples\demos\*.json (editable demos)
Licences:   licenses\ (Open CASCADE LGPL-2.1, Intel, NVIDIA, FreeType, FreeImage, TBB, ...)
"@

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    Set-Content -Encoding ASCII "$zip.sha256" "$hash  $name.zip"
    '{0}: {1:N0} MB (unpacked {2:N0} MB), SHA-256 {3}' -f $zip, ((Get-Item $zip).Length / 1MB), ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $hash
    if ($script:iscc) { Build-Installer $out $(if ($gpu) { 'full' } else { 'light' }) }
}

# Installer from the publish folder (tools/installer/ImpressionFEA.iss).
function Build-Installer([string]$source, [string]$flavor) {
    $isccArgs = @("/DAppVersion=$Version", "/DFlavor=$flavor", "/DSourceDir=$source", "/DOutputDir=$artifacts", '/Q')
    $dll = Join-Path $source 'SolidWorks\ImpressionFEA.SolidWorks.dll'
    if (Test-Path $dll) {
        $asm = [Reflection.AssemblyName]::GetAssemblyName($dll)
        $isccArgs += "/DAddInAssembly=$($asm.FullName)", "/DAddInVersion=$($asm.Version)"
    }
    & $script:iscc @isccArgs (Join-Path $root 'tools\installer\ImpressionFEA.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }
    $setup = Join-Path $artifacts "ImpressionFEA-$Version-win-x64-$flavor-setup.exe"
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
    Set-Content -Encoding ASCII "$setup.sha256" "$hash  $(Split-Path $setup -Leaf)"
    '{0}: {1:N0} MB, SHA-256 {2}' -f $setup, ((Get-Item $setup).Length / 1MB), $hash
}

$script:iscc = $null
if (-not $NoInstaller) {
    $script:iscc = @((Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $script:iscc) { 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup): building the zip packages only.' }
}

$script:addinDir = $null
if (Test-Path 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist\SolidWorks.Interop.sldworks.dll') {
    $script:addinDir = Join-Path $artifacts 'solidworks-addin'
    dotnet build src\ImpressionFEA.SolidWorks\ImpressionFEA.SolidWorks.csproj -c Release -p:DebugType=none -o $script:addinDir
    if ($LASTEXITCODE -ne 0) { throw "SOLIDWORKS add-in build failed ($LASTEXITCODE)." }
} else {
    'SOLIDWORKS API not found: packages are built without the SOLIDWORKS add-in.'
}

if ($Flavor -in 'both', 'light') { Build-Package $false }
if ($Flavor -in 'both', 'full') { Build-Package $true }
