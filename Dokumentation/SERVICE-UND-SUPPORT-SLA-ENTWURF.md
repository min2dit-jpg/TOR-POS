# TOR POS · Service und Support (SLA) – ENTWURF

**Status:** Entwurf vom 25.09.2026, nicht veröffentlicht, nicht Vertragsbestandteil.
Alle Zeiten, Preise und Kontaktdaten in `[eckigen Klammern]` oder als **Vorschlag**
markiert sind Entscheidungen des Inhabers. Vor der Verwendung gegenüber Kunden:
AGB/Nutzungsbedingungen (`Desktop/NUTZUNGSBEDINGUNGEN-DE.txt`) und Haftung
rechtlich prüfen lassen. Offene Punkt dazu in
`Desktop/COMMERCIAL-RELEASE-CHECKLIST-DE.md`: „Support, Updates, SLA,
Gewährleistung und Haftung vertraglich festgelegt“.

---

## Teil A · Kundenkarte „Wen rufe ich an?“

*Zum Ausdrucken neben die Kasse. Eine Seite, keine Fachbegriffe.*

> **TOR POS Support**
>
> ☎ **[Hotline-Nummer]** – [Servicezeiten, z. B. Mo–Sa 9–21 Uhr]
> ✉ **[support@…]** – wird werktags innerhalb von [4] Stunden beantwortet
> 🚨 **Kasse steht / keine Zahlung möglich:** immer anrufen, nicht mailen.
>
> **Bitte bereithalten:**
> 1. Ihre **Kundennummer**: [steht auf dem Vertrag]
> 2. Die **Fehler-ID**, falls die Kasse eine anzeigt (z. B. `20260914-153000-AB12`)
> 3. Was Sie gerade tun wollten (Verkauf, Storno, Z-Bericht, Kartenzahlung …)
>
> **Bis wir uns melden:**
> - Kasse zeigt „TSE-Ausfall“? Sie dürfen weiter verkaufen. Die Kasse vermerkt
>   den Ausfall selbst auf jedem Bon und im Protokoll. Uns trotzdem anrufen.
> - Kartenterminal geht nicht? Bar kassieren, Kasse nicht neu installieren.
> - Nichts löschen und nichts neu installieren, bevor wir gesprochen haben.

---

## Teil B · Leistungsbeschreibung

### 1. Störungsklassen

| Klasse | Bedeutung | Beispiele aus TOR POS |
|---|---|---|
| **P1 – Kasse steht** | Kein Verkauf möglich | Kasse startet nicht; Verkauf wird verweigert; Datenbank lässt sich nicht öffnen |
| **P2 – Stark eingeschränkt** | Verkauf möglich, aber eine Pflicht- oder Kernfunktion fehlt | TSE-Ausfall (Kasse signiert nicht); keine Kartenzahlung; Bondrucker druckt nicht; Z-Bericht schlägt fehl |
| **P3 – Eingeschränkt** | Nebenfunktion gestört | TOR Cloud/Portal zeigt keine Daten; Bericht-E-Mail kommt nicht an; Küchendrucker (Restaurant) |
| **P4 – Frage / Wunsch** | Keine Störung | Bedienfrage; Artikelimport; Auswertung; Änderungswunsch |

TSE-Ausfall ist bewusst **P2** und nicht P1: Die Kasse verkauft weiter und
dokumentiert den Ausfall selbst (Hinweis auf dem Bon und im TSE-Ausfallprotokoll).
Er muss trotzdem zügig behoben werden.

### 2. Servicezeiten und Reaktionszeiten (Vorschlag)

**Reaktionszeit** = Zeit bis zur ersten qualifizierten Rückmeldung durch einen
Techniker (Anruf oder Fernwartung beginnt), gerechnet innerhalb der Servicezeit.
Sie ist **keine** Lösungszeit.

| | **Basis** | **Standard** | **Gastro Plus** |
|---|---|---|---|
| Für wen | Kiosk, Späti, kleiner Einzelhandel | Einzelhandel, Imbiss | Imbiss mit Abendgeschäft, Restaurant |
| Servicezeit | Mo–Fr 9–17 Uhr | Mo–Sa 9–21 Uhr | täglich 9–23 Uhr, auch Sonn-/Feiertage |
| Kanäle | E-Mail | Telefon, E-Mail, Fernwartung | Telefon, E-Mail, Fernwartung |
| P1 Kasse steht | 8 Std. | **2 Std.** | **1 Std.** |
| P2 stark eingeschränkt | 1 Werktag | 4 Std. | 2 Std. |
| P3 eingeschränkt | 2 Werktage | 1 Werktag | 8 Std. |
| P4 Frage | 3 Werktage | 2 Werktage | 1 Werktag |
| Updates | ✓ | ✓ | ✓ |
| Ersatzgerät bei Hardwaredefekt | – | gegen Aufpreis | innerhalb [1] Werktag |
| Preis / Monat je Kasse | [ ] € | [ ] € | [ ] € |

