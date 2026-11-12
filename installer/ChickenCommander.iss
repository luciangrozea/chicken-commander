; Inno Setup script for Chicken Commander.
; Build:  ISCC.exe /DAppVersion=1.0.0 installer\ChickenCommander.iss
; Expects the single-file build in ..\publish\ChickenCommander.exe (see the GitHub Actions workflow).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "Chicken Commander"
#define AppExe "ChickenCommander.exe"
#define AppUrl "https://github.com/luciangrozea/chicken-commander"

[Setup]
; The AppId identifies the app for upgrades and uninstall: never change it.
AppId={{3F7C7572-E534-4511-AE33-8F177D4FD202}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=luciangrozea
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Installs for the current user without admin rights; the first page offers "for all users" too.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\src\ChickenCommander\Assets\chicken.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\artifacts
OutputBaseFilename=ChickenCommander-Setup-{#AppVersion}
; Close a running copy before upgrading, and start it again afterwards.
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Settings in %AppData%\ChickenCommander are kept on uninstall, so a reinstall remembers tabs, hotlist and connections.
