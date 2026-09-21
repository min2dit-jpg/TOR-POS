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
; R121: had drifted to 0.7.33.751 while TorRelease.Version was already .820 -
; the installer's "Programme und Features" entry showed a version the app did
; not report. Keep this in step with TorPos.Core.TorRelease.Version.
#define MyAppVersion "0.7.33.881"
; R145: releases carry a name; shown in the wizard and in Programme und Features.
#define MyAppReleaseName "Merd-M"
#define MyAppPublisher "TOR Kassensysteme"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppReleaseName}
AppPublisher={#MyAppPublisher}
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
; R66: future TOR POS versions own this mutex while running. Setup stops early
; instead of waiting until file-copy time and showing a generic locked-file dialog.
AppMutex={#MyAppMutex}

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Dirs]
; Demo identity is machine-wide and intentionally survives a normal uninstall.
; The application writes only a random Trial-ID here; no hardware identifiers.
Name: "{commonappdata}\{#MyDataDirName}"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Eine einzige, eindeutige Verknuepfung. Alte benutzerspezifische Links werden
; vor der Installation entfernt, damit Windows nicht versehentlich eine alte
; LocalAppData-Version startet.
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "TOR POS Pro starten"
Name: "{commonprograms}\{#MyDefaultGroupName}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "TOR POS Pro starten"

[InstallDelete]
; Verknuepfungen aus allen bisherigen Installationsvarianten entfernen.
; Danach legt [Icons] genau die neuen Links auf {app} an.
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
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Description: "TOR POS Pro starten"; Flags: nowait postinstall skipifsilent shellexec

[Code]
var
  EditionPage: TInputOptionWizardPage;
  AdminPage: TInputQueryWizardPage;
  ExistingEdition: String;
  ExistingInstallation: Boolean;
  SecurityAlreadyInitialized: Boolean;

function EditionLockPath(): String;
begin
  Result := ExpandConstant('{userappdata}\{#MyDataDirName}\edition.lock');
end;

function SecurityMarkerPath(): String;
begin
  Result := ExpandConstant('{userappdata}\{#MyDataDirName}\security.initialized');
end;

function BootstrapAdminPath(): String;
begin
  Result := ExpandConstant('{userappdata}\{#MyDataDirName}\first-run-admin.cfg');
end;

function FixedProductEdition(): String;
begin
  Result := Uppercase(Trim('{#MyProductEdition}'));
end;

function LegacyPermanentEditionLockPath(): String;
begin
  Result := ExpandConstant('{userappdata}\TOR-POS-Pro\edition.permanent.lock');
end;

function LegacySecurityMarkerPath(): String;
begin
  Result := ExpandConstant('{userappdata}\TOR-POS-Pro\security.initialized');
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
  Result :=
    (FixedEdition <> '') and
    (ReadLegacyPermanentEdition() = FixedEdition);
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
  if EditionPage.Values[0] then
    Result := 'KIOSK'
  else if EditionPage.Values[1] then
    Result := 'IMBISS'
  else
    Result := '';
end;

function IsFourDigitPin(const S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) = 4;
  if not Result then Exit;

  for I := 1 to Length(S) do
  begin
    if (S[I] < '0') or (S[I] > '9') then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function HexDigit(Value: Integer): String;
begin
  case (Value mod 16) of
    0: Result := '0';
    1: Result := '1';
    2: Result := '2';
    3: Result := '3';
    4: Result := '4';
    5: Result := '5';
    6: Result := '6';
    7: Result := '7';
    8: Result := '8';
    9: Result := '9';
    10: Result := 'A';
    11: Result := 'B';
    12: Result := 'C';
    13: Result := 'D';
    14: Result := 'E';
    15: Result := 'F';
  end;
end;

function Hex4(Value: Integer): String;
begin
  Result :=
    HexDigit((Value div 4096) mod 16) +
    HexDigit((Value div 256) mod 16) +
    HexDigit((Value div 16) mod 16) +
    HexDigit(Value mod 16);
end;

function Utf16Hex(const S: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
    Result := Result + Hex4(Ord(S[I]));
end;

procedure InitializeWizard;
begin
  ExistingEdition := Uppercase(ReadExistingEdition());

  { Edition is locked only for an actual installed TOR POS instance.
    A stale edition.lock after uninstall must not force KIOSK/IMBISS on a fresh install. }
  ExistingInstallation :=
    FileExists(ExpandConstant('{autopf}\{#MyDefaultDirName}\{#MyAppExeName}')) or
    FileExists(ExpandConstant('{localappdata}\Programs\{#MyDefaultDirName}\{#MyAppExeName}'));

  SecurityAlreadyInitialized :=
    (ExistingInstallation and FileExists(SecurityMarkerPath())) or
    (LegacyMatchesFixedEdition() and FileExists(LegacySecurityMarkerPath()));

  EditionPage :=
    CreateInputOptionPage(
      wpSelectDir,
      'TOR POS Version',
      'Welche Version soll installiert werden?',
      'Die Kassenart wird beim ersten Start direkt im TOR POS Anmeldefenster ausgewählt.',
      True,
      False);

  EditionPage.Add('EINZELHANDEL – Kiosk / Spätkauf / Markt / Blumen / Friseur / Schneiderei / Shop');
  EditionPage.Add('GASTRONOMIE – Döner / Imbiss / Restaurant / Café / Bäckerei / Bar / Foodtruck');

  if ExistingInstallation and
     ((ExistingEdition = 'KIOSK') or
      (ExistingEdition = 'IMBISS')) then
  begin
    EditionPage.Values[0] := ExistingEdition = 'KIOSK';
    EditionPage.Values[1] := ExistingEdition = 'IMBISS';
    EditionPage.CheckListBox.Enabled := False;
    EditionPage.SubCaptionLabel.Caption :=
      'Aktuell ist ' + ExistingEdition +
      ' installiert und an die Edition-Lizenz gebunden. Ein Update ändert diese Auswahl nicht.';
  end
  else
  begin
    { Fresh installation: neither edition is preselected.
      The user must make an explicit choice before continuing. }
    EditionPage.Values[0] := False;
    EditionPage.Values[1] := False;
    EditionPage.CheckListBox.Enabled := True;
    EditionPage.SubCaptionLabel.Caption :=
      'Bitte EINZELHANDEL oder GASTRONOMIE ausdrücklich auswählen. Es ist keine Version vorausgewählt.';
  end;

  AdminPage := CreateInputQueryPage(
    EditionPage.ID,
    'Administrator-Zugang',
    'Admin-Passwort und PIN festlegen',
    'TOR POS kann nicht ohne Anmeldung geöffnet werden. Benutzername: admin. Die Startwerte admin / 1234 können übernommen oder geändert werden.');

  AdminPage.Add('Admin-Passwort:', True);
  AdminPage.Add('Passwort wiederholen:', True);
  AdminPage.Add('4-stellige Admin-PIN:', True);
  AdminPage.Add('PIN wiederholen:', True);

  AdminPage.Values[0] := 'admin';
  AdminPage.Values[1] := 'admin';
  AdminPage.Values[2] := '1234';
  AdminPage.Values[3] := '1234';

  if SecurityAlreadyInitialized then
  begin
    AdminPage.Edits[0].Enabled := False;
    AdminPage.Edits[1].Enabled := False;
    AdminPage.Edits[2].Enabled := False;
    AdminPage.Edits[3].Enabled := False;
    if LegacyMatchesFixedEdition() and (not ExistingInstallation) then
      AdminPage.SubCaptionLabel.Caption :=
        'Bestehende R181-Daten wurden für diese Produkt-Edition erkannt. ' +
        'Der vorhandene Admin-Zugang wird bei der ersten sicheren Datenübernahme beibehalten.'
    else
      AdminPage.SubCaptionLabel.Caption :=
        'Admin-Zugang ist auf diesem PC bereits eingerichtet. Ein Update überschreibt das bestehende Passwort und die PIN nicht.';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { KIOSK/IMBISS is selected directly on the TOR POS login panel.
    Existing installations keep their existing edition.lock. }
  Result := PageID = EditionPage.ID;
end;

function TorPosProcessRunning(): Boolean;
var
  ResultCode: Integer;
  PowerShellPath: String;
begin
  Result := False;
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
    Exit;

  if Exec(
       PowerShellPath,
       '-NoProfile -NonInteractive -Command "if (Get-Process -Name {#MyProcessName} -ErrorAction SilentlyContinue) { exit 66 } else { exit 0 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode = 66;
end;

function LegacyTorPosProcessRunning(): Boolean;
var
  ResultCode: Integer;
  PowerShellPath: String;
begin
  Result := False;
  if not LegacyMatchesFixedEdition() then
    Exit;

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if not FileExists(PowerShellPath) then
    Exit;

  if Exec(
       PowerShellPath,
       '-NoProfile -NonInteractive -Command "if (Get-Process -Name TorPos.App -ErrorAction SilentlyContinue) { exit 66 } else { exit 0 }"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode = 66;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';

  { R66.1: do not let Restart Manager scan/close unrelated applications.
    R66+ is protected by AppMutex. For R65 and older, explicitly check
    TorPos.App.exe immediately before file installation. }
  if TorPosProcessRunning() or LegacyTorPosProcessRunning() then
  begin
    Result :=
      'TOR POS ist noch geöffnet.' + #13#10 + #13#10 +
      'Bitte die laufende TOR-POS-Anwendung normal schließen und kurz warten, bis sie vollständig beendet ist. ' +
      'Danach die Installation erneut starten.' + #13#10 + #13#10 +
      'Bei einer R181-Datenübernahme wird die alte Installation niemals im laufenden Betrieb kopiert.';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  { R66: R65 and older do not own the fixed AppMutex yet. Before the final
    installer step, detect their TorPos.App.exe process explicitly. Never
    force-kill a POS process: it must be allowed to perform its normal backup
    and printer shutdown. }
  if (CurPageID = AdminPage.ID) and
     (TorPosProcessRunning() or LegacyTorPosProcessRunning()) then
  begin
    MsgBox(
      'TOR POS ist noch geöffnet.' + #13#10 + #13#10 +
      'Bitte TOR POS normal schließen und warten, bis das Programm vollständig beendet ist. ' +
      'Danach hier erneut auf Weiter klicken.' + #13#10 + #13#10 +
      'Die Installation wird nicht erzwungen, damit keine offene Buchung, Sicherung oder Druckeroperation beschädigt wird.',
      mbInformation, MB_OK);
    Result := False;
    Exit;
  end;

  if CurPageID = EditionPage.ID then
  begin
    if not EditionPage.Values[0] and not EditionPage.Values[1] then
    begin
      MsgBox('Bitte wählen Sie zuerst EINZELHANDEL oder GASTRONOMIE. Ohne Auswahl kann die Installation nicht fortgesetzt werden.', mbInformation, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if (CurPageID = AdminPage.ID) and (not SecurityAlreadyInitialized) then
  begin
    if AdminPage.Values[0] = '' then
    begin
      MsgBox('Das Admin-Passwort darf nicht leer sein.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if AdminPage.Values[0] <> AdminPage.Values[1] then
    begin
      MsgBox('Die Passwörter stimmen nicht überein.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if not IsFourDigitPin(AdminPage.Values[2]) then
    begin
      MsgBox('Die Admin-PIN muss genau 4 Ziffern haben.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    if AdminPage.Values[2] <> AdminPage.Values[3] then
    begin
      MsgBox('Die PIN-Eingaben stimmen nicht überein.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DataDir: String;
  Bootstrap: AnsiString;
begin
  if CurStep = ssPostInstall then
  begin
    DataDir := ExpandConstant('{userappdata}\{#MyDataDirName}');
    if not DirExists(DataDir) then
      ForceDirectories(DataDir);

    { A dedicated KIOSK/DÖNER installer may have found a matching, permanently
      bound R181 installation. In that case do NOT place new bootstrap credentials
      into the target folder before the application performs its backup-first copy.
      Fresh installations still receive the normal first-run bootstrap. }

    if not SecurityAlreadyInitialized then
    begin
      Bootstrap :=
        'username=admin' + #13#10 +
        'password_hex=' + AnsiString(Utf16Hex(AdminPage.Values[0])) + #13#10 +
        'pin=' + AnsiString(AdminPage.Values[2]) + #13#10;

      SaveStringToFile(BootstrapAdminPath(), Bootstrap, False);
    end;
  end;
end;
