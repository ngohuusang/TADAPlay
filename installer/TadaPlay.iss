; Inno Setup script for TADA Play.
; Build the publish output first:
;   dotnet publish TadaPlay/TadaPlay.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -p:PublishSingleFile=false -o publish/TadaPlay
; Then compile this script with ISCC.exe (Inno Setup 6).

#define MyAppName "TADA Play"
#define MyAppVersion "3.13.0"
#define MyAppExeName "TadaPlay.exe"
#define MyPublishDir "..\publish\TadaPlay"

[Setup]
; Fixed AppId (do not change) so future versions upgrade in place instead of installing side by side.
AppId={{5C3E9C2A-6C9A-4E7B-9F3A-5B7B7E2A4A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\TADA Play
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=TadaPlay-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
SetupIconFile=..\TadaPlay\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The app itself always requests admin (needed for the WireGuard driver), so the installer
; runs elevated too - no point prompting twice, and per-machine install fits an admin-only app.
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên Desktop"; GroupDescription: "Biểu tượng bổ sung:"

[Files]
; Excludes Log: running the app straight out of the publish folder (which is how a build gets
; smoke-tested) leaves a tadaplay.log there. Without this it would be packaged and shipped to
; every user, and worse, ISCC fails outright with a sharing violation whenever that test
; instance is still running.
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Excludes: "Log,*.log"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Gỡ cài đặt {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; The app itself sets up this same Scheduled Task on first launch (see
; AppContext.SetRunOnStartupSetting) - create it here too so autostart is active immediately
; after install. A plain HKCU "Run" registry entry does NOT work here: the app's manifest
; requires admin, and Windows silently refuses to auto-elevate Run-key entries at logon (no UAC
; prompt is ever shown for them). A Scheduled Task with "Run with highest privileges" is the
; supported way to launch an elevated app at logon without a prompt.
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""TadaPlay"" /TR ""\""{app}\{#MyAppExeName}\"" --minimized"" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden; StatusMsg: "Đang thiết lập khởi động cùng Windows..."
; Match sharing (see LiveShareServer): peers fetch finished matches over the VPN on this
; port. The app adds this rule itself on first run too - doing it here as well means the
; very first launch is never the one that silently fails.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""TadaPlay match sharing"" dir=in action=allow protocol=TCP localport=53755 profile=any"; Flags: runhidden; StatusMsg: "Đang mở cổng chia sẻ trận đấu..."
Filename: "{app}\{#MyAppExeName}"; Description: "Khởi chạy {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""TadaPlay"" /F"; Flags: runhidden; RunOnceId: "DeleteTadaPlayScheduledTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TadaPlay match sharing"""; Flags: runhidden; RunOnceId: "DeleteTadaPlayFirewallRule"
