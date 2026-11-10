; Gloam setup (Inno Setup 6). package.ps1 compiles it:
;
;   ISCC.exe /DVersion=X.Y.Z /DSourceDir=<staged package> /O<output dir> installer\Gloam.iss
;
; It installs per user, without administrator rights, into %LOCALAPPDATA%\Programs\Gloam and adds a
; "Gloam" Start menu shortcut and an entry in Settings > Apps > Installed apps. Running a newer setup
; updates that same folder in place, so the shortcut, a taskbar pin and the Start with Windows
; registration (an HKCU Run value pointing at Gloam.exe) never need repointing.
;
; Tray -> Check for Updates downloads the release's setup and runs it with
;   /SILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1
; and exits. The setup waits for that Gloam to let go of its single-instance mutex (it holds its
; files open until then), replaces the files, and starts Gloam again: /relaunch=1 says to, because
; by the time the setup looks, the Gloam that asked may already be gone. See UpdateService.
;
; The unins000.exe the setup leaves beside Gloam.exe is how the app tells an installation from a dev
; run (UpdateService.IsInstallation): only an installation updates itself or takes over the Start
; with Windows registration.
;
; Uninstalling deletes the installed files, the shortcut, and the Start with Windows registration
; when it points into the installation. %LOCALAPPDATA%\Gloam (settings, calibration reports, logs)
; is the user's and stays.
;
; Keep this file ASCII.

#ifndef Version
  #error Pass the version: /DVersion=X.Y.Z
#endif
#ifndef SourceDir
  #error Pass the staged package: /DSourceDir=<folder holding Gloam.exe>
#endif

#define RepoUrl "https://github.com/dkozlov-ua/gloam"

[Setup]
; Identifies the installation across versions: never change it.
AppId={{CB8DF675-CAAD-474E-9C3D-DC066BF4F915}
AppName=Gloam
AppVersion={#Version}
AppVerName=Gloam {#Version}
AppPublisher=Dmytro Kozlov
AppCopyright=Copyright (c) Dmytro Kozlov
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#Version}
VersionInfoProductName=Gloam

; Per user: {autopf} is %LOCALAPPDATA%\Programs here.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Gloam
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Gloam is waited for by [Code] below, through its own single-instance mutex, rather than closed
; by Restart Manager: it exits through its own tray Exit (or the update flow), never from outside.
CloseApplications=no
RestartApplications=no

WizardStyle=modern
SetupIconFile=..\src\Gloam\app.ico
UninstallDisplayIcon={app}\Gloam.exe
UninstallDisplayName=Gloam
OutputBaseFilename=Gloam-{#Version}-setup
Compression=lzma2/max
SolidCompression=yes

[InstallDelete]
; The bundled ArgyllCMS lives in a folder named for its version, and ArgyllPathFinder picks among
; whatever versions are there: an update that bumps ArgyllCMS must not leave the old one beside it.
Type: filesandordirs; Name: "{app}\argyll_cache"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Gloam"; Filename: "{app}\Gloam.exe"; Comment: "Gamma correction for SDR content in Windows HDR mode"

[Run]
; An update from the tray, or a setup run over a running Gloam: start it again.
Filename: "{app}\Gloam.exe"; Check: ShouldRelaunch; Flags: nowait
Filename: "{app}\Gloam.exe"; Description: "Start Gloam"; Check: not ShouldRelaunch; Flags: nowait postinstall skipifsilent

[Code]
const
  { Program.cs: held for the whole life of the tray. }
  InstanceMutex = 'Local\Gloam-SingleInstance';
  { StartupManager: the Start with Windows registration. }
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'Gloam';

var
  WasRunning: Boolean;

function ShouldRelaunch: Boolean;
begin
  Result := WasRunning or (ExpandConstant('{param:relaunch|0}') = '1');
end;

{ True once no Gloam holds the mutex, polling for up to Seconds. }
function WaitForExit(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 4 do
  begin
    if not CheckForMutexes(InstanceMutex) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(250);
  end;
  Result := not CheckForMutexes(InstanceMutex);
end;

{ Gloam holds its files open while it runs. An update from the tray exits right after starting a
  silent setup, so a silent run waits for that; a run by hand asks the user to exit it. }
function GloamExited(Silent: Boolean): Boolean;
begin
  if Silent then
  begin
    Result := WaitForExit(30);
    Exit;
  end;
  Result := WaitForExit(1);
  while not Result do
  begin
    if MsgBox('Gloam is running. Exit it from its tray icon (right-click, Exit), then click Retry.',
              mbError, MB_RETRYCANCEL) <> IDRETRY then
      Exit;
    Result := WaitForExit(5);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  WasRunning := CheckForMutexes(InstanceMutex);
  if not GloamExited(WizardSilent) then
    Result := 'Gloam is still running. Exit it from its tray icon, then run the setup again.';
end;

function InitializeUninstall: Boolean;
begin
  Result := GloamExited(UninstallSilent);
end;

{ The Start with Windows registration goes with the installation it points into, and only then:
  it may point somewhere else entirely. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if RegQueryStringValue(HKCU, RunKey, RunValue, Value) and
     (Pos(Lowercase(ExpandConstant('{app}\')), Lowercase(Value)) > 0) then
    RegDeleteValue(HKCU, RunKey, RunValue);
end;
