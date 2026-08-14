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
#ifndef MyRunnerDir
  #error MyRunnerDir must be supplied
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
Source: "{#MyRunnerDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
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
  FindRec: TFindRec;
  DesktopSharedKey: String;
  VersionNames: TArrayOfString;
  i: Integer;
begin
  Result := False;

  // 1) 注册表权威记录：.NET 安装器会在该键下登记已安装的 Windows Desktop Runtime x64 版本值。
  //    覆盖任意安装目录（不依赖 Program Files 固定位置），安装 .NET 8 SDK 亦可满足。
  //    只有至少一个已登记版本名以 8. 开头才算满足，避免把只装 9/10 的电脑误判为已装 8。
  DesktopSharedKey := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetValueNames(HKLM64, DesktopSharedKey, VersionNames) then begin
    for i := 0 to GetArrayLength(VersionNames) - 1 do begin
      if Copy(VersionNames[i], 1, 2) = '8.' then begin
        Result := True;
        exit;
      end;
    end;
  end;

  // 2) 文件系统兜底：默认安装位置的 shared framework（glob 覆盖 8.x 任意补丁版本）。
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) then begin
        Result := True;
        exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDotNetDesktopRuntime8();
  if not Result then begin
    if MsgBox('此精简安装包需要 .NET 8 Desktop Runtime（Windows x64）。' + #13#10 + #13#10 + '点击“是”将打开微软 .NET 8 官方下载选择页，请选择 Windows x64 的 “.NET Desktop Runtime 8”，或直接安装 .NET 8 SDK（亦可满足）。' + #13#10 + #13#10 + '安装完成后，请重新打开并运行本安装包。' + #13#10 + '点击“否”则退出安装。', mbConfirmation, MB_YESNO) = IDYES then begin
      ShellExec('open', 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
  end;
end;
