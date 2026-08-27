; GBEX Depo Ajanı — Windows kurulum paketi (Inno Setup)
;
; Inno Setup 6 is preinstalled on GitHub's windows-latest runner image —
; no extra tooling install needed in CI. Turkish.isl ships with Inno Setup
; itself, so the installer UI is Turkish by default with no extra download.
;
; Source files come from the self-contained win-x64 `dotnet publish` output
; (see .github/workflows/ci.yml) — this script does not build the app
; itself, only packages the already-published output.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{6E2B9C2E-6B0E-4E3B-9C7A-2B8B6C0F1A11}
AppName=GBEX Depo Ajanı
AppVersion={#AppVersion}
AppPublisher=GBEX
AppPublisherURL=https://gbex.com.tr
DefaultDirName={autopf}\GbexWarehouseAgent
DefaultGroupName=GBEX Depo Ajanı
DisableProgramGroupPage=yes
OutputBaseFilename=GbexWarehouseAgentSetup
OutputDir=..\installer-output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\GbexWarehouseAgent.exe
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\GbexWarehouseAgent\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\GBEX Depo Ajanı"; Filename: "{app}\GbexWarehouseAgent.exe"
Name: "{group}\{cm:UninstallProgram,GBEX Depo Ajanı}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\GBEX Depo Ajanı"; Filename: "{app}\GbexWarehouseAgent.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GbexWarehouseAgent.exe"; Description: "{cm:LaunchProgram,GBEX Depo Ajanı}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Application files only — deliberately does NOT touch
; %LOCALAPPDATA%\GbexWarehouseAgent (settings, the DPAPI-encrypted station
; secret, the outbox database, logs). Removing the station credential is a
; separate, explicit action in the app itself ("Kimlik Bilgisini Kaldır")
; so an uninstall never silently destroys a working configuration an
; operator might reinstall later.
Type: filesandordirs; Name: "{app}"
