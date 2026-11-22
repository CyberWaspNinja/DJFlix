; DJFlix setup: desktop app + hub service. Built in CI with Inno Setup 6.
[Setup]
AppId={{B6E5B0B4-6D0C-4E0B-9A63-2F0D3C5E7A11}
AppName=DJFlix
AppVersion=1.0.0
AppPublisher=DJFlix
DefaultDirName={autopf}\DJFlix
DefaultGroupName=DJFlix
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=DJFlix-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName=DJFlix
WizardStyle=modern
CloseApplications=force

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"
Name: "startup"; Description: "Open DJFlix when I sign in to Windows"; Flags: unchecked

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\DJFlix"; Filename: "{app}\DJFlix.exe"
Name: "{autodesktop}\DJFlix"; Filename: "{app}\DJFlix.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DJFlix"; ValueData: """{app}\DJFlix.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Registers the "DJFlix" service (automatic start, restart on failure), adds the LAN-only firewall rules and starts it.
Filename: "{app}\server\DJFlix.Server.exe"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Installing the DJFlix service..."
Filename: "{app}\DJFlix.exe"; Description: "Open DJFlix"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\server\DJFlix.Server.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
// Stop a running copy before files are replaced on upgrade.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop DJFlix', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DJFlix.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DJFlix.Server.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  Result := '';
end;
