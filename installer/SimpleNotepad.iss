; Inno Setup script for Simple Notepad
; Builds a per-machine installer that lays the self-contained app down under
; Program Files (no temp extraction, fast startup on every launch).
; Build with: "ISCC.exe" installer\SimpleNotepad.iss
;   Override the version in CI: "ISCC.exe" /DAppVersion=1.0.42 installer\SimpleNotepad.iss

#define AppName "Simple Notepad"
; AppVersion can be overridden on the ISCC command line with /DAppVersion=x.y.z
#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#define AppPublisher "Simple Notepad"
#define AppExeName "SimpleNotepad.exe"
; Path to the published self-contained folder build, relative to this script.
#define SourceDir "..\artifacts\app-folder"
; Plaintext folder the app reads installer-provisioned credentials from on first run.
#define DataFolder "SimpleNotepad"

[Setup]
; A stable GUID identifies the app for upgrades/uninstall. Do not reuse for other apps.
AppId={{B6F6B5C2-3D7E-4F2A-9C1E-2A6F0E7D8C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
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
; Cleanly upgrade in place: close a running instance, then don't try to relaunch it.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Dirs]
; Create the shared data folder with user-modify rights so the app (running as the
; signed-in user, not elevated) can re-encrypt and then delete the provisioning file.
Name: "{commonappdata}\{#DataFolder}"; Permissions: users-modify

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  AiPage: TInputQueryWizardPage;
  SyncPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  AiPage := CreateInputQueryPage(wpWelcome,
    'AI Rewrite (optional)',
    'Azure OpenAI credentials for the Rewrite and AI title features.',
    'Leave any field blank to skip. You can also set or change these later inside the app under the Settings (gear) button.');
  AiPage.Add('Endpoint (https://your-resource.openai.azure.com/):', False);
  AiPage.Add('Deployment name:', False);
  AiPage.Add('API key:', True);

  SyncPage := CreateInputQueryPage(AiPage.ID,
    'Cloud Sync (optional)',
    'Azure Blob Storage connection for syncing notes across your devices.',
    'Leave any field blank to skip. You can also set or change these later inside the app under the Settings (gear) button.');
  SyncPage.Add('Connection string:', False);
  SyncPage.Add('Container (default: simplenotepad):', False);
  SyncPage.Add('Device name:', False);
end;

function JsonEscape(const S: string): string;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteProvisioningFile;
var
  AiEndpoint, AiDeployment, AiKey, SyncConn, SyncContainer, DeviceName: string;
  Json, Dir, FilePath: string;
begin
  AiEndpoint := Trim(AiPage.Values[0]);
  AiDeployment := Trim(AiPage.Values[1]);
  AiKey := Trim(AiPage.Values[2]);
  SyncConn := Trim(SyncPage.Values[0]);
  SyncContainer := Trim(SyncPage.Values[1]);
  DeviceName := Trim(SyncPage.Values[2]);

  // Nothing worth provisioning unless an AI key/endpoint or a sync connection was provided.
  if (AiEndpoint = '') and (AiDeployment = '') and (AiKey = '') and (SyncConn = '') then
    Exit;

  Json :=
    '{' + #13#10 +
    '  "aiEndpoint": "' + JsonEscape(AiEndpoint) + '",' + #13#10 +
    '  "aiDeployment": "' + JsonEscape(AiDeployment) + '",' + #13#10 +
    '  "aiApiKey": "' + JsonEscape(AiKey) + '",' + #13#10 +
    '  "syncConnectionString": "' + JsonEscape(SyncConn) + '",' + #13#10 +
    '  "syncContainer": "' + JsonEscape(SyncContainer) + '",' + #13#10 +
    '  "deviceName": "' + JsonEscape(DeviceName) + '"' + #13#10 +
    '}' + #13#10;

  Dir := ExpandConstant('{commonappdata}\{#DataFolder}');
  ForceDirectories(Dir);
  FilePath := Dir + '\provisioning.json';
  SaveStringToFile(FilePath, Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteProvisioningFile;
end;
