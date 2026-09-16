# TOR POS R85 – Fehlermeldungen: Kassierer-Text von Techniker-Details getrennt

## Kontext
Bislang landete an vielen Stellen in `MainWindow.axaml.cs` die rohe
.NET-Exception-Message direkt im `ScannerStatus`-Text, den die Kassiererin
oder der Kassierer sieht - z. B. bei fehlgeschlagenem Artikelimport,
Etikettendruck, Datenbank-Backup oder Berichtsdruck. Das ist für den
Tagesbetrieb weder verständlich noch hilfreich, und exponiert unnötig
interne Details (Dateipfade, SQL-Fehlercodes, Stacktrace-Fragmente) auf dem
Kassenbildschirm.

## Änderung
1. Neuer Helper `ShowOperationalError(category, ex, printerRelated)` in
   `MainWindow.axaml.cs` - ersetzt rund 20 direkte
   `ScannerStatus.Text = "... " + ex.Message`-Zuweisungen. Der Helfer ruft
   die bereits vorhandene `ReportOperationalError`-Pipeline auf (die die
   volle Exception inkl. Stacktrace ins `CrashLog` schreibt und eine
   Fehler-ID vergibt) und zeigt der Kassiererin nur noch
   `"<KATEGORIE> FEHLGESCHLAGEN · Fehler-ID <ID>"` an - betrifft u. a.
   BON EIN/AUS, ETIKETTEN, ARTIKELIMPORT/-EXPORT, DATENBANKIMPORT,
   BESTELLÜBERSICHT, DATENSICHERUNG, BERICHT (alle Berichtsmenüpunkte über
   den gemeinsamen `ShowReportAsync`-Helfer), KASSENSTURZ, EXPORT
   BUCHUNGSDATEN, PROGRAMMIERUNGSPROTOKOLL, FISKAL-PRÜFUNG, GDPDU-TOOLS,
   DSFINV-K, TSE EXPORT, FISKALSTATUS, UPDATE. Drei Stellen, die
   Exception-Details nur in den internen `pos_action_log`-Audit-Trail
   schreiben (nicht auf dem Bildschirm), sowie zwei bereits sauber über
   `ReportOperationalError` laufende Stellen (Bon-Druck, Bon-Historie-
   Reprint) blieben unverändert - dort war die Trennung bereits korrekt.
2. Neue Methode `CrashLog.FindErrorId(string errorId)` in
   `StartupDiagnostics.cs`: durchsucht alle Session-Logs
   (`TOR-POS-*.log`) neueste zuerst nach `ERROR-ID=<id>` und liefert den
   vollständigen Block (Fehlermeldung + Exception-Typ + Stacktrace) bis
   zur nächsten Logzeile zurück - ohne dass ein Techniker Logdateien von
   Hand durchsuchen muss.
3. Neuer Abschnitt "TECHNIKER-DETAILS ZU EINER FEHLER-ID" im
   Diagnose-Fenster (`DiagnosticsWindow.cs`): Eingabefeld für die
   Fehler-ID (z. B. `20260914-153000-AB12`), Suche per Klick oder Enter,
   Ergebnis in Monospace-Schrift.

## Ergebnis
- Kassenbildschirm zeigt ab sofort ausschließlich kurze, verständliche
  Statusmeldungen mit Fehler-ID statt roher Exception-Texte.
- Die vollständige technische Information geht nicht verloren, sondern ist
  über das Diagnose-Fenster gezielt nach Fehler-ID abrufbar - weiterhin nur
  lokal auf dem Kassenrechner, keine Übertragung nach außen.
- 4 neue Prüfungen in `R85ReviewTests.cs` (Fehler-ID-Treffer inkl.
  Blockabgrenzung zur nächsten Logzeile, kein Treffer bei unbekannter ID,
  Ablehnung einer leeren Suche, Groß-/Kleinschreibung wird ignoriert).
  Sicherheits-Testsuite: **438/438 PASS** (vorher 434, keine Regression).
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine UI-/Diagnose-Änderung ohne Bezug zu Fiskalisierung, TSE-Signierung
oder Zahlungsabwicklung. Beide Fiskal-Sperren
(`FiscalRelease.Enabled=false`,
`FiscalComplianceService`-Readiness-Flags) unverändert `false`.
