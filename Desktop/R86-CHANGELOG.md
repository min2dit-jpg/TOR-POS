# TOR POS R86 – Direkter Bonschnitt & Kassenschublade (StarPRNT-Rohbefehle)

## Kontext
Der Bondruck lief bisher ausschließlich über den Windows-GDI-Druckertreiber
(`PrintDocument`). Schnitt und Kassenschublade hingen damit vollständig von
der Treiberkonfiguration ab, die TOR POS selbst nicht steuerte - siehe
`STAR-MCP31CBI-INTEGRATION.md`, "Phase 2: direct cut command / direct
cash-drawer command". Ein echtes Star-SDK (StarIO10) liegt nicht vor und
wird - wie schon beim Swissbit-SDK - nicht von TOR gebündelt oder
vorausgesetzt.

## Änderung
Schnitt- und Schubladenbefehl werden jetzt als eigener RAW-Druckauftrag
direkt an den Windows-Drucker-Spooler gesendet (`WritePrinter` mit
Datentyp `RAW`, neue Klasse `RawPrinterIo` in TorPos.Infrastructure) -
unmittelbar nach dem GDI-Bondruck, kein Vendor-SDK nötig:
- **Schnitt**: StarPRNT/ESC-POS-Standardbefehl `GS V` (`TorPos.Core.StarPrntRawCommands.PartialCut`).
- **Kassenschublade**: StarPRNT/ESC-POS-Standardbefehl `ESC p` (`OpenCashDrawer(pin, onMs, offMs)`).

Beide Befehle sind Teil des dokumentierten StarPRNT-Befehlssatzes des
mC-Print3, nicht proprietär - deshalb ohne StarIO10-SDK umsetzbar. Ein
fehlschlagender Rohbefehl blockiert nie den eigentlichen Bondruck (analog
zum bestehenden Logo-/QR-Fallback-Muster): der Bon ist bereits gedruckt,
ein ausbleibender Schnitt/Schubladen-Impuls ist ein Hardware-Komfort-Thema,
kein Grund, dem Kassierer einen Druckfehler vorzutäuschen.

Neue Felder (alle mit sicherem Default, kein bestehender Aufrufer musste
geändert werden):
- `ReceiptPrintJob.AutoCut` (Default `true`), `.OpenCashDrawer` (Default `false`)
- `ReportPrintJob.AutoCut`, `KitchenPrintJob.AutoCut`, `PickupSlipPrintJob.AutoCut`, `ErrorSlipPrintJob.AutoCut` (alle Default `true`)

`MainWindow.BuildReceiptPrintJob` setzt beide Bon-Felder aus neuen
Einstellungen:
- `printer.auto_cut.enabled` (Default AN)
- `printer.drawer_kick.enabled` (Default AN)

Die Kassenschublade öffnet **ausschließlich** beim Original-Bon einer
Barzahlung (`method==PaymentMethod.Cash && !isCopy`) - nie bei
Kartenzahlung, Testdruck oder Bon-Kopien aus der Bon-Historie. Beide
Schalter in Einstellungen → Bon & Rechnung → Druckverhalten.

## Ergebnis
- 8 neue Prüfungen in `R86ReviewTests.cs`: Befehls-Bytes (Schnitt/Schublade
  inkl. Pin-Auswahl), sichere Defaults auf allen Druckauftragstypen,
  Abschaltbarkeit pro Auftrag, sauberes Fehlschlagen bei fehlendem
  Druckernamen sowie bei einer nicht existierenden Windows-Druckerwarteschlange
  (kein Hängenbleiben). Sicherheits-Testsuite: **446/446 PASS** (vorher 438,
  keine Regression).
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung / offen
Dies schließt zwei der vier "Printer v0.4.2"-Punkte
(`Direct cutter command`, `Direct DK cash-drawer command`). Weiterhin
offen, absichtlich nicht Teil dieser Änderung:
- **Direct StarPRNT SDK status** / **Paper-out monitoring**: der Windows-
  Spooler unterstützt für einen RAW-Auftrag kein zuverlässiges
  synchrones Rücklesen (`ReadPrinter`) über alle Treiber/Transportarten
  hinweg; ein belastbarer Live-Status (Papier/Abdeckung/Schneider)
  bräuchte entweder das echte StarIO10-SDK oder eine direkte
  Port-Anbindung (USB/LAN) unter Umgehung des Spoolers - beides über den
  Rahmen dieser Änderung hinaus.
- Die Byte-Werte sind nach StarPRNT-Befehlsreferenz-Dokumentation korrekt,
  aber **nicht gegen die reale MCP31CBI verifiziert** - das ist Teil des
  als Nächstes anstehenden Hardware-Abnahmetests.

Reine Drucker-/UX-Änderung ohne Bezug zu Fiskalisierung, TSE-Signierung
oder Zahlungsabwicklung. Beide Fiskal-Sperren unverändert `false`.
