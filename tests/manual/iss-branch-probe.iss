#define AppName "Synapic"
#define ArtifactsDir "artifacts"
#define RuntimeExe "windowsdesktop-runtime-win-x64.exe"
#ifexist ArtifactsDir + "\" + RuntimeExe
#pragma message "RUNTIME-BUNDLED-BRANCH"
#else
#pragma message "DOWNLOAD-FALLBACK-BRANCH"
#endif
[Setup]
AppName={#AppName}
AppVersion=1.0.0
DefaultDirName={autopf}\Test
