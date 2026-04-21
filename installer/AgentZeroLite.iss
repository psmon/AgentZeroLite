; AgentZero Lite — Inno Setup script
; Invoked from CI with: iscc /DAppVersion="x.y.z" installer/AgentZeroLite.iss
; (the workflow strips the leading "v" from the tag before passing /DAppVersion)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{8E2D1B3C-9F7A-4D22-A9B6-AZLITE000001}
AppName=AgentZero Lite
AppVersion={#AppVersion}
AppPublisher=psmon
AppPublisherURL=https://github.com/psmon/AgentZeroLite
AppSupportURL=https://github.com/psmon/AgentZeroLite/issues
AppUpdatesURL=https://github.com/psmon/AgentZeroLite/releases
DefaultDirName={autopf}\AgentZeroLite
DefaultGroupName=AgentZero Lite
OutputDir=..\output
OutputBaseFilename=AgentZeroLite-v{#AppVersion}-Setup
SetupIconFile=..\Project\AgentZeroWpf\agentzero.ico
UninstallDisplayIcon={app}\AgentZeroLite.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "korean";  MessagesFile: "compiler:Languages\Korean.isl"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\AgentZero Lite"; Filename: "{app}\AgentZeroLite.exe"
Name: "{group}\Uninstall AgentZero Lite"; Filename: "{uninstallexe}"
Name: "{autodesktop}\AgentZero Lite"; Filename: "{app}\AgentZeroLite.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\AgentZeroLite.exe"; Description: "Launch AgentZero Lite"; Flags: nowait postinstall skipifsilent
