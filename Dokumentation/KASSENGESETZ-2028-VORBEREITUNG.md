# TOR POS – Vorbereitung auf das Zweite Kassengesetz (Stand 26.09.2026)

> **Kurzfassung (TR):** Bu belge, Alman kasa mevzuatındaki planlanan değişikliklere
> (Zweites Kassengesetz) hazırlık için eklenen altyapıyı açıklar. Hiçbir taslak
> hüküm aktif değildir; fiskal davranış yalnız yürürlükteki hukuka ve
> `FiscalRelease` kapılarına göre çalışır. Yeni olan: TSE-Wechsel denetim kaydı,
> Finanzamt bildirim veri modeli (kapalı kapı arkasında), versiyonlu fiskal
> kural setleri, Digital-Bon servis katmanı, QR güvenliği, provider durum
> ayrımı, AI güvenlik sınırı, DSFinV-K preflight ve doğrulanmış ZIP paketi.

## 1. Rechtsstand – vor jeder Aktivierung erneut prüfen

| Punkt | Stand 26.09.2026 |
|---|---|
| Referentenentwurf BMF | veröffentlicht 05.08.2026 |
| Regierungsentwurf („Zweites Kassengesetz“) | Kabinettsbeschluss 23.09.2026 |
| Bundestag / Bundesrat | **nicht abgeschlossen** |
| Verkündung im BGBl. | **nein** |
| Geplanter Inhalt | Kassenpflicht ab 100.000 EUR Jahresumsatz, elektronische Belegbereitstellung (z. B. QR/NFC) statt Papier-Bonpflicht, geplante Anwendung ab 01.01.2028 |

Quellen: BMF-Themenseite „Digitale Kassen und das Ende der Papierbons“,
Kanzlei-/Beraterberichte vom 25.09.2026. Maßgeblich ist allein der verkündete
Gesetzestext. **Kein Entwurfsinhalt ist in TOR aktiv.**

## 2. Was implementiert ist

### 2.1 Versionierte Regelwerke (`TorPos.Core/GermanFiscalRuleset.cs`)
- `FiscalComplianceProfile` bündelt die Pflichten (TSE, DSFinV-K, Papier-/Digitalbeleg,
  Mitteilungspflichten) mit Rechtsgrundlage und Prüfstatus.
- `GermanFiscalRulesets.Current` = geltendes Recht; `Planned2028` = Regierungsentwurf.
- `Resolve(datum)` liefert **immer** `Current`, solange `Planned2028Enacted = false`.
- `RequireEnacted` verweigert einen nur zur Vorschau geladenen Entwurf.
- CI (`tools/Verify-Fiscal-Release-Gates.ps1`) bricht ab, wenn `Planned2028Enacted`
  oder `AutomaticSubmissionEnabled` auf `true` gesetzt wird.

### 2.2 TSE-Wechselprotokoll (`TseChange.cs`, `TseChangeJournal.cs`)
- Jeder TSE-Wechsel (neue Seriennummer oder anderer Provider, auch Ersteinrichtung)
  wird mit alter/neuer TSE, TSE-Art, Zertifikatsdaten (nur öffentliche), Grund,
  Benutzer, Kassen-ID (eAS-Seriennummer), Terminal, Client-ID, Mandant, Ergebnis
  und ggf. Fehlercode/-text protokolliert.
- Speicherung als Zeilen im bestehenden `audit_log` (Trigger verhindern UPDATE/DELETE)
  → append-only **ohne Schemaänderung**.
- Auslöser: Geräteprüfung, Aktivierung (auch fehlgeschlagene) und manuelles Speichern
  der TSE-Einstellungen im Einstellungsfenster.
- Verkäufe, TSE-Ergebnisse und DSFinV-K-Daten der alten TSE werden nie geändert
  oder der neuen TSE zugeordnet.

### 2.3 Meldedatenmodell (`TseChangeNotificationRecord`)
- Status: `NotRequired`, `Pending`, `ReadyForSubmission`, `Submitted`, `Failed`, `Superseded`;
  Historie append-only, Übergänge geprüft, `Submitted` nur mit Referenz (z. B. ELSTER-Transferticket).
