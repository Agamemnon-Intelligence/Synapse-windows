; Synapse for Windows installer (Inno Setup 6). Built by build.ps1:
;   iscc /DAppVersion=1.0.0 /DArch=x64 /DSourceDir=..\dist\Synapse-win-x64 /DOutputDir=..\dist installer\Synapse.iss
; Installs per user (no admin prompt), like most modern desktop apps.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\Synapse-win-" + Arch
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{6F0B3C8E-4A52-4C1B-9E1F-5A7D2C9B8E31}
AppName=Synapse
AppVersion={#AppVersion}
AppVerName=Synapse {#AppVersion}
AppPublisher=mertyesileducation
AppPublisherURL=https://github.com/Agamemnon-Intelligence/Synapse-windows
AppSupportURL=https://github.com/Agamemnon-Intelligence/Synapse-windows/issues
AppCopyright=Copyright © 2026 mertyesileducation
LicenseFile=..\LICENSE
DefaultDirName={localappdata}\Programs\Synapse
DefaultGroupName=Synapse
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=Synapse-{#AppVersion}-win-{#Arch}-setup
SetupIconFile=..\assets\synapse.ico
UninstallDisplayIcon={app}\Synapse.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
CloseApplications=force
RestartApplications=no
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "cli"; Description: "Add the synapse command to PATH (use any model from a terminal)"; GroupDescription: "Terminal:"
Name: "startup"; Description: "Start Synapse when I sign in (runs in the tray)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Synapse"; Filename: "{app}\Synapse.exe"; AppUserModelID: "Synapse.Windows"
Name: "{autodesktop}\Synapse"; Filename: "{app}\Synapse.exe"; Tasks: desktopicon; AppUserModelID: "Synapse.Windows"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Synapse"; ValueData: """{app}\Synapse.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Synapse.exe"; Description: "{cm:LaunchProgram,Synapse}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}\ui"

[Code]
const EnvKey = 'Environment';

function CliDir(): string;
begin
  Result := ExpandConstant('{app}\cli');
end;

function PathHas(Paths, Dir: string): Boolean;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0;
end;

procedure AddToPath();
var Paths: string;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then Paths := '';
  if PathHas(Paths, CliDir()) then exit;
  if (Paths <> '') and (Copy(Paths, Length(Paths), 1) <> ';') then Paths := Paths + ';';
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Paths + CliDir());
end;

procedure RemoveFromPath();
var Paths: string; P: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvKey, 'Path', Paths) then exit;
  Paths := ';' + Paths + ';';
  P := Pos(';' + Uppercase(CliDir()) + ';', Uppercase(Paths));
  if P = 0 then exit;
  Delete(Paths, P, Length(CliDir()) + 1);
  Paths := Copy(Paths, 2, Length(Paths) - 2);
  RegWriteExpandStringValue(HKCU, EnvKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('cli') then AddToPath();
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromPath();
end;
