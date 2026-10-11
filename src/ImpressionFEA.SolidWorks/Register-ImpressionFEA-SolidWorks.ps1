# Registers the ImpressionFEA add-in with SOLIDWORKS: the COM class of ImpressionFEA.SolidWorks.dll (per user), the
# location of ImpressionFEA.App.exe, load-at-startup, and the SOLIDWORKS add-in entry. SOLIDWORKS only lists add-ins
# found under HKEY_LOCAL_MACHINE\SOFTWARE\SolidWorks\AddIns, so that one key is written through a single Windows
# administrator (UAC) prompt when this script is not already elevated. Restart SOLIDWORKS afterwards; the
# "ImpressionFEA" tab appears in the CommandManager for parts and assemblies (Tools > Add-Ins toggles it).
#   powershell -ExecutionPolicy Bypass -File Register-ImpressionFEA-SolidWorks.ps1 [-AppPath <ImpressionFEA.App.exe>]
param([string]$AppPath, [switch]$MachineOnly)
$ErrorActionPreference = 'Stop'
$guid = '{9E53A9B9-AFAE-45A0-B90A-FB51F8CE3A65}'
$title = 'ImpressionFEA'
$description = 'Closed-die impression forging simulation (Marconnet Technologies LLC)'

function Set-Key([string]$path, [hashtable]$values) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    foreach ($k in $values.Keys) {
        $v = $values[$k]
        $type = if ($v -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $path -Name $k -Value $v -PropertyType $type -Force | Out-Null
    }
}

# SOLIDWORKS add-in entry (machine-wide). DesktopAddinType/3DExperienceAddinType mark it as a desktop add-in that also
# loads in 3DEXPERIENCE SOLIDWORKS, as other third-party add-ins do.
$addin = @{ '(Default)' = 1; 'Description' = $description; 'Title' = $title; 'DesktopAddinType' = 2; '3DExperienceAddinType' = 2 }
if ($MachineOnly) { Set-Key "HKLM:\SOFTWARE\SolidWorks\AddIns\$guid" $addin; return }

$dll = Join-Path $PSScriptRoot 'ImpressionFEA.SolidWorks.dll'
if (-not (Test-Path $dll)) { throw "ImpressionFEA.SolidWorks.dll not found next to this script." }
$dll = (Resolve-Path $dll).Path
if (-not $AppPath) {
    foreach ($c in (Join-Path $PSScriptRoot 'ImpressionFEA.App.exe'), (Join-Path $PSScriptRoot '..\ImpressionFEA.App.exe')) {
        if (Test-Path $c) { $AppPath = (Resolve-Path $c).Path; break }
    }
}
if (-not $AppPath -or -not (Test-Path $AppPath)) { throw 'ImpressionFEA.App.exe not found; pass -AppPath.' }

$class = 'ImpressionFEA.SolidWorks.AddIn'
$progId = 'ImpressionFEA.SolidWorksAddIn'
$assembly = [Reflection.AssemblyName]::GetAssemblyName($dll)
# Unescaped, as RegAsm writes it: the CLR does not decode %20 in CodeBase (paths with spaces would fail to load).
$codeBase = 'file:///' + ($dll -replace '\\', '/')

# COM class (what RegAsm /codebase would write), per user.
$classes = 'HKCU:\Software\Classes'
$server = @{ '(Default)' = 'mscoree.dll'; 'ThreadingModel' = 'Both'; 'Class' = $class; 'Assembly' = $assembly.FullName; 'RuntimeVersion' = 'v4.0.30319'; 'CodeBase' = $codeBase }
Set-Key "$classes\CLSID\$guid" @{ '(Default)' = $class }
Set-Key "$classes\CLSID\$guid\InprocServer32" $server
Set-Key "$classes\CLSID\$guid\InprocServer32\$($assembly.Version)" @{ 'Class' = $class; 'Assembly' = $assembly.FullName; 'RuntimeVersion' = 'v4.0.30319'; 'CodeBase' = $codeBase }
Set-Key "$classes\CLSID\$guid\ProgId" @{ '(Default)' = $progId }
Set-Key "$classes\CLSID\$guid\Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}" @{}
Set-Key "$classes\$progId" @{ '(Default)' = $class }
Set-Key "$classes\$progId\CLSID" @{ '(Default)' = $guid }

# Load at startup (per user), and where the add-in finds the app.
Set-Key "HKCU:\Software\SolidWorks\AddInsStartup\$guid" @{ '(Default)' = 1 }
Set-Key 'HKCU:\Software\Marconnet Technologies\ImpressionFEA' @{ 'AppPath' = $AppPath; 'AddInPath' = $dll }

# The machine-wide add-in entry: directly when elevated, else through one UAC prompt.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($admin) {
    Set-Key "HKLM:\SOFTWARE\SolidWorks\AddIns\$guid" $addin
} elseif (-not (Test-Path "HKLM:\SOFTWARE\SolidWorks\AddIns\$guid")) {
    'Requesting administrator permission to list the add-in in SOLIDWORKS (one registry key)...'
    $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-MachineOnly'
    if ($p.ExitCode -ne 0 -or -not (Test-Path "HKLM:\SOFTWARE\SolidWorks\AddIns\$guid")) { throw 'The SOLIDWORKS add-in entry was not written (administrator permission is required once).' }
}

"ImpressionFEA add-in registered for $env:USERNAME"
"  add-in : $dll"
"  app    : $AppPath"
'Restart SOLIDWORKS. The ImpressionFEA tab appears in the CommandManager for parts and assemblies (Tools > Add-Ins toggles it).'
