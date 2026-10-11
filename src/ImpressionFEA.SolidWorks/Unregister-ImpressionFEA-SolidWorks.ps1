# Removes the ImpressionFEA SOLIDWORKS add-in registration written by Register-ImpressionFEA-SolidWorks.ps1 (the
# machine-wide add-in entry through one administrator prompt when not elevated).
#   powershell -ExecutionPolicy Bypass -File Unregister-ImpressionFEA-SolidWorks.ps1
param([switch]$MachineOnly)
$guid = '{9E53A9B9-AFAE-45A0-B90A-FB51F8CE3A65}'
$machine = "HKLM:\SOFTWARE\SolidWorks\AddIns\$guid"
if ($MachineOnly) { if (Test-Path $machine) { Remove-Item $machine -Recurse -Force }; return }
$paths = @(
    "HKCU:\Software\Classes\CLSID\$guid",
    'HKCU:\Software\Classes\ImpressionFEA.SolidWorksAddIn',
    "HKCU:\Software\SolidWorks\AddIns\$guid",
    "HKCU:\Software\SolidWorks\AddInsStartup\$guid",
    'HKCU:\Software\Marconnet Technologies\ImpressionFEA'
)
foreach ($p in $paths) { if (Test-Path $p) { Remove-Item $p -Recurse -Force } }
if (Test-Path $machine) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($admin) { Remove-Item $machine -Recurse -Force }
    else { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-MachineOnly' }
}
'ImpressionFEA add-in unregistered. Restart SOLIDWORKS to remove the tab.'
