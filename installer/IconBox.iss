; ============================================================================
;  IconBox 安装向导（Inno Setup 6）
;  编译：installer\build-installer.ps1   （或直接 ISCC.exe IconBox.iss）
;  产物：dist\IconBox-Setup-<版本>.exe
;  安装形态：按用户安装（免管理员、免 UAC），默认目录 %LOCALAPPDATA%\Programs\IconBox
;  说明：本脚本为 UTF-8 with BOM；build-installer.ps1 会自动补 BOM。
; ============================================================================

#define MyAppName "IconBox"
#define MyAppVersion "1.0.36"
#define MyAppPublisher "IconBox"
#define MyAppURL "https://github.com/FlowerSea111222/IconBox"
#define MyAppExeName "IconBox.exe"
#define PayloadDir "..\dist\portable"

[Setup]
; AppId 固定不变：升级时才能识别为"同一款软件"并原地覆盖安装
AppId={{B4C1E0A2-7F3D-4A85-9C6E-2D9B5A1F8E37}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppComments=免费桌面启动台：毛玻璃 Dock、应用面板、控制中心
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoCopyright={#MyAppPublisher}

; 按用户安装：不需要管理员权限，装到 %LOCALAPPDATA%\Programs，在线更新也能直接写文件
PrivilegesRequired=lowest
DefaultDirName={autopf}\IconBox
DefaultGroupName={#MyAppName}
ShowLanguageDialog=no
DisableWelcomePage=no
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=no
UsePreviousGroup=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes

OutputDir=..\dist
OutputBaseFilename=IconBox-Setup-{#MyAppVersion}
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Uninstallable=yes
CreateUninstallRegKey=yes

Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
WizardImageFile=assets\wizard-large.bmp
WizardSmallImageFile=assets\wizard-small.bmp
WizardImageStretch=yes
LicenseFile=assets\LICENSE.txt
SetupMutex=IconBoxSetupMutex
SetupLogging=yes
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
; —— 中文 ——
chinesesimplified.StartupGroup=启动选项：
chinesesimplified.StartupTask=登录 Windows 时自动启动 IconBox（推荐）
chinesesimplified.CloseAppTitle=IconBox 正在运行
chinesesimplified.CloseAppPrompt=检测到 IconBox 正在运行，安装/卸载前需要先把它关闭。%n%n是否现在关闭？（你的 Dock 布局与设置不会丢失）
chinesesimplified.CloseAppFail=无法关闭 IconBox，请手动退出后重试。
chinesesimplified.DotNetTitle=缺少 .NET Framework 4.8
chinesesimplified.DotNetMissing=本软件需要 .NET Framework 4.8，当前系统未检测到。%n%n是否打开微软官方下载页面？
chinesesimplified.DeleteDataTitle=同时删除个人配置？
chinesesimplified.DeleteDataPrompt=卸载将删除程序文件。%n%n是否同时删除你的 Dock 布局、图标缓存与全部设置？%n（位置：AppData\Local\IconBox）%n%n选择“否”将保留这些数据，以后重新安装可直接继续使用。
chinesesimplified.UninstKeepData=已保留配置数据（AppData\Local\IconBox）。
chinesesimplified.RunAfterInstall=立即启动 IconBox
; —— English ——
english.StartupGroup=Startup options:
english.StartupTask=Start IconBox when I sign in to Windows (recommended)
english.CloseAppTitle=IconBox is running
english.CloseAppPrompt=IconBox is currently running and must be closed before continuing.%n%nClose it now? (Your dock layout and settings will be kept.)
english.CloseAppFail=Could not close IconBox. Please exit it manually and try again.
english.DotNetTitle=.NET Framework 4.8 required
english.DotNetMissing=This application requires .NET Framework 4.8, which was not detected.%n%nOpen the official Microsoft download page?
english.DeleteDataTitle=Also delete personal data?
english.DeleteDataPrompt=Uninstall will remove the program files.%n%nAlso delete your dock layout, icon cache and all settings?%n(Location: AppData\Local\IconBox)%n%nChoose No to keep them; a future reinstall will pick them up.
english.UninstKeepData=Personal data kept in AppData\Local\IconBox.
english.RunAfterInstall=Launch IconBox

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "startup"; Description: "{cm:StartupTask}"; GroupDescription: "{cm:StartupGroup}"; Flags: checkedonce

[Files]
; 主程序与依赖
Source: "{#PayloadDir}\{#MyAppExeName}";            DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\{#MyAppExeName}.config";     DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Interop.UIAutomationClient.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\lib\NPSMLib.dll";            DestDir: "{app}\lib"; Flags: ignoreversion
; 文档与第三方许可
Source: "{#PayloadDir}\README.md";                  DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD_PARTY_NOTICES.md";     DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\licenses\*.txt";             DestDir: "{app}\licenses"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}";                     Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "{#MyAppName} 桌面启动台"
Name: "{group}\使用说明";                          Filename: "{app}\README.md"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}";               Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:RunAfterInstall}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 运行期可能产生的文件（在线更新、自检）
Type: files; Name: "{app}\self-test-result.txt"
Type: files; Name: "{app}\IconBox.Updater.exe"
Type: files; Name: "{app}\error.log"
Type: dirifempty; Name: "{app}\lib"
Type: dirifempty; Name: "{app}\licenses"
Type: dirifempty; Name: "{app}"

[Code]
const
  RUN_KEY      = 'Software\Microsoft\Windows\CurrentVersion\Run';
  APPROVED_KEY = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  RUN_NAME     = 'IconBox';
  APP_MUTEX    = 'IconBox.Desktop.v1';
  DOTNET_KEY   = 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full';
  DOTNET_URL   = 'https://dotnet.microsoft.com/download/dotnet-framework/net48';

var
  DeleteUserData: Boolean;

{ ---------- 通用 ---------- }

function DataDir: String;
begin
  Result := ExpandConstant('{localappdata}\IconBox');
end;

function Net48Installed: Boolean;
var
  Rel: Cardinal;
begin
  Result := False;
  if RegQueryDWordValue(HKLM, DOTNET_KEY, 'Release', Rel) and (Rel >= 528040) then
    Result := True
  else if IsWin64 and RegQueryDWordValue(HKLM64, DOTNET_KEY, 'Release', Rel) and (Rel >= 528040) then
    Result := True;
end;

function AppRunning: Boolean;
begin
  Result := CheckForMutexes(APP_MUTEX);
end;

{ 结束正在运行的 IconBox；先让它把任务栏/桌面图标还原，再强制结束。 }
function CloseRunningApp: Boolean;
var
  ResultCode, I: Integer;
  ExePath: String;
begin
  Result := True;
  if not AppRunning then
    Exit;

  if MsgBox(ExpandConstant('{cm:CloseAppPrompt}'), mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  { 优先用当前安装目录里的程序做优雅还原；目录还不存在时跳过 }
  ExePath := ExpandConstant('{app}\IconBox.exe');
  if FileExists(ExePath) then
    Exec(ExePath, '--restore-shell', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Exec('taskkill.exe', '/IM IconBox.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  { 旧版进程（GlassBoard.exe）也一并结束，否则它会继续占用任务栏/桌面图标状态 }
  Exec('taskkill.exe', '/IM GlassBoard.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 20 do
  begin
    if not AppRunning then
      Break;
    Sleep(150);
  end;

  if AppRunning then
  begin
    MsgBox(ExpandConstant('{cm:CloseAppFail}'), mbError, MB_OK);
    Result := False;
  end;
end;

{ 启动项：值格式与程序内 StartupRules.Command 保持一致 —— "路径" --startup }
procedure ApplyStartupEntry;
var
  Existing: String;
  Cmd: String;
begin
  if WizardIsTaskSelected('startup') then
  begin
    Cmd := '"' + ExpandConstant('{app}\IconBox.exe') + '" --startup';
    RegWriteStringValue(HKCU, RUN_KEY, RUN_NAME, Cmd);
  end
  else
  begin
    { 取消勾选时，只在启动项确实指向本安装目录时才清除，避免误删用户其它入口 }
    if RegQueryStringValue(HKCU, RUN_KEY, RUN_NAME, Existing)
       and (Pos(ExpandConstant('{app}'), Existing) > 0) then
    begin
      RegDeleteValue(HKCU, RUN_KEY, RUN_NAME);
      RegDeleteValue(HKCU, APPROVED_KEY, RUN_NAME);
    end;
  end;
end;

procedure RemoveStartupEntry;
var
  Existing: String;
begin
  if RegQueryStringValue(HKCU, RUN_KEY, RUN_NAME, Existing)
     and (Pos(ExpandConstant('{app}'), Existing) > 0) then
  begin
    RegDeleteValue(HKCU, RUN_KEY, RUN_NAME);
    RegDeleteValue(HKCU, APPROVED_KEY, RUN_NAME);
  end;
end;

{ ---------- 安装流程 ---------- }

function InitializeSetup: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not Net48Installed then
  begin
    { 静默安装（/SILENT、/VERYSILENT）不弹窗、不开浏览器，直接失败退出 }
    if not WizardSilent then
    begin
      if MsgBox(ExpandConstant('{cm:DotNetMissing}'), mbError, MB_YESNO) = IDYES then
        ShellExec('open', DOTNET_URL, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
    Result := False;
    Exit;
  end;
  if AppRunning then
    Result := CloseRunningApp;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { 更名迁移：清掉旧版 GlassBoard 的开机自启动项，避免新旧两份同时启动 }
  if CurStep = ssInstall then
  begin
    RegDeleteValue(HKCU, RUN_KEY, 'GlassBoard');
    RegDeleteValue(HKCU, APPROVED_KEY, 'GlassBoard');
  end;
  if CurStep = ssPostInstall then
    ApplyStartupEntry;
end;

{ ---------- 卸载流程 ---------- }

function InitializeUninstall: Boolean;
begin
  Result := True;
  if AppRunning then
  begin
    if not CloseRunningApp then
    begin
      Result := False;
      Exit;
    end;
  end;

  { 静默卸载绝不删数据：只有交互式卸载且用户明确同意才删 }
  DeleteUserData := False;
  if DirExists(DataDir) and not UninstallSilent then
    DeleteUserData := (MsgBox(ExpandConstant('{cm:DeleteDataPrompt}'), mbConfirmation, MB_YESNO) = IDYES);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveStartupEntry;

  if CurUninstallStep = usPostUninstall then
  begin
    if DeleteUserData and DirExists(DataDir) then
      DelTree(DataDir, True, True, True)
    else if DirExists(DataDir) then
      MsgBox(ExpandConstant('{cm:UninstKeepData}'), mbInformation, MB_OK);
  end;
end;
