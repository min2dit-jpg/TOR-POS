# TOR POS – KassenSichV 2026 Compliance Matrix

Stand: 2026-09-19  
Rechtsquelle: KassenSichV, zuletzt geändert durch Art. 3 der Verordnung vom 14.01.2026.

> Diese Matrix ist eine technische Entwicklungs- und Abnahmehilfe. Sie ist keine
> Rechtsberatung und ersetzt weder die Zertifizierung der TSE noch die reale
> Hardware-/Export-Abnahme.

## § 1 – Elektronische Aufzeichnungssysteme

**Anwendbar:** Ja. TOR POS ist ein elektronisches/computergestütztes Kassensystem.

**Code-Status:** abgedeckt.

## § 2 – Protokollierung digitaler Grundaufzeichnungen

Pflichtpunkte:
- unmittelbarer Start eines neuen Vorgangs,
- Vorgangsbeginn,
- eindeutige/fortlaufende Transaktionsnummer,
- Vorgangsart,
- Vorgangsdaten,
- Zahlungsarten,
- Vorgangsende/-abbruch,
- Prüfwert,
- Seriennummern von Kasse und TSE,
- Signaturzähler.

**TOR-Umsetzung:**
- `TseVorgangService` / `TseVorgangCartTracker`: Vorgang beginnt mit der ersten Position.
- `FiscalProcessData`: `Kassenbeleg-V1`, `Bestellung-V1`, Abbruchdaten.
- `SaleFiscalSigningService`: übernimmt TSE-Rückgabedaten; keine erfundenen Signaturen.
- `CheckoutJournal`: Zahlstatus/UNKNOWN-Recovery verhindert blindes Wiederholen.
- `KassenSichV2026.ValidateTransaction`: executable Code-Check für intern prüfbare §-2-Felder.

**Status:** CODE READY / HARDWARE VALIDATION OFFEN.

## § 3 – Speicherung der Grundaufzeichnungen

Anforderung:
- vollständig,
- unverändert,
- manipulationssicher,
- nichtflüchtig,
- Transaktionsverkettung mit erkennbaren Lücken.

**TOR-Umsetzung:**
- SQLite mit WAL + `synchronous=FULL`,
- append-only/immutable Trigger für fiskalische Nachweisobjekte,
- TSE-Transaktionsnummer/Signaturzähler werden aus der TSE übernommen,
- TSE-Ausfälle werden separat protokolliert,
- TAR-/TSE-Exportpfad vorhanden.

**Wichtig:** SQLite allein ist keine zertifizierte TSE. Die Manipulationssicherheit
der gesetzlich relevanten TSE-Daten muss mit der realen zertifizierten TSE
nachgewiesen werden.

**Status:** CODE READY / PHYSISCHE TSE-KETTE OFFEN.

## § 4 – Einheitliche digitale Schnittstelle

**TOR-Umsetzung:**
- DSFinV-K 2.4 Export,
- Kassen-/TSE-Stammdaten,
- Beleg-, Zahlungs-, Positions-, Storno-/Retoure- und Bestellbezug,
- Export-Preflight.

**Status:** IMPLEMENTIERT / FINALER PRÜFDATENSATZ NOCH ZU VALIDIEREN.

Die zentrale Freigabe `FiscalRelease.DsfinvkValidated` bleibt bis dahin bewusst `false`.

## § 5 – Anforderungen an die TSE

Die technischen Anforderungen an Sicherheitsmodul, Speichermedium,
Schnittstelle, Schlüssel und Zertifikate werden vom BSI festgelegt.

**TOR-Umsetzung:**
- direkte Swissbit-WORM/TSE-Anbindung,
- SDK/TSE nicht im Repository gebündelt,
- TSE-Werte werden vom Gerät übernommen,
- Ausfall statt erfundener Signatur.

**Status:** SOFTWARE-ANBINDUNG VORBEREITET / ZERTIFIZIERTE KONKRETE TSE + E2E OFFEN.

