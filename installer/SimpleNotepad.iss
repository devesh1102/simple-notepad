; Inno Setup script for Simple Notepad
; Builds a per-machine installer that lays the self-contained app down under
; Program Files (no temp extraction, fast startup on every launch).
; Build with: "ISCC.exe" installer\SimpleNotepad.iss

#define AppName "Simple Notepad"
#define AppVersion "1.0.0"
#define AppPublisher "Simple Notepad"
#define AppExeName "SimpleNotepad.exe"
; Path to the published self-contained folder build, relative to this script.
#define SourceDir "..\artifacts\app-folder"

[Setup]
; A stable GUID identifies the app for upgrades/uninstall. Do not reuse for other apps.
AppId={{B6F6B5C2-3D7E-4F2A-9C1E-2A6F0E7D8C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir=..\artifacts\installer
OutputBaseFilename=SimpleNotepad-Setup-{#AppVersion}
; Per-machine install (Program Files) requires elevation.
PrivilegesRequired=admin

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