Die Zahlen sind ein Vorschlag, gemessen an dem, was ein kleines Team verlässlich
halten kann. **Nichts zusagen, was nicht jeden Tag gehalten wird** – eine
verpasste 1-Stunden-Zusage kostet mehr Vertrauen als eine ehrliche 2-Stunden-Zusage.

### 3. Kanäle

- **Telefon-Hotline** [Nummer]: für P1 und P2 immer der erste Weg.
- **E-Mail / Ticket** [Adresse bzw. System]: jede Anfrage bekommt eine
  Ticketnummer; Antwort mit Ticketnummer.
- **Fernwartung** [Werkzeug festlegen]: nur mit Zustimmung des Kunden für die
  einzelne Sitzung; der Kunde sieht, was passiert. Zugriff auf Kundendaten nur
  soweit zur Störungsbehebung nötig (Auftragsverarbeitung nach Art. 28 DSGVO
  vertraglich regeln).
- **Vor Ort** [Region / Anfahrtspauschale]: optional.

### 4. Was bereits im Produkt steckt

Diese Funktionen machen die Zusagen oben einlösbar und sind gute
Verkaufsargumente („wir sehen, was los ist“):

- **Fehler-ID:** Die Kasse zeigt bei Fehlern eine Fehler-ID an; der Techniker
  findet sie unter *Systemstatus / Diagnose* wieder.
- **Systemstatus / Diagnose:** Geräteprüfung (Drucker, Kartenterminal, TSE) auf
  Anforderung, blockierte Kartenerstattungen und Küchenbons, Messwerte.
- **TSE-Ausfallprotokoll:** Beginn, Ende und Grund jedes Ausfalls werden
  unveränderbar protokolliert; die Kasse vermerkt den Ausfall auf dem Bon.
- **TOR Cloud:** zeigt, wann jede Kasse sich zuletzt gemeldet hat, ihre
  Softwareversion und eine offene TSE-Störung – Grundlage für **proaktiven
  Support** (wir rufen an, bevor der Kunde es merkt).
- **Update-Kanal pro Produkt:** signierte Updates für Einzelhandel, Gastro und
  Restaurant getrennt.
- **Daten für die Mitteilung nach § 146a Abs. 4 AO** und der **DSFinV-K-Export**
  stehen in der Kasse bereit.

### 5. Leistungsumfang

Enthalten:

- Störungsannahme und -behebung nach Störungsklasse und Paket.
- Software-Updates und Sicherheitsupdates von TOR POS und TOR Cloud.
- Hilfe bei TSE-Ausfall: Ursache finden, TSE wieder in Betrieb nehmen,
  Ausfallzeitraum aus dem Protokoll bereitstellen.
- Hilfe bei Kassen-Nachschau und Betriebsprüfung: DSFinV-K-Export und
  TSE-Export bereitstellen und erklären.
- Betrieb von TOR Cloud einschließlich Datensicherung der Cloud-Daten.

Nicht enthalten (Mitwirkung des Kunden):

- Internetzugang, Windows-Updates, Virenschutz und Strom der Kasse.
- Gewährleistung und Reparatur von Fremdhardware (Drucker, Terminal, PC), soweit
  nicht als Ersatzgerät-Leistung vereinbart.
- Steuerliche Pflichten des Kunden, insbesondere die Mitteilung nach § 146a
  Abs. 4 AO und die Aufbewahrung. TOR stellt die Daten dafür bereit.
- Eigene Datensicherung der Kasse auf einem externen Datenträger, sofern nicht
  gesondert vereinbart.
- Schäden durch Eingriffe Dritter oder Änderungen am System ohne Absprache.

### 6. Eskalation

Wird eine P1/P2-Störung nicht innerhalb von [4] Stunden behoben, informiert TOR
den Kunden unaufgefordert über Stand und nächsten Schritt; nach [1] Werktag
übernimmt [Name/Rolle] persönlich.

---

## Teil C · Entscheidungen des Inhabers (vor Veröffentlichung)

1. Hotline-Nummer, Support-E-Mail, ggf. Ticketsystem.
2. Servicezeiten und Reaktionszeiten je Paket (Tabelle Abschnitt 2).
3. Preise je Paket, Vertragslaufzeit, Kündigungsfrist.
4. Fernwartungswerkzeug und AV-Vertrag nach Art. 28 DSGVO.
5. Ersatzgeräte: ja/nein, Vorrat, Region.
6. Wer vertritt bei Urlaub/Krankheit? Ohne Vertretung keine P1-Zusage.
7. Rechtliche Prüfung von Haftungsbegrenzung und Gewährleistung.
