; Nocturne Tray - Inno Setup Installer Script
; ============================================
;
; Prerequisites:
;   1. Inno Setup 6.x (https://jrsoftware.org/isinfo.php)
;   2. Publish output at ..\publish\x64\ (run dotnet publish first)
;   3. Run assets\branding\generate-assets.ps1 to generate app.ico (or provide your own)

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "Nocturne Tray"
#define MyAppPublisher "Nocturne"
#define MyAppExeName "Nocturne.Desktop.Tray.exe"
#define MyAppId "{{B7E3F9A1-4D2C-4F8A-9E6B-1A3C5D7F0E2B}"
#define MyProtocol "nocturne-tray"
#define MyAppIcon "app.ico"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=output
OutputBaseFilename=NocturneTray-{#MyAppVersion}-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
SetupLogging=yes
UninstallDisplayName={#MyAppName}
#ifdef MyAppIcon
SetupIconFile={#MyAppIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Register nocturne-tray:// protocol handler (per-user)
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}"; ValueType: string; ValueName: ""; ValueData: "URL:{#MyAppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}\shell"; ValueType: string; ValueName: ""; ValueData: "open"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}\shell\open"; ValueType: string; ValueName: ""; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Classes\{#MyProtocol}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up protocol handler registry keys on uninstall
    RegDeleteKeyIncludingSubkeys(HKCU, 'SOFTWARE\Classes\{#MyProtocol}');
  end;
end;