- Nach geltendem Recht startet jede Meldung als `NotRequired` (Hinweis: Mitteilung nach
  § 146a Abs. 4 AO über Mein ELSTER eigenverantwortlich prüfen).
- Automatische Übermittlung: `FiscalNotificationRelease.AutomaticSubmissionEnabled = false`,
  einziger Provider `DisabledFiscalNotificationProvider` lehnt ab.

### 2.4 Digitaler Beleg (`DigitalReceiptDelivery.cs`)
- Kanäle: Papier, QR-Code, PDF, E-Mail, Download-Link; `IDigitalReceiptProvider` als Schnittstelle.
- `ReceiptDeliveryPolicy.Plan` verlangt einen fiskal abgeschlossenen Vorgang (signiert oder
  dokumentierter TSE-Ausfall); sonst: „TSE nicht verfügbar. Vorgang kann derzeit nicht fiskal
  abgeschlossen werden“.
- Heute Standard Papier, Papier immer angeboten; Vorschau 2028: Standard QR, Papier auf Wunsch.
- `MustPrintPaper`: Papier, sobald der gewählte digitale Kanal nicht bestätigt hat.
- Der Beleg ist reine Darstellung – er ändert keinen fiskalischen Datensatz.

### 2.5 QR-Sicherheit (`QrReceiptPayload`)
- Im QR nur ein HTTPS-Link `/r/<43-stelliges Token>` (256 Bit, opak) – keine Kundendaten,
  keine TSE-Daten, keine Zugangsdaten, keine Query/Fragment. TOR Cloud speichert nur den
  Hash des Tokens, der Link läuft ab (TTL), und jeder Beleg ist an `business_id`/`register_id`
  des einreichenden Geräts gebunden – ein Gerät kann nur eigene Belege neu ausstellen.

### 2.6 Provider-Zustand (`ProviderReadiness.cs`)
- `Configured`, `Reachable`, `Validated`, `Health` und `Capability` getrennt;
  „verbunden“ ≠ „fiskal freigegeben“ (`StatusText`).

### 2.7 KI-Grenze (`AiSafetyBoundary.cs`)
- Verboten für KI – auch mit Bestätigung: TSE-Transaktion abschließen, TSE-Daten ändern,
  Zahlung abschließen, alte Verkäufe/finale Rechnungen/DSFinV-K/Audit-Log ändern,
  Rechte erhöhen.
- Preis-/Steueränderung nur: KI-Vorschlag → Benutzerbestätigung → Berechtigung →
  deterministischer TOR-Dienst (Prüfung, Audit).
- Audit-Typen `AiRecommendationCreated/Accepted/Rejected`, `AiActionExecuted/Failed`;
  KI-Text nur im Audit-Log, nie in fiskalischen Datensätzen.
- Test: der Fiskal-/Zahlungskern referenziert keine KI-Typen.

### 2.8 DSFinV-K
- Zusätzliche Preflight-Regeln (`DsfinvkPreflightChecks`): Teiltag/Zukunft (DST-sicher),
  fehlende Z-Nummern (Warnung), doppelte Z-Nummern (blockierend), mehrere TSE im Zeitraum
  und undokumentierte TSE-Wechsel. Alle Hinweise erscheinen im Dialog und im Exportprotokoll.
- E-Mail-Versand: `DsfinvkPackage` erstellt das ZIP, vergleicht jede Datei per SHA-256 mit
  dem Export (verlustfrei, keine Umformatierung), schreibt die ZIP-Prüfsumme (`.sha256`) und
  nimmt sie in E-Mail-Text und Audit-Log auf. Abweichende oder unvollständige Pakete werden abgelehnt.

### 2.9 Tests
- `GermanFiscalPrepTests` (33 Prüfungen) und `FiscalPropertyTests` (7 Prüfungen, feste Seeds):
  Bon-Links/QR, DSFinV-K-Zeiträume über Sommer-/Winterzeit, Cent-genaue USt-Aufteilung,
  Teilretouren, CSV-Import, TSE-TAR-Parser, TSE-Seriennummern.

## 3. Einzelhandel/Gastro – Nacharbeiten aus dem Prüfbericht 24.09.2026

