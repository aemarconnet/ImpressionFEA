; Windows installer for ImpressionFEA (Inno Setup 6). Built by tools/package-release.ps1 -Installer, which passes:
;   /DAppVersion=0.9.0 /DFlavor=full|light /DSourceDir=<publish folder> /DOutputDir=<artifacts>
;   /DAddInAssembly="ImpressionFEA.SolidWorks, Version=..., Culture=neutral, PublicKeyToken=null" /DAddInVersion=0.9.0.0
;     (only when the package contains the SOLIDWORKS add-in)
; Installs per machine into Program Files with a Start Menu entry and an uninstaller. The optional SOLIDWORKS add-in
; is registered by writing its registry keys directly (machine-wide COM class + SOLIDWORKS add-in entry), so no
; PowerShell, execution policy or second administrator prompt is involved; uninstalling removes them again.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z (tools/package-release.ps1 -Installer does this).
#endif
#ifndef Flavor
  #define Flavor "full"
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<publish folder>.
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "ImpressionFEA"
#define AppExe "ImpressionFEA.App.exe"
#define Publisher "Marconnet Technologies LLC"
; Braces doubled: Inno reads {...} in these strings as a constant.
#define AddInClsid "{{9E53A9B9-AFAE-45A0-B90A-FB51F8CE3A65}"
#define AddInClass "ImpressionFEA.SolidWorks.AddIn"
#define AddInProgId "ImpressionFEA.SolidWorksAddIn"
#ifdef AddInAssembly
  #define HasAddIn
#endif

[Setup]
AppId={{6C1C7E5A-3F0B-4D7E-9B61-2A8E4F1D9C37}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Flavor})
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/aemarconnet/ImpressionFEA
AppSupportURL=https://github.com/aemarconnet/ImpressionFEA/issues
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-{#Flavor}-setup
SetupIconFile=..\..\assets\ImpressionFEA.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
#ifdef HasAddIn
Name: swaddin; Description: "Register the &SOLIDWORKS add-in (ImpressionFEA tab in the CommandManager)"; GroupDescription: "SOLIDWORKS:"; Flags: unchecked
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Closed-die impression forging simulation"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

#ifdef HasAddIn
[Registry]
; COM class of the add-in, machine-wide (what RegAsm /codebase writes). CodeBase is unescaped: the CLR does not decode %20.
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}"; ValueType: string; ValueName: ""; ValueData: "{#AddInClass}"; Flags: uninsdeletekey; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: ""; ValueData: "mscoree.dll"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: "ThreadingModel"; ValueData: "Both"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: "Class"; ValueData: "{#AddInClass}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: "Assembly"; ValueData: "{#AddInAssembly}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: "RuntimeVersion"; ValueData: "v4.0.30319"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32"; ValueType: string; ValueName: "CodeBase"; ValueData: "{code:AddInCodeBase}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32\{#AddInVersion}"; ValueType: string; ValueName: "Class"; ValueData: "{#AddInClass}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32\{#AddInVersion}"; ValueType: string; ValueName: "Assembly"; ValueData: "{#AddInAssembly}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32\{#AddInVersion}"; ValueType: string; ValueName: "RuntimeVersion"; ValueData: "v4.0.30319"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\InprocServer32\{#AddInVersion}"; ValueType: string; ValueName: "CodeBase"; ValueData: "{code:AddInCodeBase}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\ProgId"; ValueType: string; ValueName: ""; ValueData: "{#AddInProgId}"; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\CLSID\{#AddInClsid}\Implemented Categories\{{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}"; Flags: uninsdeletekey; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\{#AddInProgId}"; ValueType: string; ValueName: ""; ValueData: "{#AddInClass}"; Flags: uninsdeletekey; Tasks: swaddin
Root: HKLM; Subkey: "Software\Classes\{#AddInProgId}\CLSID"; ValueType: string; ValueName: ""; ValueData: "{#AddInClsid}"; Tasks: swaddin
; SOLIDWORKS only lists add-ins found here (machine-wide). Load at startup by default; Tools > Add-Ins toggles it.
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddInClsid}"; ValueType: dword; ValueName: ""; ValueData: 1; Flags: uninsdeletekey; Tasks: swaddin
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddInClsid}"; ValueType: string; ValueName: "Title"; ValueData: "{#AppName}"; Tasks: swaddin
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddInClsid}"; ValueType: string; ValueName: "Description"; ValueData: "Closed-die impression forging simulation ({#Publisher})"; Tasks: swaddin
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddInClsid}"; ValueType: dword; ValueName: "DesktopAddinType"; ValueData: 2; Tasks: swaddin
Root: HKLM; Subkey: "SOFTWARE\SolidWorks\AddIns\{#AddInClsid}"; ValueType: dword; ValueName: "3DExperienceAddinType"; ValueData: 2; Tasks: swaddin
; Where the add-in finds the app (it also checks HKCU, written by the PowerShell registration of the zip package).
Root: HKLM; Subkey: "Software\Marconnet Technologies\{#AppName}"; ValueType: string; ValueName: "AppPath"; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey; Tasks: swaddin
Root: HKLM; Subkey: "Software\Marconnet Technologies\{#AppName}"; ValueType: string; ValueName: "AddInPath"; ValueData: "{app}\SolidWorks\ImpressionFEA.SolidWorks.dll"; Tasks: swaddin
#endif

[Run]
#if Flavor == "light"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Get-GpuSolver.ps1"""; Description: "Download the optional NVIDIA GPU solver now (about 880 MB from NVIDIA)"; Flags: postinstall unchecked runascurrentuser
#endif
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: postinstall nowait skipifsilent

[Code]
#ifdef HasAddIn
var
  AddInDefaulted: Boolean;
#endif

function SolidWorksInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\SolidWorks') or DirExists(ExpandConstant('{commonpf64}\SOLIDWORKS Corp\SOLIDWORKS'));
end;

function IsProcessRunning(const Name: String): Boolean;
var
  Locator, Service, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + Name + '''');
    Result := Found.Count > 0;
  except
    Result := False;
  end;
end;

{ Unescaped file:/// URL of the add-in DLL, as RegAsm writes it. }
function AddInCodeBase(Param: String): String;
begin
  Result := ExpandConstant('{app}\SolidWorks\ImpressionFEA.SolidWorks.dll');
  StringChangeEx(Result, '\', '/', True);
  Result := 'file:///' + Result;
end;

{ Tick the SOLIDWORKS task by default when SOLIDWORKS is installed (once; later changes by the user are kept). }
procedure CurPageChanged(CurPageID: Integer);
begin
#ifdef HasAddIn
  if (CurPageID = wpSelectTasks) and not AddInDefaulted then
  begin
    AddInDefaulted := True;
    if SolidWorksInstalled then WizardSelectTasks('swaddin');
  end;
#endif
end;

{ SOLIDWORKS locks the add-in DLL while it runs. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsProcessRunning('SLDWORKS.exe') and FileExists(ExpandConstant('{app}\SolidWorks\ImpressionFEA.SolidWorks.dll')) then
    Result := 'SOLIDWORKS is running and has the ImpressionFEA add-in loaded. Close SOLIDWORKS, then press Back and Next to continue.';
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  while Result and IsProcessRunning('SLDWORKS.exe') and FileExists(ExpandConstant('{app}\SolidWorks\ImpressionFEA.SolidWorks.dll')) do
    Result := MsgBox('SOLIDWORKS is running and may have the ImpressionFEA add-in loaded. Close SOLIDWORKS and press OK, or Cancel to stop.',
      mbError, MB_OKCANCEL) = IDOK;
end;