## § 6 – Anforderungen an den Beleg

Mindestens:
- Unternehmername + Anschrift,
- Belegdatum,
- Vorgangsbeginn und -ende,
- Menge + Art der Leistung,
- Transaktionsnummer,
- Entgelt + Steuer/Steuersatz,
- Seriennummer Kasse + TSE,
- Prüfwert + Signaturzähler.

**TOR-Umsetzung:**
- `FiscalReceiptFields` ist die zentrale Pflichtfeldliste,
- Papier- und Digitalbeleg verwenden dieselben TSE-/Steuerdaten,
- `VatSummaryCalculator` berechnet die MwSt. auf den tatsächlich gezahlten Betrag,
- `KassenSichV2026.ValidateReceipt` prüft Pflichtfelder, Steuercontainer und Cent-Gleichheit,
- QR-Daten werden aus den tatsächlich signierten TSE-Daten aufgebaut.

**Status:** CODE READY / REALER 80-MM-BELEG + QR MIT PHYSISCHER TSE OFFEN.

## Mixed-VAT Menü / Combo

Ein Menü kann z. B. Außer Haus aus 7-%-Speise + 19-%-Getränk bestehen.
Das aktuelle persistente `sale_items`-Modell hält je kommerzieller Position
nur einen Steuersatz.

**Neue Sicherheitsregel:**
- `MenuVatPolicy` berechnet die Marktwertaufteilung deterministisch,
- gemischte effektive Steuersätze werden erkannt,
- Combo-Pfand wird als nicht eindeutig erkannt,
- `CheckoutApplicationService` blockiert den Produktivverkauf vor Journal/
  Terminal/TSE-Nebenwirkungen,
- Parken/Bestellung wird vor TSE-Nebenwirkung blockiert, sobald die Kasse
  fiskal produziert.

**Status:** SAFE-BLOCK IMPLEMENTIERT / MULTI-RATE-PERSISTENZ NOCH OFFEN.

Es darf **keine** globale Freischaltung erfolgen, indem ein einzelner
Menüpreis einfach mit 7 % oder 19 % gespeichert wird.

## § 7–§ 10

EU-Taxameter und Wegstreckenzähler.

**Für TOR POS als Registrierkasse:** nicht anwendbar.

## § 11 – Zertifizierung

Betrifft die Zertifizierung der technischen Sicherheitseinrichtung nach den
BSI-/EUCC-Vorgaben.

**TOR-Folge:** TOR POS darf nur eine dafür geeignete, gültig zertifizierte TSE
in Produktion verwenden. Ein grüner Software-Build ersetzt diese
TSE-Zertifizierung nicht.

## Production-Gates

Die Freigaben liegen zentral in `FiscalRelease` und bleiben bis zum jeweiligen Nachweis `false`:
- `FiscalRelease.DsfinvkValidated`
- `FiscalRelease.KassenSichVReceiptValidated`
- `FiscalRelease.ParkedOrderTseValidated`
- `FiscalRelease.PfandTaxValidated`
- `FiscalRelease.PhysicalTseE2EValidated`
- `FiscalRelease.IndependentFiscalReviewValidated`

`FiscalRelease.Enabled` wird ausschließlich dann `true`, wenn **alle** Qualifikationen erfüllt sind.

## Freigabereihenfolge

1. Automatische Safety-/Regression-Tests grün.
2. DSFinV-K Prüfdatensatz gegen reale Testdaten validieren.
3. Pfand- und Bestellung-Szenarien fachlich/fiskal validieren.
4. Physische Swissbit TSE anschließen.
5. BAR-Testbon, QR, TSE-Zähler/Seriennummer/Prüfwert prüfen.
6. TAR-/Export und Neustart-/Ausfall-Recovery prüfen.
7. Unabhängige Review dokumentieren.
8. Erst danach Production-Gates einzeln freigeben.