Ergänzend zu PR #97 (O-4/5/7/8/9/10/13/14/16/17/19), ohne diese zu wiederholen:

| Punkt | Umsetzung |
|---|---|
| V-3 | DSFinV-K-Preflight/Export liest auf eigener Read-only-Verbindung in einem WAL-Snapshot statt in der globalen Schreib-Warteschlange – Kassieren bleibt während des Exports möglich (Test hält die Warteschlange fest). |
| O-3 | Erfolgreiche TSE-Prüfung wird 30 s wiederverwendet; jeder Fehler, Ausfall oder abgelaufenes Zertifikat verwirft sie. |
| Belegausgabe | Kasse entscheidet Papier/QR über `ReceiptDeliveryPolicy` + geltendes Regelwerk. Endet die Signierung mit einer Ausnahme (weder signiert noch Ausfall dokumentiert), wird **kein Normalbeleg** gedruckt: deutsche Meldung, Audit `RECEIPT_WITHHELD_NOT_FISCAL`, Nachdruck über Bon-Historie. |
| TSE-Wechselprotokoll | Fenster in den TSE-Einstellungen; nur Admin pflegt den Meldestatus (manuelle ELSTER-Mitteilung mit Transferticket). |
| Last/Eingaben | Zahlungsjournal-Wettlauf, 80 parallele Wiederholungen unter SQLite-Sperre + Export, langsame/fehlerhafte TSE; EAN-13- und Waagen-Property-Tests. |
| G-4 | Lizenz-Deaktivierung zusätzlich als Tombstone in ProgramData und Registry (HKCU); Ablauf gegen die höchste je gesehene Uhrzeit (max. 400 Tage Vorlauf vertraut); Demo-Cache nur plausibel (≤ 7 Tage ab Start, Start ≤ letzter Serverkontakt). |
| O-15 | Startprüfung warnt, wenn Kassendaten desselben Produkts in einem anderen Windows-Benutzerprofil liegen. |

**Bewusst nicht umgesetzt (Begründung):**
- **O-15 Verlagerung nach ProgramData:** betrifft Installer-ACL und die Migration der
  Fiskaldatenbank auf jedem Kunden-PC. Ohne Windows-/Hardwaretest nicht verantwortbar;
  Plan: Installer legt `ProgramData\<Produkt>` mit Gruppe „TOR-POS-Bediener“ an, Migration
  per SQLite-Backup-API + `integrity_check` + Zeilenvergleich, alte DB wird nur umbenannt.
- **O-2 dauerhafter Swissbit-Worker:** braucht echte TSE zur Messung und Abnahme.
- **G-4 Rest:** vollständiger Schutz erfordert servergesignierte Demo-Token (Cloud-Änderung)
  und eine Geräte-Bindung der Demo auf dem Server; die Demo-API-Domain ist eine Betriebsentscheidung.

## 4. Offen / nächste Schritte

| Priorität | Punkt |
|---|---|
| P1 | UI für TSE-Wechselprotokoll und manuelle Meldestatus-Pflege (Admin) |
| P1 | Checkout-Anbindung von `ReceiptDeliveryPolicy` je Edition (Einzelhandel/Gastro/Restaurant getrennt) |
| P1 | Weitere Fuzz-Ziele: REST-/Cloud-API, Zahlungs- und TSE-Provider-Antworten, Restaurant-Split |
| P1 | Last-/Stresstest Restaurant/Kiosk (parallele Bestellungen, Split, Storno, Drucker-/Netz-/TSE-Verzögerung, SQLite busy, Neustart; keine Doppelzahlung/-signierung) |
| P2 | Automatische Meldung (ERiC/ELSTER) – erst nach Verkündung und Abnahme |
| P2 | Aktivierung `Planned2028` – erst nach Verkündung, Abgleich mit Endfassung, Review |
| – | TOR E-Rechnung bleibt eigenes Produkt; POS liefert höchstens Rechnungsanforderung, Kundendaten, Vorgangsreferenz (Export/API) |

Aktivierung eines Entwurfs erfordert: verkündeter Text, Abgleich jeder Profil-Eigenschaft,
Anpassung von `Verify-Fiscal-Release-Gates.ps1`, Review, Hardware-/E2E-Abnahme.
