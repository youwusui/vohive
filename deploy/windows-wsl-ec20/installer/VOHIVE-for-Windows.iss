#define MyAppName "VOHIVE for Windows"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "youwusui"
#define MyAppExeName "VoHiveControlCenter.exe"

[Setup]
AppId={{B67C9B69-4224-40B5-BF47-49D83335DF77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={commonpf32}\VOHIVE for Windows
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\..\..\artifacts\installer-output
OutputBaseFilename=VOHIVE-for-Windows-Setup-v{#MyAppVersion}
SetupIconFile=..\control-center\Assets\vohive.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
MinVersion=10.0.22000
CloseApplications=yes
RestartApplications=no
RestartIfNeededByRun=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: checkedonce

[Files]
Source: "..\..\..\artifacts\VOHIVE-for-Windows-v1.1.0-app-r3\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\..\artifacts\installer-cache\ffplay.exe"; DestDir: "{app}\Tools\ffmpeg"; Flags: ignoreversion
Source: "Install-VoHiveRuntime.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "Uninstall-VoHiveRuntime.ps1"; DestDir: "{app}\Installer"; Flags: ignoreversion
Source: "..\..\..\artifacts\installer-staging\vohive-rootfs.tar.gz"; DestDir: "{app}\Payload"; Flags: ignoreversion
Source: "..\..\..\artifacts\installer-cache\wsl.2.7.10.0.x64.final.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\..\..\artifacts\installer-cache\usbipd-win_5.3.0_x64.final.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\..\..\artifacts\installer-cache\MicrosoftEdgeWebView2RuntimeInstallerX64.r2.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autodesktop}\VOHIVE for Windows"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{group}\VOHIVE for Windows"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\卸载 VOHIVE for Windows"; Filename: "{uninstallexe}"

[Run]
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\wsl.2.7.10.0.x64.final.msi"" /qn /norestart"; StatusMsg: "正在安装 WSL2 运行时..."; Flags: runhidden waituntilterminated
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\usbipd-win_5.3.0_x64.final.msi"" /qn /norestart"; StatusMsg: "正在安装 USB 设备桥接组件..."; Flags: runhidden waituntilterminated
Filename: "{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.r2.exe"; Parameters: "/silent /install"; StatusMsg: "正在安装 WebView2 运行时..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Installer\Install-VoHiveRuntime.ps1"" -InstallRoot ""{app}"" -DistroName ""VoHive"" -DistroDataRoot ""{commonappdata}\VOHIVE for Windows\WSL"""; StatusMsg: "正在部署 VOHIVE WSL 环境..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExeName}"; Description: "启动 VOHIVE for Windows"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File ""{app}\Installer\Uninstall-VoHiveRuntime.ps1"" -DistroName ""VoHive"" -DistroDataRoot ""{commonappdata}\VOHIVE for Windows\WSL"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveVoHiveWSL"

[Code]
function NeedRestart(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\install-pending-restart.marker'));
end;
