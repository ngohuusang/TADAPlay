; Inno Setup script for TADA Play.
; Build the publish output first:
;   dotnet publish TadaPlay/TadaPlay.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -p:PublishSingleFile=false -o publish/TadaPlay
; Then compile this script with ISCC.exe (Inno Setup 6).

#define MyAppName "TADA Play"
#define MyAppVersion "3.37.1"
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
;
; Delete before add, so the pair is idempotent. `netsh ... add rule` appends unconditionally
; and never merges by name, so re-running the installer - which every upgrade does - stacked
; another identical copy of this rule each time; a machine upgraded a few times ends up with
; a dozen of them. The delete removes ALL rules carrying this name, so it both prevents new
; duplicates and clears out any left behind by earlier installers. On a first install it
; matches nothing and exits non-zero, which is harmless: Inno only reports [Run] entries that
; fail to *start*, not ones returning an error code. (The app's own EnsureFirewallRule
; already checks before adding, so it never contributed duplicates and needs no change.)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TadaPlay match sharing"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""TadaPlay match sharing"" dir=in action=allow protocol=TCP localport=53755 profile=any"; Flags: runhidden; StatusMsg: "Đang mở cổng chia sẻ trận đấu..."
; No "launch now" step. Setup runs elevated, so anything it starts inherits that - which means
; the app would run as whoever answered the UAC prompt rather than as the player. On a PC where
; those are different accounts it comes up against the wrong registry hive: no saved login, no
; game folder, and errors that look like missing permissions. The player starts it themselves
; from the desktop icon, or it comes up at the next logon through the scheduled task above.
; The auto-updater runs this installer silently and then has to get the app back up, but the
; entry above is skipifsilent by design (a silent install triggered by anything else should not
; spawn a window). So the updater passes /RELAUNCH and gets exactly one relaunch from this line.
; runasoriginaluser matters: the updater is elevated, and without it the app would be restarted
; as whatever elevated identity ran the installer rather than as the player.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WantsRelaunch

[InstallDelete]
; The DirectPlay lobby helper from the Sep 2026 "join straight into the game" builds. Those
; builds extracted it from an embedded resource into this folder at run time, so no installer
; ever recorded it, and the feature has since been removed - but the 9 MB file is still sitting
; on every PC that ran them. Removed on update so it does not linger forever.
Type: files; Name: "{app}\TadaLobby.exe"

[UninstallDelete]
; Written by the app while it runs, so the uninstaller has no record of them and would leave the
; folder behind - which looks exactly like an uninstall that failed.
Type: filesandordirs; Name: "{app}\Log"
Type: filesandordirs; Name: "{app}\Logs"
; See [InstallDelete]: extracted at run time by older builds, so equally unknown to the uninstaller.
; This was the one file keeping "C:\Program Files\TADA Play" alive after an uninstall.
Type: files; Name: "{app}\TadaLobby.exe"
; Last, and only if empty. This section runs as the final step of an uninstall - AFTER the
; uninstaller has already tried to remove the folder and found it still holding the log. So the
; entries above empty it too late for that attempt, and without this line an empty
; "C:\Program Files\TADA Play" is left behind. dirifempty rather than a forced delete, so
; anything a player put in there themselves is never taken with it.
Type: dirifempty; Name: "{app}"

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""TadaPlay"" /F"; Flags: runhidden; RunOnceId: "DeleteTadaPlayScheduledTask"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TadaPlay match sharing"""; Flags: runhidden; RunOnceId: "DeleteTadaPlayFirewallRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""TadaPlay VPN ping"""; Flags: runhidden; RunOnceId: "DeleteTadaPlayPingRule"

[Code]
const
  AppProcess = 'TadaPlay.exe';

// Whether TADA Play is running. Asked of WMI rather than looked for as a window, because the app
// normally has no visible window at all: it starts with Windows hidden in the tray, and closing
// its window only hides it there. A failed query answers False - CloseRunningApp still sends the
// kill regardless, so the only thing lost is the wait for it to finish.
function IsAppRunning(): Boolean;
var
  Locator, Service, Found: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Found := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''' + AppProcess + '''');
    Result := Found.Count > 0;
  except
    Result := False;
  end;
end;

// Ends TADA Play so its files can be replaced or removed.
//
// Without this, installing or uninstalling while the app sat in the tray - which is most of the
// time, since it starts with Windows - failed on the first DLL it loads ("DeleteFile failed;
// code 5. Access is denied" on Accessibility.dll). The in-app updater had the same problem the
// other way round: it starts this installer and then exits, and a silent install begins copying
// at once, while the app is still taking its VPN and lobby connection down.
//
// Forced (/F), because asking does not work: a close request only hides the app to the tray, by
// design. Deliberately NOT a tree kill (/T): the app starts the game as a child process, and
// taking the tree down would end a player's running game with it.
procedure CloseRunningApp();
var
  ResultCode, Waited: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM ' + AppProcess, '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  // The lobby helper older builds ran alongside the app (see [InstallDelete]). Current builds
  // never start it, but a PC still on one of those builds may have it running, and a running exe
  // cannot be deleted. It is our own helper, not the game, so ending it is safe.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM TadaLobby.exe', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);

  // taskkill returns once the kill is requested, not once Windows has let go of the files.
  Waited := 0;
  while IsAppRunning() and (Waited < 10000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

// Runs before any file is copied, in the wizard and in a silent (updater) install alike.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  // Someone running the wizard by hand is told first: closing the app drops its VPN, which ends
  // any online game in progress. The updater's silent install needs no warning - the player has
  // just chosen to update, and the app is already on its way out.
  if IsAppRunning() and not WizardSilent() then
  begin
    if MsgBox('TADA Play đang chạy và sẽ được đóng để cập nhật.' + #13#10 +
              'Nếu đang trong trận đấu, trận sẽ bị ngắt kết nối.' + #13#10#13#10 +
              'Tiếp tục?', mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := 'Hãy thoát TADA Play rồi chạy lại trình cài đặt.';
      Exit;
    end;
  end;

  CloseRunningApp();

  if IsAppRunning() then
    Result := 'Không đóng được TADA Play. Hãy thoát ứng dụng (chuột phải biểu tượng ở khay hệ thống, chọn Thoát) rồi thử lại.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  if IsAppRunning() and not UninstallSilent() then
  begin
    if MsgBox('TADA Play đang chạy và sẽ được đóng để gỡ cài đặt.' + #13#10#13#10 +
              'Tiếp tục?', mbConfirmation, MB_OKCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;

  CloseRunningApp();
end;

// True when the installer was started with /RELAUNCH, which only the in-app updater does.
// Written as a plain command-line scan rather than {param:...} so it behaves the same however
// the parameter is quoted.
function WantsRelaunch(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;
