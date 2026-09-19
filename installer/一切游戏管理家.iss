; 一切游戏管理家 —— Inno Setup 安装包脚本
;
; 用 F:\Inno Setup 6\ISCC.exe 编译。
; 安装包只装程序本体；游戏库数据由程序自己决定放哪：
; 装到 Program Files 时写不进程序目录，程序会自动改用 %APPDATA%\一切游戏管理家\data，
; 卸载时不会动这份数据（除非用户在卸载时主动选择删除）。

#define AppName "一切游戏管理家"
#define AppVersion "1.0.0"
#define AppPublisher "雪村落雪 · deepseek"
#define AppExeName "一切游戏管理家.exe"
#define SourceDir "..\src\publish"

[Setup]
; AppId 一经发布就不要改，否则升级会被当成另一个程序。
AppId={{7C4E1B92-3A5D-4E88-9F21-6B0D5A7C3E14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir=..\dist
OutputBaseFilename=一切游戏管理家-安装包-{#AppVersion}
SetupIconFile=..\src\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 默认「仅为我安装」，不需要管理员权限；有需要可以在界面上切成「为所有用户安装」。
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableWelcomePage=no

[Languages]
Name: "chinese"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#SourceDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\使用说明"; Filename: "{app}\README.md"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; 开机自启动故意不在这里写注册表：安装程序可能是以管理员身份运行的，
; 那样 HKCU 会写到管理员账户下，而不是真正要用这个程序的人。
; 程序自己的「设置 → 开机自动启动」是以当前用户身份运行的，写的一定是对的那个账户。

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前先结束正在运行的进程，否则文件删不掉。
Filename: "{cmd}"; Parameters: "/c taskkill /f /im ""{#AppExeName}"""; Flags: runhidden; RunOnceId: "KillApp"

[Code]
// 卸载时问一句要不要连游戏库一起删掉，默认不删。
//
// 数据可能在两个地方：单用户安装（数据写在程序旁边）用 {app}\data；
// 为所有用户安装时装到 Program Files，程序写不进去，数据会落到用户目录下。
// 两个位置都要检查。
procedure AskDeleteData(const DataDir: String);
begin
  if not DirExists(DataDir) then Exit;

  // 静默卸载时一律保留数据。被抑制的对话框会按第一个按钮返回，也就是「是」，
  // 那会把用户的游戏库在无人确认的情况下删掉。
  if UninstallSilent then Exit;

  if MsgBox('是否同时删除游戏库数据？' + #13#10 + #13#10 +
            '位置：' + DataDir + #13#10 +
            '里面包含游戏记录、封面和壁纸。' + #13#10 + #13#10 +
            '选择「否」会保留这些数据，以后重新安装可以直接接着用。',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // 在删除程序文件之前问，删完数据后程序目录才会被卸载程序一并清掉。
  if CurUninstallStep = usUninstall then
  begin
    AskDeleteData(ExpandConstant('{app}\data'));
    AskDeleteData(ExpandConstant('{userappdata}\{#AppName}\data'));
  end;
end;
