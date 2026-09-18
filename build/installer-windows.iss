; Inno Setup script for Synapic (spec §7.2 Windows row)
; Preprocessor defines passed by package-windows.ps1:
;   /DAppVersion=1.0.0  /DRid=win-x64  /DArtifactsDir=artifacts\win-x64

#define AppName "Synapic"
#define AppPublisher "Synapic Project"
#define AppExe "Synapic.exe"
#define SidecarExe "synapic-inference.exe"

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
AppId={{8A7C2C31-5E0D-4B21-9C4F-SYNAPICNET01}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
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
Source: "{#ArtifactsDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ArtifactsDir}\{#SidecarExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ArtifactsDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists(ExpandConstant('{#ArtifactsDir}'))
Source: "{#ArtifactsDir}\*.pdb"; DestDir: "{app}"; Flags: skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Optionally remove per-user model cache on uninstall (user choice via checkbox not supported here; kept)
Type: filesandordirs; Name: "{localappdata}\Synapic\models"
