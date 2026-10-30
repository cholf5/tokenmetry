; Tokenmetry — Windows 安装器（Inno Setup 6）
;
; 为什么要安装器而不是发 zip：zip 只有裸 exe——没有卸载入口、没有开始菜单项，自启动写下的
; HKCU\...\Run 指向用户随手解压的临时路径（下载文件夹被清理即失效）。安装器把它变成一份
; 位置固定、可卸载、可原地升级的安装。
;
; 用法：
;   ISCC.exe /DMyAppVersion=0.3.1 installer\Tokenmetry.iss
; 产物：dist\Tokenmetry-{版本}-win-x64-setup.exe（与 CI/package.sh/UpdateAssets.cs 的资产名契约同源）。
;
; 按用户安装（PrivilegesRequired=lowest，装到 %LOCALAPPDATA%\Programs\Tokenmetry）：
; 托盘常驻应用不需要管理员权限，也避免 Program Files 下升级要提权。默认目录必须与
; UpdateAssets.InstallDirectory 一致——应用据此判定"已安装副本"并选安装器做静默升级。

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#ifndef SourceExe
  #define SourceExe "..\dist\stage\win-x64\Tokenmetry.exe"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define MyAppName "Tokenmetry"
#define MyAppPublisher "cholf5"
#define MyAppUrl "https://github.com/cholf5/tokenmetry"
#define MyAppExeName "Tokenmetry.exe"

[Setup]
; AppId 一改，覆盖安装就变成"并存两个版本"——这是永久身份，永远不要动。
AppId={{6C1F4E52-3B7A-4A9E-9E4C-2F3D1C0A7B58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\Tokenmetry.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
; 应用是托盘常驻：安装/升级前必须让旧实例让开（同一互斥名，见 SingleInstanceGuard）。
AppMutex=Local\Tokenmetry.SingleInstance
CloseApplications=yes

[Languages]
; 只带 Inno 官方语言（英文内置）。中文向导需要额外的非官方 chinesesimplified.isl，
; 而静默升级（应用内下载更新）根本不显示向导——需要时再补，不为它引入外部依赖。
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 桌面快捷方式默认勾选（Windows 用户的第一反应是去桌面找），想干净的人可以取消。
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: {#SourceExe}; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 装完（含 /SILENT 静默升级）把应用拉起来。不写 skipifsilent：静默升级后用户点完"下载更新"
; 期待应用自己回来，而不是凭空消失。
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall

[UninstallDelete]
; 只清安装目录自身。数据目录（%LOCALAPPDATA%\Tokenmetry，含 tokenmetry.db 与 settings.json）
; 一律不动——卸载重装不该丢历史聚合。
