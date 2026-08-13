; SimpleCalendar Inno Setup Script
; Builds a single-file installer that bundles the self-contained .NET app

#define MyAppName "SimpleCalendar"
#define MyAppVersion "1.2.3"
#define MyAppPublisher "SimpleCalendar"
#define MyAppExeName "SimpleCalendar.exe"

[Setup]
AppId={{B7F3E2A1-9C4D-4E8B-A5F6-1A2B3C4D5E6F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; 一键安装：跳过欢迎/安装位置/确认页，固定装到默认目录（与旧版一致，可直接覆盖升级）
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableProgramGroupPage=yes
OutputDir=..
OutputBaseFilename=SimpleCalendarSetup_v{#MyAppVersion}
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "bin\Release\publish_installer\SimpleCalendar.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\publish_installer\ClockHookDll.dll"; DestDir: "{app}"; Flags: ignoreversion
; 自包含单文件发布时 WPF 原生 DLL 无法打进单文件，必须随 exe 一起安装，否则启动即崩溃（DllNotFoundException）
Source: "bin\Release\publish_installer\wpfgfx_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\publish_installer\PresentationNative_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\publish_installer\PenImc_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\publish_installer\D3DCompiler_47_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\publish_installer\vcruntime140_cor3.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[UninstallRun]
; Remove auto-start registry entry on uninstall
Filename: "reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""SimpleCalendar"" /f"; Flags: runhidden
