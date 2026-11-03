; AgentZero Lite — Inno Setup script for the OFFICIAL Windows build: the Avalonia host
; (Project/AgentZeroAvalonia), with the wearable host under wearable\.
; Invoked from CI with: iscc /DAppVersion="x.y.z" installer/AgentZeroLite.iss
; (the workflow strips the leading "v" from the tag before passing /DAppVersion)
;
; The AppId is the one the WPF installer used up to v0.25.3, on purpose: installing this
; over an existing WPF install upgrades it in place (same folder, same Start Menu entry,
; one uninstaller). The WPF host is now a reference build with its own script and AppId —
; installer/AgentZeroLite-WPF.iss.

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
; Both GUIs hold this mutex while running; Setup asks the user to close the app first.
AppMutex=Local\AgentZeroLite.SingleInstance

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
; Terminals render in WebView2. Older Windows 10, LTSC/Server and managed images may lack the
; runtime; when it is missing, the wizard downloads Microsoft's Evergreen bootstrapper (see
; NextButtonClick) and it runs here, before the app can be launched. Without elevation it
; installs per-user. A silent install skips the wizard pages, so the app's own banner
; (Install WebView2 runtime) remains the fallback.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing the Microsoft Edge WebView2 runtime..."; Check: NeedWebView2Install; Flags: waituntilterminated
Filename: "{app}\AgentZeroLite.exe"; Description: "Launch AgentZero Lite"; Flags: nowait postinstall skipifsilent

[Code]
// ── WebView2 runtime ─────────────────────────────────────────────────────────
// Same rule as ZeroCommon's WebView2Runtime.Probe: the Evergreen client's "pv" in HKLM
// (WOW6432Node on 64-bit) or HKCU; missing, empty or 0.0.0.0 means not installed.
const
  WebView2Client = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  DownloadPage: TDownloadWizardPage;

function WebView2VersionUsable(Root: Integer; Key: String): Boolean;
var
  Pv: String;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0');
end;

function WebView2Installed(): Boolean;
begin
  Result :=
    WebView2VersionUsable(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    WebView2VersionUsable(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2Client) or
    WebView2VersionUsable(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\' + WebView2Client);
end;

function NeedWebView2Install(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe')) and not WebView2Installed();
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not WebView2Installed() then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(WebView2Bootstrapper, 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        // Not fatal: the app shows "Install WebView2 runtime" on its terminal tabs.
        SuppressibleMsgBox('The Microsoft Edge WebView2 runtime could not be downloaded (' + GetExceptionMessage + ').' + #13#10 +
          'AgentZero Lite will offer to install it when it starts.', mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

// ── previous install ─────────────────────────────────────────────────────────
// Remove the previous install's files before copying the new ones. An upgrade from the
// WPF build would otherwise leave the WPF host's assemblies beside the Avalonia ones
// (harmless to .NET, which loads what deps.json names, but hundreds of MB of dead files).
// The uninstaller removes only what it installed: the database, settings and models live
// under %LOCALAPPDATA%\AgentZeroLite and are untouched, so both hosts keep reading them.
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{8E2D1B3C-9F7A-4D22-A9B6-AZLITE000001}_is1';

function PreviousUninstaller(): String;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Result) then
    RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', Result);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Cmd: String;
  Code: Integer;
begin
  if CurStep = ssInstall then
  begin
    Cmd := RemoveQuotes(PreviousUninstaller());
    if (Cmd <> '') and FileExists(Cmd) then
      Exec(Cmd, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;
