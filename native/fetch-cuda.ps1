# Downloads the NVIDIA redistributables used by the optional GPU solver (cuDSS, CUDA 12 runtime, cuBLAS) from
# developer.download.nvidia.com, verifies their SHA-256 against NVIDIA's manifests and stages the DLLs in native\cuda.
# Without them the app still runs and uses Intel MKL (multithreaded CPU) or the managed solver.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = $PSScriptRoot
$dl = Join-Path $root '_downloads'
New-Item -ItemType Directory -Force $dl, (Join-Path $root 'cuda'), (Join-Path $root 'licenses') | Out-Null

$cudss = Invoke-RestMethod 'https://developer.download.nvidia.com/compute/cudss/redist/redistrib_0.8.0.json'
$cuda = Invoke-RestMethod 'https://developer.download.nvidia.com/compute/cuda/redist/redistrib_12.9.2.json'
$items = @(
    @{ Base = 'https://developer.download.nvidia.com/compute/cudss/redist/'; Entry = $cudss.libcudss.'windows-x86_64'.cuda12 },
    @{ Base = 'https://developer.download.nvidia.com/compute/cuda/redist/'; Entry = $cuda.cuda_cudart.'windows-x86_64' },
    @{ Base = 'https://developer.download.nvidia.com/compute/cuda/redist/'; Entry = $cuda.libcublas.'windows-x86_64' }
)

foreach ($it in $items) {
    $url = $it.Base + $it.Entry.relative_path
    $file = Join-Path $dl ([IO.Path]::GetFileName($url))
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $([IO.Path]::GetFileName($url)) ($([math]::Round([double]$it.Entry.size / 1MB)) MB)..."
        Invoke-WebRequest -UseBasicParsing $url -OutFile $file
    }
    if ((Get-FileHash $file -Algorithm SHA256).Hash.ToLower() -ne $it.Entry.sha256) { throw "SHA-256 mismatch for $file" }
    Expand-Archive $file -DestinationPath (Join-Path $dl 'x') -Force
}

$x = Join-Path $dl 'x'
foreach ($n in 'cudss64_0.dll', 'cudss_mtlayer_vcomp14064_0.dll', 'cudart64_12.dll', 'cublas64_12.dll', 'cublasLt64_12.dll') {
    Copy-Item (Get-ChildItem $x -Recurse -Filter $n | Select-Object -First 1).FullName (Join-Path $root 'cuda') -Force
}
Copy-Item (Get-ChildItem $x -Recurse -Filter LICENSE | Where-Object { $_.FullName -like '*libcudss*' } | Select-Object -First 1).FullName (Join-Path $root 'licenses\cuDSS-LICENSE.txt') -Force
Copy-Item (Get-ChildItem $x -Recurse -Filter LICENSE | Where-Object { $_.FullName -like '*cuda_cudart*' } | Select-Object -First 1).FullName (Join-Path $root 'licenses\CUDA-LICENSE.txt') -Force
Write-Host "GPU solver libraries staged in $(Join-Path $root 'cuda'). Rebuild the solution to copy them next to the app."
