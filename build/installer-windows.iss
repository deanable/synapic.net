; Inno Setup script for Synapic (spec §7.2 Windows row)
; Preprocessor defines passed by package-windows.ps1:
;   /DAppVersion=1.0.0  /DRid=win-x64  /DArtifactsDir=artifacts\win-x64
; Requires Inno Setup 6.3+ (DownloadTemporaryFile).

#define AppName "Synapic"
#define AppPublisher "Synapic Project"
#define AppExe "Synapic.exe"
#define SidecarExe "synapic-inference.exe"
; Offline prerequisite: package-windows.ps1 stages the official .NET 10
; Desktop Runtime installer next to the payload; when present it is bundled
; into the setup and installed silently. When absent the installer downloads
; it at the Ready step instead (DownloadTemporaryFile).
#define RuntimeExe "windowsdesktop-runtime-win-x64.exe"

#ifndef AppVersion
#define AppVersion "1.0.0"
#endif
#ifndef Rid
#define Rid "win-x64"
#endif
#ifndef ArtifactsDir
#define ArtifactsDir "artifacts\win-x64"
#endif

[Setup]
; AppId is an identity, not a display string: Inno only appends to an existing
; uninstall log when both AppIds match, and it names the Uninstall registry key
; ({AppId}_is1). It does *not* have to be a GUID - Inno accepts any string and
; defaults to AppName - so the hand-written value below is legal as-is. It also
; shipped in v0.1.0, and changing it would strand that install: a second
; Add/Remove Programs entry with no upgrade path. Leave it alone unless that is
; genuinely the intent.
AppId={{8A7C2C31-5E0D-4B21-9C4F-SYNAPICNET01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; Paths in this script resolve relative to the script's own directory (build/).
SetupIconFile=..\assets\icons\Icon.ico
OutputDir={#ArtifactsDir}
OutputBaseFilename=Synapic-Setup-{#Rid}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; The entire framework-dependent publish output: exe, sidecar, managed and
; native dlls, and the mandatory *.json (runtimeconfig, deps — without
; runtimeconfig.json the apphost treats the app as self-contained and fails
; to start). pdbs are dev artifacts; the staged runtime installer is excluded
; from the app payload and bundled into {tmp} below when present.
; The setup exe lives in the same folder (OutputDir) and is excluded: a rebuild
; would otherwise embed the previous installer into the new one, and ISCC cannot
; read the file it is writing.
Source: "{#ArtifactsDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,{#RuntimeExe},Synapic-Setup-*.exe"
#ifexist ArtifactsDir + "\" + RuntimeExe
#define BundleRuntime
; Bundled offline prerequisite — extracted to {tmp} and run silently when missing.
Source: "{#ArtifactsDir}\{#RuntimeExe}"; DestDir: "{tmp}"; Flags: deleteafterinstall
#endif

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; The service console (v1.4) sits beside the app in the Start menu: it manages
; the background indexer, and a user looking for "the service thing" finds it.
Name: "{group}\Synapic Service"; Filename: "{app}\Synapic.ServiceConsole.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; The indexer install is not here: Setup calls it from CurStepChanged so it can
; read the exit code and say on the finish page whether the service started
; (T-08-03). [Run] can start a program but cannot report what it returned.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Inno executes [UninstallRun] before it removes [Files]. --uninstall stops and
; deletes the service but keeps %ProgramData%\Synapic (database, logs, config),
; because an uninstall must not destroy an index or its log trail (D-11-32).
Filename: "{app}\Synapic.Indexer.exe"; Parameters: "--uninstall"; RunOnceId: "RemoveSynapicIndexerService"; Flags: runhidden waituntilterminated

[Code]
const
  RuntimeSetupUrl =
    'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/10.0.12/windowsdesktop-runtime-win-x64.exe';
  RuntimeExeName = 'windowsdesktop-runtime-win-x64.exe';
#ifdef BundleRuntime
  BundledRuntime = True;
#else
  BundledRuntime = False;
#endif

var
  IndexerInstallStarted: Boolean;
  IndexerInstallExitCode: Integer;

// Host-presence probe (official detection point, matches the in-app check):
// HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost → Version
// (REG_SZ, e.g. "10.0.12"). Absent key ⇒ no .NET host at all.
function IsDotNetMissing(): Boolean;
var
  Version: String;
  Dot, Major: Integer;
begin
  Result := True;
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost', 'Version', Version) then
    Exit;
  Dot := Pos('.', Version + '.');
  Major := StrToIntDef(Copy(Version, 1, Dot - 1), 0);
  Result := Major < 10;
end;

// Canonical prerequisite hook: runs after the Ready page and before any app
// files are copied. The app is framework-dependent, so when the runtime is
// missing we resolve a local installer copy — the bundled one (extracted to
// {tmp}) or a live download (built-in progress UI) — and run it silently.
// Returning a non-empty string aborts setup with that message.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not IsDotNetMissing() then Exit;

  if BundledRuntime then
  begin
    WizardForm.StatusLabel.Caption := 'Extracting the .NET 10 Desktop Runtime…';
    ExtractTemporaryFile(RuntimeExeName);
  end
  else
  begin
    try
      WizardForm.StatusLabel.Caption := 'Downloading the .NET 10 Desktop Runtime (prerequisite)…';
      DownloadTemporaryFile(RuntimeSetupUrl, RuntimeExeName, '', nil);
    except
      Result := 'Could not download the .NET 10 Desktop Runtime.'#13#10 +
        'An internet connection is required the first time you install.';
      Exit;
    end;
  end;

  WizardForm.StatusLabel.Caption := 'Installing the .NET 10 Desktop Runtime (prerequisite)…';
  if not Exec(ExpandConstant('{tmp}\' + RuntimeExeName), '/install /quiet /norestart',
    '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'The .NET 10 Desktop Runtime installer could not be started.';
    Exit;
  end;

  // 0 = success; 3010 = success, reboot required.
  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    SuppressibleMsgBox('A restart is needed to finish the .NET runtime installation.'#13#10 +
      'Setup will complete after you restart this computer.', mbInformation, MB_OK, IDOK);
  end
  else if ResultCode <> 0 then
    Result := 'The .NET 10 Desktop Runtime installer exited with code ' +
      IntToStr(ResultCode) + '.';
end;

// D-11-30: the indexer registers and starts itself, through the same code a
// manual --install runs; the installer never constructs sc.exe arguments.
// ssPostInstall fires after the payload and the runtime prerequisite are in
// place and before [Run]; the exit code is kept for the finish page.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;

  WizardForm.StatusLabel.Caption := 'Installing the Synapic background indexer...';
  IndexerInstallStarted := Exec(ExpandConstant('{app}\Synapic.Indexer.exe'), '--install', '',
    SW_HIDE, ewWaitUntilTerminated, IndexerInstallExitCode);
  Log('Synapic.Indexer.exe --install: started=' + IntToStr(Ord(IndexerInstallStarted)) +
    ', exit code=' + IntToStr(IndexerInstallExitCode));
end;

// T-08-03: a service that could not be registered or started is reported here,
// not swallowed. The common first-install case is a start that fails because no
// configuration exists yet; the indexer's own log names the exact reason.
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID <> wpFinished then Exit;

  if not IndexerInstallStarted then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The Synapic background indexer service could not be registered. ' +
      'Run Synapic.Indexer.exe --install from an elevated prompt to retry.'
  else if IndexerInstallExitCode <> 0 then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      'The Synapic background indexer is installed but did not start (exit code ' +
      IntToStr(IndexerInstallExitCode) + '). Write %ProgramData%\Synapic\indexer-config.json, ' +
      'then run: sc.exe start SynapicIndexer';
end;

[UninstallDelete]
; Optionally remove per-user model cache on uninstall (user choice via checkbox not supported here; kept)
Type: filesandordirs; Name: "{localappdata}\Synapic\models"
