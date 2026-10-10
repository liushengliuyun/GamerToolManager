; GamerTool 管理器 安装脚本 (Inno Setup 7)
; 版本号自动读取 publish-sc\GamerToolManager.exe 的文件版本，永不脱节
#define AppVersion GetVersionNumbersString("publish-sc\GamerToolManager.exe")

[Setup]
AppId={{0A1D0D2F-86BA-4D45-A5F8-D8B24F4B419A}
AppName=GamerTool 管理器
AppVersion={#AppVersion}
AppVerName=GamerTool 管理器 {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
AppPublisher=GamerTool
DefaultDirName={autopf}\GamerToolManager
DefaultGroupName=GamerTool 管理器
UninstallDisplayIcon={app}\GamerToolManager.exe
OutputDir=installer
OutputBaseFilename=GamerToolManager-Setup
SetupIconFile=app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
ShowLanguageDialog=no

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务:"

[Files]
Source: "publish-sc\GamerToolManager.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "tools\st-setup-1.8.30.exe"; DestDir: "{app}\bundled"; Flags: ignoreversion
Source: "使用教程.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GamerTool 管理器"; Filename: "{app}\GamerToolManager.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\GamerTool 管理器"; Filename: "{app}\GamerToolManager.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GamerToolManager.exe"; Description: "立即运行 GamerTool 管理器(&L)"; Flags: nowait postinstall skipifsilent
