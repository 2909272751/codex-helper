#ifndef MyAppVersion
  #error MyAppVersion must be supplied
#endif
#ifndef MyMainDir
  #error MyMainDir must be supplied
#endif
#ifndef MyHelperDir
  #error MyHelperDir must be supplied
#endif
#ifndef MyRescueDir
  #error MyRescueDir must be supplied
#endif
#ifndef MyOutputDir
  #error MyOutputDir must be supplied
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
UninstallDisplayName=Codex Helper
ArchitecturesInstallIn64BitMode=x64

[Files]
Source: "{#MyMainDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#MyHelperDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "{#MyRescueDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Codex Helper"; Filename: "{app}\CodexHelper.exe"
Name: "{autodesktop}\Codex Helper"; Filename: "{app}\CodexHelper.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他选项："; Flags: unchecked

[Run]
Filename: "{app}\CodexHelper.exe"; Description: "启动 Codex Helper"; Flags: nowait postinstall skipifsilent

[Code]
function HasDotNetDesktopRuntime8(): Boolean;
var
  RuntimeVersions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', RuntimeVersions) then begin
    for Index := 0 to GetArrayLength(RuntimeVersions) - 1 do begin
      if Copy(RuntimeVersions[Index], 1, 2) = '8.' then begin
        Result := True;
        exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := HasDotNetDesktopRuntime8();
  if not Result then begin
    MsgBox('此精简安装包需要 .NET 8 Desktop Runtime。请先安装微软官方 .NET 8 Desktop Runtime，或下载本项目的便携 ZIP / 离线完整包。', mbError, MB_OK);
  end;
end;
