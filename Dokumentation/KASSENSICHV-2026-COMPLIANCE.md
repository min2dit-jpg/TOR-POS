# TOR POS – KassenSichV 2026 Compliance Matrix

Stand: 2026-09-19  
Rechtsquelle: Kassensicherungsverordnung (KassenSichV), zuletzt geändert durch Art. 3 V vom 14.01.2026 (BGBl. 2026 I Nr. 10).  
Amtlicher Text: https://www.gesetze-im-internet.de/kassensichv/

> Diese Matrix dokumentiert den technischen Umsetzungsstand. Sie ersetzt keine
> steuerrechtliche Beratung und keine reale Hardware-/Prüferabnahme.

## § 1 Elektronische Aufzeichnungssysteme

TOR POS ist ein computergestütztes Kassensystem und fällt in den Anwendungsbereich.

**Status:** anwendbar.

## § 2 Protokollierung von digitalen Grundaufzeichnungen

| Anforderung | TOR POS | Nachweis / Gate |
|---|---|---|
| Vorgang startet unmittelbar als neue Transaktion | implementiert | `TseVorgangService`, Start bei erstem Vorgang/erster Position |
| Vorgangsbeginn | implementiert | `CheckoutSnapshot.StartedAt`, TSE start log time |
| eindeutige fortlaufende Transaktionsnummer | TSE-seitig vorgesehen | `SaleTseResult.TransactionNumber`; reale TSE-Abnahme offen |
| Art des Vorgangs | implementiert | `Kassenbeleg-V1`, `Bestellung-V1`, Abbruch/Training |
| Daten des Vorgangs | implementiert | `FiscalProcessData` |
| Zahlungsarten | implementiert | BAR / KARTE / MIXED → Bar / Unbar |
| Vorgangsende/-abbruch | implementiert | Finish / Abort / Startup-Recovery |
| Prüfwert | implementiert als TSE-Rückgabedatum | darf nie von TOR erfunden werden |
| Seriennummer Kasse + TSE | implementiert | `KassenSeriennummer`, TSE-Masterdata |
| Signaturzähler | implementiert als TSE-Rückgabedatum | reale TSE-Abnahme offen |

**Offen:** echte Swissbit-Hardware muss Transaktionsnummer, Zähler, Zeiten, Signatur und Lücken-Erkennbarkeit beweisen.

## § 3 Speicherung der Grundaufzeichnungen

- Verkaufs-, Positions-, Training-, Storno-/Retoure-, Bestellung- und TSE-Ergebnisdaten werden unveränderbar/final gespeichert.
- SQLite-Trigger verhindern nachträgliche Änderung/Löschung zentraler fiskalischer Datensätze.
- TSE-Ausfälle werden als Ausfall gespeichert; es erfolgt keine nachträgliche erfundene Signatur.
- TSE-Transaktionsverkettung selbst muss durch die zertifizierte TSE geliefert und im Hardware-E2E nachgewiesen werden.

**Status:** Softwareseite weitgehend implementiert, Hardware-Nachweis offen.

## § 4 Einheitliche digitale Schnittstelle

- DSFinV-K 2.4 Export ist implementiert.
- Tabellen-/Spaltendefinition wird aus der offiziellen `index.xml` gelesen.
- Export besitzt Preflight und verweigert bekannte inkonsistente Datensätze.
- `dsfinvkImplementedAndValidated` bleibt bis zur finalen Prüfdatei-Abnahme bewusst `false`.

**Status:** implementiert, final validation offen.

## § 5 Technische Sicherheitseinrichtung

TOR implementiert keine eigene TSE. Die Produktionsarchitektur nutzt eine zertifizierte
Swissbit-TSE über die WORM API.

- TSE-Schlüssel/Zertifikate werden nicht durch TOR ersetzt.
- TOR erzeugt keine eigenen Transaktionsnummern, Signaturzähler oder Prüfwerte.
- fehlende TSE wird als Ausfall behandelt.

**Status:** Integration implementiert; physische TSE-/Zertifikatsprüfung offen.

## § 6 Anforderungen an den Beleg

Zentraler Validator: `FiscalReceiptFields`.

Geprüft werden:
- vollständiger Unternehmername und Anschrift,
- Beleg-/Vorgangszeiten,
- Menge und Art der gelieferten Gegenstände / Leistung,
- Transaktionsnummer,
- Entgelt-Konsistenz,
- Steuersatz/Steuerbetrag,
- Seriennummer Kasse und TSE,
- Prüfwert,
- Signaturzähler.

Papier- und Digitalbon verwenden dieselbe zentrale Regel.

QR: `TseQrCodePayload` erzeugt nur bei vollständigen TSE-Daten einen DSFinV-K-orientierten Payload; bei unvollständigen Daten wird kein scheinbar gültiger QR erzeugt.

**Status:** Softwareprüfung implementiert; reale TSE-Daten + 80-mm-Hardwarebon müssen final abgenommen werden.

## §§ 7–10

EU-Taxameter und Wegstreckenzähler. Für TOR POS als stationäres Kassenprogramm nicht einschlägig.

## § 11 Zertifizierung

Die Zertifizierung betrifft die technische Sicherheitseinrichtung. TOR POS muss eine gültig
zertifizierte TSE korrekt anbinden; TOR POS selbst wird dadurch nicht zur TSE.

## Zusätzlicher R150-Schutz: gemischte MwSt. in Menüs/Combos

Ein Menü mit mehreren effektiven Umsatzsteuersätzen darf aktuell nicht produktiv bezahlt werden,
solange seine Marktwertaufteilung nicht durchgängig in `sale_items`, Beleg, TSE und DSFinV-K
repräsentiert wird.

`MenuVatPolicy`:
- berechnet eine deterministische Marktwertaufteilung,
- berücksichtigt Im Haus/Außer Haus,
- berücksichtigt den tatsächlich verkauften Menüpreis,
- verweigert erfundene Marktwerte,
- blockiert den Checkout **vor** Journal- und Terminal-I/O.

Dies ist ein Fail-Closed-Schutz, keine Behauptung, dass die Multi-Rate-Repräsentation bereits final ist.

## Production-Gates

Folgende Gates bleiben bis zum jeweiligen Nachweis geschlossen:

- `FiscalRelease.Enabled = false`
- DSFinV-K final validation
- §6 Bon/TSE hardware acceptance
- Bestellung/Parken hardware validation
- Pfand fiscal validation
- gesamter Production Release

Keines dieser Gates darf allein aufgrund eines grünen CI-Laufs aktiviert werden.
