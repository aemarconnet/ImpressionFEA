# Builds ImpressionFeaCad.dll (C API over Open CASCADE) with the installed Visual Studio C++ toolset and stages it, plus
# exactly the OCCT DLLs it needs at run time (transitive import closure), in native\cad\bin.
# Requires, from the OCCT 8.0.1 GitHub release (github.com/Open-Cascade-SAS/OCCT/releases):
#   opencascade-release-no-pch.zip  extracted to native\_downloads\occt\  (gives native\_downloads\occt\opencascade-*)
#   3rdparty-vc14-64.zip            its inner archive saved as native\_downloads\3rdparty-inner.zip
# and Visual Studio 2022 with the C++ x64 tools.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$occt = Get-ChildItem (Join-Path $here '..\_downloads\occt') -Directory -Filter 'opencascade-*' | Select-Object -First 1
if (-not $occt) { throw 'OCCT package not found in native\_downloads\occt.' }
$inc = Join-Path $occt.FullName 'inc'
$lib = Join-Path $occt.FullName 'win64\vc14\lib'
$bin = Join-Path $occt.FullName 'win64\vc14\bin'
$out = Join-Path $here 'bin'
$obj = Join-Path $here 'obj'
New-Item -ItemType Directory -Force $out, $obj | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$env:PATH = "$(Split-Path $vswhere);$env:PATH" # vcvars64.bat calls vswhere.exe
$libs = (Get-ChildItem $lib -Filter 'TK*.lib' | ForEach-Object Name) -join ' '
$cmd = "`"$vcvars`" >nul && cl /nologo /LD /EHsc /O2 /MD /std:c++17 /W3 /DNDEBUG /I`"$inc`" /Fo`"$obj\\`" /Fe`"$out\ImpressionFeaCad.dll`" `"$here\ImpressionFeaCad.cpp`" /link /NOLOGO /LIBPATH:`"$lib`" $libs"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "C++ build failed ($LASTEXITCODE)." }

# Copy the transitive closure of OCCT DLLs imported by the shim.
function Get-Imports([string]$path) {
    $s = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path))
    [regex]::Matches($s, 'TK[A-Za-z0-9]+\.dll') | ForEach-Object { $_.Value } | Sort-Object -Unique
}
$queue = [Collections.Generic.Queue[string]]::new()
$seen = @{}
Get-Imports (Join-Path $out 'ImpressionFeaCad.dll') | ForEach-Object { $queue.Enqueue($_) }
while ($queue.Count -gt 0) {
    $d = $queue.Dequeue()
    if ($seen.ContainsKey($d)) { continue }
    $src = Join-Path $bin $d
    if (-not (Test-Path $src)) { continue }
    $seen[$d] = $true
    Copy-Item $src $out -Force
    Get-Imports $src | ForEach-Object { if (-not $seen.ContainsKey($_)) { $queue.Enqueue($_) } }
}
# Third-party runtimes imported by TKernel (jemalloc, TBB) and TKService (FreeType, FFmpeg, OpenVR), taken from
# OCCT's 3rdparty-vc14-64 package (inner archive extracted to native\_downloads\3rdparty-inner.zip).
$third = Join-Path $here '..\_downloads\3rdparty-inner.zip'
if (Test-Path -LiteralPath $third) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($third)
    $want = 'jemalloc-vc14-64/bin/jemalloc.dll', 'tbb-2021.13.0-x64/bin/tbb12.dll', 'freetype-2.13.3-x64/bin/freetype.dll',
        'ffmpeg-3.3.4-64/bin/avcodec-57.dll', 'ffmpeg-3.3.4-64/bin/avformat-57.dll', 'ffmpeg-3.3.4-64/bin/avutil-55.dll',
        'ffmpeg-3.3.4-64/bin/swscale-4.dll', 'openvr-1.14.15-64/bin/win64/openvr_api.dll', 'freeimage-3.18.0-x64/bin/FreeImage.dll',
        'freeimage-3.18.0-x64/license-fi.txt'
    foreach ($w in $want) {
        $e = $zip.Entries | Where-Object { $_.FullName -eq "3rdparty-vc14-64/$w" }
        if ($e) { [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $out $e.Name), $true) }
    }
    $zip.Dispose()
}

if ($args -contains '-Samples') {
    $gen = Join-Path $obj 'GenSamples.exe'
    cmd /c "`"$vcvars`" >nul && cl /nologo /EHsc /O2 /MD /std:c++17 /W3 /DNDEBUG /I`"$inc`" /Fo`"$obj\\`" /Fe`"$gen`" `"$here\GenSamples.cpp`" /link /NOLOGO /LIBPATH:`"$lib`" $libs"
    if ($LASTEXITCODE -ne 0) { throw "Sample generator build failed ($LASTEXITCODE)." }
    $samples = Join-Path $here '..\..\samples'
    New-Item -ItemType Directory -Force $samples | Out-Null
    $env:PATH = "$out;$env:PATH"
    Push-Location $samples
    try { & $gen; if ($LASTEXITCODE -ne 0) { throw "Sample generator failed ($LASTEXITCODE)." } } finally { Pop-Location }
}

Get-ChildItem -LiteralPath $out -File | Where-Object { $_.Extension -in '.lib', '.exp' } | Remove-Item
if (Test-Path -LiteralPath (Join-Path $out 'license-fi.txt')) { Move-Item -LiteralPath (Join-Path $out 'license-fi.txt') (Join-Path $here '..\licenses\FreeImage-license-fi.txt') -Force }
Copy-Item (Join-Path $occt.FullName 'LICENSE_LGPL_21.txt') (Join-Path $here '..\licenses\OCCT-LICENSE_LGPL_21.txt') -Force
Copy-Item (Join-Path $occt.FullName 'OCCT_LGPL_EXCEPTION.txt') (Join-Path $here '..\licenses\OCCT_LGPL_EXCEPTION.txt') -Force
"ImpressionFeaCad.dll + $($seen.Count) OCCT DLLs staged in $out"
