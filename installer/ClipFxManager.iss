; ClipFx Manager installer (Inno Setup 6)
#define AppName "ClipFx Manager"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6F0E4F5C-2B0C-4A57-9E59-7C1F0B6E2A41}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=ClipFx
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=ClipFxManagerSetup-{#AppVersion}
SetupIconFile=..\assets\clipfx.ico
UninstallDisplayIcon={app}\ClipFxManager.exe
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\ClipFxManager.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\ClipFxManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ClipFxManager.exe"; Parameters: "--install"; StatusMsg: "Starting the ClipFx service..."; Flags: runhidden waituntilterminated
Filename: "{app}\ClipFxManager.exe"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\ClipFxManager.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Stop the running service so its files can be replaced during an upgrade.
  if FileExists(ExpandConstant('{app}\ClipFxManager.exe')) then
    Exec(ExpandConstant('{app}\ClipFxManager.exe'), '--stop-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
