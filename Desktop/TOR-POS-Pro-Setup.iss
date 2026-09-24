#ifndef MyAppName
  #define MyAppName "TOR POS Pro"
#endif
#ifndef MyAppId
  #define MyAppId "{{7F8D13C7-86D8-4A3B-A44A-0C20E8E5931B}"
#endif
#ifndef MyDefaultDirName
  #define MyDefaultDirName "TOR POS Pro"
#endif
#ifndef MyDefaultGroupName
  #define MyDefaultGroupName "TOR POS Pro"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "TorPos.App.exe"
#endif
#ifndef MyAppMutex
  #define MyAppMutex "TOR-POS-Pro-Running"
#endif
#ifndef MyDataDirName
  #define MyDataDirName "TOR-POS-Pro"
#endif
#ifndef MyProcessName
  #define MyProcessName "TorPos.App"
#endif
#ifndef MyProductEdition
  #define MyProductEdition ""
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "TOR-POS-Pro-Setup"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "publish\win-x64"
#endif
#define MyAppVersion "0.7.33.882"
#define MyAppReleaseName "Merd-D"
#define MyAppPublisher "TOR Kassensysteme"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppReleaseName}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoCopyright=(c) {#MyAppPublisher}
DefaultDirName={autopf}\{#MyDefaultDirName}
UsePreviousAppDir=yes
DefaultGroupName={#MyDefaultGroupName}
DisableProgramGroupPage=yes
LicenseFile=NUTZUNGSBEDINGUNGEN-DE.txt
UninstallDisplayName={#MyAppName} {#MyAppReleaseName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=installer-output
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile=src\TorPos.App\Assets\TorPos.ico
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
DisableWelcomePage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
SetupLogging=yes
CloseApplications=no
RestartApplications=no
AppMutex={#MyAppMutex}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Dirs]
Name: "{commonappdata}\{#MyDataDirName}"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} starten"
Name: "{commonprograms}\{#MyDefaultGroupName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppName} starten"

[InstallDelete]
Type: files; Name: "{userdesktop}\{#MyAppName}.lnk"
Type: files; Name: "{commondesktop}\{#MyAppName}.lnk"
Type: files; Name: "{autodesktop}\{#MyAppName}.lnk"
Type: files; Name: "{userprograms}\{#MyDefaultGroupName}\{#MyAppName}.lnk"
Type: files; Name: "{commonprograms}\{#MyDefaultGroupName}\{#MyAppName}.lnk"
Type: files; Name: "{autoprograms}\{#MyDefaultGroupName}\{#MyAppName}.lnk"
Type: dirifempty; Name: "{userprograms}\{#MyDefaultGroupName}"
Type: dirifempty; Name: "{commonprograms}\{#MyDefaultGroupName}"
Type: dirifempty; Name: "{autoprograms}\{#MyDefaultGroupName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "{#MyAppName} starten"; Flags: nowait postinstall skipifsilent shellexec

[Code]
var
  EditionPage: TInputOptionWizardPage;
  ExistingEdition: String;
  ExistingInstallation: Boolean;

function EditionLockPath(): String;
begin
  Result := ExpandConstant('{userappdata}\{#MyDataDirName}\edition.lock');
end;

function FixedProductEdition(): String;
begin
  Result := Uppercase(Trim('{#MyProductEdition}'));
end;

function LegacyPermanentEditionLockPath(): String;
begin
  Result := ExpandConstant('{userappdata}\TOR-POS-Pro\edition.permanent.lock');
end;

function ReadLegacyPermanentEdition(): String;
var
  S: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(LegacyPermanentEditionLockPath(), S) then
    Result := Uppercase(Trim(String(S)));
end;

function LegacyMatchesFixedEdition(): Boolean;
var
  FixedEdition: String;
begin
  FixedEdition := FixedProductEdition();
  Result := (FixedEdition <> '') and (ReadLegacyPermanentEdition() = FixedEdition);
end;

function ReadExistingEdition(): String;
var
  S: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(EditionLockPath(), S) then
    Result := Trim(String(S));
end;

function SelectedEdition(): String;
begin
  if EditionPage.Values[0] then Result := 'KIOSK'
  else if EditionPage.Values[1] then Result := 'IMBISS'
  else Result := '';
end;

procedure InitializeWizard;
begin
  ExistingEdition := Uppercase(ReadExistingEdition());
  ExistingInstallation := FileExists(ExpandConstant('{autopf}\{#MyDefaultDirName}\{#MyAppExeName}')) or FileExists(ExpandConstant('{localappdata}\Programs\{#MyDefaultDirName}\{#MyAppExeName}'));
  EditionPage := CreateInputOptionPage(wpSelectDir, 'TOR POS Version', 'Welche Version soll installiert werden?', 'Die Kassenart wird beim ersten Start direkt im TOR POS Anmeldefenster ausgewählt.', True, False);
  EditionPage.Add('EINZELHANDEL – Kiosk / Spätkauf / Markt / Blumen / Friseur / Schneiderei / Shop');
  EditionPage.Add('GASTRONOMIE – Döner / Imbiss / Restaurant / Café / Bäckerei / Bar / Foodtruck');
  if ExistingInstallation and ((ExistingEdition = 'KIOSK') or (ExistingEdition = 'IMBISS')) then begin
    EditionPage.Values[0] := ExistingEdition = 'KIOSK'; EditionPage.Values[1] := ExistingEdition = 'IMBISS'; EditionPage.CheckListBox.Enabled := False;
    EditionPage.SubCaptionLabel.Caption := 'Aktuell ist ' + ExistingEdition + ' installiert und an die Edition-Lizenz gebunden. Ein Update ändert diese Auswahl nicht.';
  end else begin
    EditionPage.Values[0] := False; EditionPage.Values[1] := False; EditionPage.CheckListBox.Enabled := True;
    EditionPage.SubCaptionLabel.Caption := 'Bitte EINZELHANDEL oder GASTRONOMIE ausdrücklich auswählen. Es ist keine Version vorausgewählt.';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := PageID = EditionPage.ID;
end;

{ The running-app check uses the mutex every TOR build creates at start
  (ProductBuild.RunningMutexName) instead of a hidden shell process.
  A setup that silently launches a script host is exactly the pattern browser
  download scanners and antivirus heuristics classify as malicious. }
function TorPosProcessRunning(): Boolean;
begin
  Result := CheckForMutexes('{#MyAppMutex}');
end;

function LegacyTorPosProcessRunning(): Boolean;
begin
  Result := LegacyMatchesFixedEdition() and CheckForMutexes('TOR-POS-Pro-Running');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if TorPosProcessRunning() or LegacyTorPosProcessRunning() then Result := 'TOR POS ist noch geöffnet.' + #13#10 + #13#10 + 'Bitte die laufende TOR-POS-Anwendung normal schließen und kurz warten, bis sie vollständig beendet ist. Danach die Installation erneut starten.' + #13#10 + #13#10 + 'Bei einer R181-Datenübernahme wird die alte Installation niemals im laufenden Betrieb kopiert.';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = EditionPage.ID then if not EditionPage.Values[0] and not EditionPage.Values[1] then begin MsgBox('Bitte wählen Sie zuerst EINZELHANDEL oder GASTRONOMIE. Ohne Auswahl kann die Installation nicht fortgesetzt werden.', mbInformation, MB_OK); Result := False; Exit; end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var DataDir: String;
begin
  { R182: the till ships with its documented access - admin / admin, staff PIN
    1234, training code 0000 - so setup no longer asks for credentials and no
    bootstrap credential file is written. The operator changes them later in
    the Benutzerverwaltung. }
  if CurStep = ssPostInstall then begin
    DataDir := ExpandConstant('{userappdata}\{#MyDataDirName}'); if not DirExists(DataDir) then ForceDirectories(DataDir);
  end;
end;