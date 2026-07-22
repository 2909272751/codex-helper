#ifndef MyAppVersion
  #error MyAppVersion must be supplied by scripts\build-release.ps1
#endif

#ifndef MyMainExe
  #error MyMainExe must be supplied by scripts\build-release.ps1
#endif

#ifndef MyCredentialHelperExe
  #error MyCredentialHelperExe must be supplied by scripts\build-release.ps1
#endif

#ifndef MyRescueExe
  #error MyRescueExe must be supplied by scripts\build-release.ps1
#endif

#ifndef MyOutputDir
  #error MyOutputDir must be supplied by scripts\build-release.ps1
#endif

#ifndef MyGuideFile
  #error MyGuideFile must be supplied by scripts\build-release.ps1
#endif

[Setup]
AppId={{1D76B7B1-7F55-4CAB-9C29-2EA2C2E104B0}
AppName=Codex Helper
AppVersion={#MyAppVersion}
AppPublisher=2909272751
DefaultDirName={localappdata}\Programs\Codex Helper
DefaultGroupName=Codex Helper
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename=codex-helper-v{#MyAppVersion}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile={#MyGuideFile}
UninstallDisplayName=Codex Helper
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "{#MyMainExe}"; DestDir: "{app}"; DestName: "CodexHelper.exe"; Flags: ignoreversion
Source: "{#MyCredentialHelperExe}"; DestDir: "{app}"; DestName: "CodexHelperCredentialHelper.exe"; Flags: ignoreversion
Source: "{#MyRescueExe}"; DestDir: "{app}"; DestName: "CodexHelperRescue.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Codex Helper"; Filename: "{app}\CodexHelper.exe"
Name: "{autodesktop}\Codex Helper"; Filename: "{app}\CodexHelper.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项："; Flags: unchecked

[Run]
Filename: "{app}\CodexHelper.exe"; Description: "启动 Codex Helper"; Flags: nowait postinstall skipifsilent
