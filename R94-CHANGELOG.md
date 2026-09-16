# TOR POS R94 – ZVT Terminal-Tagesabschluss (End-of-Day)

## Kontext
Beim Vergleich der ZVT-SDK-Fähigkeiten mit dem, was TOR bisher anbindet,
war `Portalum.Zvt 3.4.0`s `ZvtClient.EndOfDayAsync` (ZVT-Kommando 06 50 -
fordert das Terminal auf, seinen gespeicherten Tagesumsatz an den
Netzbetreiber zu übertragen) bisher ungenutzt. Anders als die zuvor
zweimal bewusst zurückgestellte Karten-Storno/Retoure-Erweiterung ist ein
Tagesabschluss kein neuer Buchungsweg und keine Rückbuchung - er betrifft
ausschließlich die Abstimmung des Terminals mit dem Netzbetreiber, nicht
TORs eigene Fiskaldaten. Auf ausdrücklichen Wunsch umgesetzt.

## Änderung
- `IPaymentTerminalService.EndOfDayAsync` (neu, `TorPos.Core`) und dessen
  Implementierung in `ZvtPaymentTerminalService` - Aufbau identisch zu
  `RegisterAsync`: verbinden, Timeout, ein ZVT-Kommando senden, Ergebnis
  auswerten. Kein Bezug zu `CheckoutJournal` oder dem
  `FiscalRelease`-Gate, da kein Zahlungsvorgang stattfindet.
- Eigene, von der Verbindungsprüfung (`VERBINDUNG TESTEN`/`ZVT ANMELDUNG
  TESTEN`) getrennte Ablage: Settings-Schlüssel
  `payment.terminal.end_of_day.last_at/last_status/last_error` und
  Audit-Event `PAYMENT_TERMINAL_END_OF_DAY` - ein Tagesabschluss
  überschreibt nie den an anderer Stelle angezeigten Verbindungsstatus.
- Neuer Button "TERMINAL-TAGESABSCHLUSS" in Einstellungen → Zahlarten →
  Kartenterminal, mit eigenem Infobereich und eigener
  Status-/Fehleranzeige. Erläuternder Hinweistext macht klar: das ist der
  Terminal-eigene Abschluss beim Netzbetreiber, kein TOR-Z-Bericht/
  Kassenabschluss.

## Ergebnis
- 5 neue Prüfungen in `R94ReviewTests.cs`: verweigert ohne aktivierte
  Terminal-Integration, verweigert ohne konfigurierte IP (vor jedem
  Netzwerkversuch), scheitert bei nicht erreichbarem Terminal sauber
  (kein Hängenbleiben/Absturz), das tatsächliche Versuchsergebnis wird
  unter den eigenen Settings-Schlüsseln abgelegt, und der bestehende
  Verbindungsstatus-Schlüssel bleibt dabei unverändert.
  Sicherheits-Testsuite: **471/471 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Terminal-Diagnosefunktion ohne Bezug zu TSE-Signierung, Z-Bericht
oder Buchung - beide Fiskal-Sperren unverändert `false`. Schließt den
Roadmap-Punkt "Terminal end-of-day UI" (Payment Terminal v0.6.1).
