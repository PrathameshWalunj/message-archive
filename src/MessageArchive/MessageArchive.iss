; Message Archive Installer Script
; Inno Setup Script

#define MyAppName "Message Archive"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Prathamesh Walunj"
#define MyAppURL "https://github.com/PrathameshWalunj/message-archive"
#define MyAppExeName "MessageArchive.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; USER-MODE install (no admin)
DefaultDirName={localappdata}\{#MyAppName}
PrivilegesRequired=lowest

DisableProgramGroupPage=yes
AllowNoIcons=yes

LicenseFile=..\..\LICENSE
OutputDir=..\..\installer
OutputBaseFilename=MessageArchive-Setup
SetupIconFile=New_Icon.ico

Compression=lzma
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; FULL publish output (required)
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Optional docs
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\MessageArchive.exe"; Description: "Launch Message Archive"; Flags: nowait postinstall skipifsilent
