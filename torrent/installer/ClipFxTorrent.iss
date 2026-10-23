; ClipFx Torrent installer (Inno Setup 6)
#define AppName "ClipFx Torrent"
#define Exe "ClipFxTorrent.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E2D4B7A-91C3-4F0E-8B5D-2C7A9E1F3D44}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ClipFx
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ClipFxTorrentSetup-{#AppVersion}
SetupIconFile=..\assets\clipfx-torrent.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
ChangesAssociations=yes
MinVersion=10.0.17763

[Tasks]
Name: "assoc"; Description: "Open magnet links and .torrent files with {#AppName}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#Exe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#Exe}"; Tasks: desktopicon

[Registry]
; ProgIDs and capabilities so Windows lists the app under Default apps.
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.magnet"; ValueType: string; ValueData: "Magnet link"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.magnet\DefaultIcon"; ValueType: string; ValueData: """{app}\{#Exe}"",0"
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.magnet\shell\open\command"; ValueType: string; ValueData: """{app}\{#Exe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.torrent"; ValueType: string; ValueData: "Torrent file"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.torrent\DefaultIcon"; ValueType: string; ValueData: """{app}\{#Exe}"",0"
Root: HKA; Subkey: "Software\Classes\ClipFxTorrent.torrent\shell\open\command"; ValueType: string; ValueData: """{app}\{#Exe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\.torrent\OpenWithProgids"; ValueType: none; ValueName: "ClipFxTorrent.torrent"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\ClipFx\ClipFx Torrent\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\ClipFx\ClipFx Torrent\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Torrent downloader"
Root: HKA; Subkey: "Software\ClipFx\ClipFx Torrent\Capabilities\URLAssociations"; ValueType: string; ValueName: "magnet"; ValueData: "ClipFxTorrent.magnet"
Root: HKA; Subkey: "Software\ClipFx\ClipFx Torrent\Capabilities\FileAssociations"; ValueType: string; ValueName: ".torrent"; ValueData: "ClipFxTorrent.torrent"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\ClipFx\ClipFx Torrent\Capabilities"; Flags: uninsdeletevalue
; Make it the handler right away when the user ticks the box (Windows still lets them change it later).
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueData: "URL:Magnet link"; Tasks: assoc
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: assoc
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueData: """{app}\{#Exe}"" ""%1"""; Tasks: assoc
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueData: "ClipFxTorrent.torrent"; Tasks: assoc

[Run]
; Allow incoming peer connections on every network type: more peers means faster downloads.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#Exe}"" enable=yes profile=any protocol=TCP"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#Exe}"" enable=yes profile=any protocol=UDP"; Flags: runhidden waituntilterminated
Filename: "{app}\{#Exe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "FirewallRule"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#Exe}"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}\WebView2"

[Code]
// Only remove the magnet/.torrent handlers if they still point at us.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var v: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Software\Classes\magnet\shell\open\command', '', v) and (Pos('ClipFxTorrent.exe', v) > 0) then
      RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\magnet');
    if RegQueryStringValue(HKCU, 'Software\Classes\.torrent', '', v) and (v = 'ClipFxTorrent.torrent') then
      RegDeleteValue(HKCU, 'Software\Classes\.torrent', '');
  end;
end;
