; ClipFx Share installer (Inno Setup 6)
#define AppName "ClipFx Share"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{B1F4C0F7-6C2E-4C1B-9B1E-3A7D2E9C5F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ClipFx
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ClipFxShareSetup-{#AppVersion}
SetupIconFile=..\assets\clipfx-share.ico
UninstallDisplayIcon={app}\ClipFxShare.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\ClipFxShare.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ClipFxShare.exe"; Tasks: desktopicon
; Explorer: right-click > Send to > ClipFx Share
Name: "{usersendto}\{#AppName}"; Filename: "{app}\ClipFxShare.exe"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\ClipFxShare.exe"" --background"; Flags: uninsdeletevalue

[Run]
; Allow incoming transfers on every network type (hotspots are usually "Public").
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\ClipFxShare.exe"" enable=yes profile=any protocol=TCP localport=47630"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\ClipFxShare.exe"" enable=yes profile=any protocol=UDP localport=47631"; Flags: runhidden waituntilterminated
Filename: "{app}\ClipFxShare.exe"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden waituntilterminated; RunOnceId: "FirewallRule"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im ClipFxShare.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
