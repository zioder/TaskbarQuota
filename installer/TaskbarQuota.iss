; Inno Setup — TaskbarQuotaSetup-{version}-{arch}.exe (PowerToys-style naming)
; CI: pass absolute /DPublishDir (Inno resolves relative paths from this script's folder).

#ifndef MyAppName
  #define MyAppName "TaskbarQuota"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "TaskbarQuota.exe"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\TaskbarQuota.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef TargetArch
  #define TargetArch "x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#if TargetArch == "arm64"
  #define ArchAllowed "arm64"
  #define ArchInstallMode "arm64"
#else
  #define ArchAllowed "x64compatible"
  #define ArchInstallMode "x64compatible"
#endif

[Setup]
AppId={{A7C4E2B1-9F3D-4A8E-B5C6-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Zied Kallel
AppPublisherURL=https://github.com/zioder/TaskbarQuota
AppSupportURL=https://github.com/zioder/TaskbarQuota/issues
AppUpdatesURL=https://github.com/zioder/TaskbarQuota/releases
DefaultDirName={autopf}\TaskbarQuota
DefaultGroupName=TaskbarQuota
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=TaskbarQuotaSetup-{#MyAppVersion}-{#TargetArch}
SetupIconFile=..\src\TaskbarQuota.App\Assets\TaskBarQuota.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchInstallMode}
MinVersion=10.0.19041
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
