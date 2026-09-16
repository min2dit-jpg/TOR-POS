# TOR POS Pro – Hardware-Abnahmetest (Scanner / Bondrucker / TSE / Kartenterminal)

> Praktische Schritt-für-Schritt-Checkliste für den Test an einem echten
> Windows-PC mit angeschlossener Hardware. Ergänzt die fiskal-/rechtlich
> ausgerichtete `COMMERCIAL-RELEASE-CHECKLIST-DE.md` um die konkreten
> Handgriffe am Gerät. Kann nur an einem echten TOR-POS-Arbeitsplatz
> durchgeführt werden - nicht in einer reinen Entwicklungsumgebung.

Testdatum: __________  Tester: __________  Version: __________
PC/Gerätecode: __________

## 0. Voraussetzungen

- [ ] Windows-PC mit installiertem TOR POS Pro (Setup, nicht `dotnet run`)
- [ ] Star mC-Print3 MCP31CBI über Windows installiert (USB/LAN/Bluetooth), Treiber aktiv
- [ ] Barcode-Scanner (USB-HID/Tastatur-Emulation) angeschlossen
- [ ] Swissbit Hardware-TSE 2 im vorgesehenen USB-Slot
- [ ] ZVT-fähiges Kartenterminal im selben Netzwerk erreichbar (falls Kartenzahlung getestet wird)
- [ ] Kassenschublade am Drucker (RJ11) angeschlossen, falls vorhanden

## 1. Bondrucker – Grundfunktion

- [ ] Einstellungen → Geräte-Manager: Drucker wird erkannt, `ProbeAsync` meldet "bereit"
- [ ] Windows meldet Drucker weder offline noch Papier leer (Testfall: Papier absichtlich entnehmen → TOR muss den Windows-Fehlertext anzeigen, nicht rohen Treibertext)
- [ ] Testdruck aus Einstellungen: Layout lesbar, `Ä Ö Ü · ä ö ü · ß · €` korrekt
- [ ] Testdruck: automatischer Schnitt erfolgt direkt nach dem Bon (R86 - neu, prüfen ob Bon vollständig durchtrennt oder nur perforiert; bei Bedarf `printer.auto_cut.enabled` in Einstellungen deaktivieren)

## 2. Bondrucker – Kassenschublade (R86, neu)

- [ ] Barverkauf abschließen → Kassenschublade öffnet automatisch direkt nach dem Bon
- [ ] Kartenverkauf abschließen → Kassenschublade öffnet **nicht**
- [ ] Testdruck (Einstellungen) → Kassenschublade öffnet **nicht**
- [ ] Bon-Historie → Bon-Kopie drucken → Kassenschublade öffnet **nicht**
- [ ] `printer.drawer_kick.enabled` in Einstellungen deaktivieren → Barverkauf öffnet die Schublade nicht mehr
- [ ] Falls keine Schublade angeschlossen ist: kein Fehler/Absturz, Bondruck bleibt unbeeinträchtigt

## 3. Bondrucker – reale Ausfallszenarien

- [ ] Drucker während eines Verkaufs ausschalten → TOR zeigt verständliche Fehlermeldung mit Fehler-ID (nicht rohen Exception-Text, siehe R85), Bon bleibt in der Druckwarteschlange zur Prüfung
- [ ] Fehler-ID danach über Diagnose-Fenster → "TECHNIKER-DETAILS ZU EINER FEHLER-ID" nachschlagen, technisches Detail erscheint vollständig
- [ ] Drucker wieder einschalten, Warteschlange manuell prüfen/freigeben (`ResolveQueueAsync`-Pfad in den Einstellungen/Diagnose)
- [ ] Kein doppelter Bon-Druck nach Wiederanlauf

## 4. Barcode-Scanner

- [ ] Artikel per Scan zur Kassenliste hinzufügen, korrekter Artikel/Preis
- [ ] Unbekannter Barcode → verständliche Fehlermeldung, keine Absturzgefahr
- [ ] Schnelles Scannen mehrerer Artikel hintereinander → keine verlorenen/duplizierten Scans

## 5. Swissbit Hardware-TSE 2

- [ ] Einstellungen → TSE: Gerät wird erkannt, Seriennummer/Zertifikatsablauf werden gelesen
- [ ] Self-Test erfolgreich, Systemzeit wird aktualisiert
- [ ] Admin-PIN/PUK-Eingabe funktioniert, wird nicht dauerhaft gespeichert
- [ ] Client-Registrierung erfolgreich
- [ ] **Hinweis:** Start/Update/FinishTransaction bleiben inert, solange `FiscalRelease.Enabled=false` ist (Absicht, siehe Fiskal-Sperren) - diese Schritte prüfen nur die SDK-/Geräte-Ebene, nicht den produktiven Signiervorgang

## 6. ZVT-Kartenterminal

- [ ] Verbindungstest aus Einstellungen erfolgreich
- [ ] Registrierung/Autorisierung erfolgreich
- [ ] Kartenverkauf: Betrag korrekt übertragen, Beleg nach Erfolg gedruckt
- [ ] Terminal während einer Zahlung trennen (z. B. Netzwerkkabel) → TOR bucht keinen doppelten/unklaren Verkauf, zeigt "Status unklar" statt stillschweigend fortzufahren
- [ ] Nach Wiederanlauf: offene/unklare Zahlung ist im Journal auffindbar, nicht verloren

## 6b. ZVT-Kartenerstattung (BON STORNO/Teilretoure, R102, neu)

- [ ] Kartenverkauf abschließen, dann BON STORNO auf diesen Bon → Terminal fordert Karte erneut an (`RefundAsync`, kein Bezug auf die Original-Transaktionsnummer nötig)
- [ ] Karte am Terminal vorlegen → Erstattung bestätigt → Storno-Bon wird gebucht, zeigt `KARTE` (nicht `BAR`)
- [ ] Erstattung am Terminal ablehnen/abbrechen → BON STORNO wird **nicht** gebucht, klare Meldung, kein Bon-Duplikat
- [ ] Terminal während der Erstattung trennen → "Status unklar, Terminalbeleg prüfen" statt stillschweigend weiterzumachen; Storno wird **nicht** gebucht
- [ ] Gemischte Zahlung (Bar+Karte) → Teilretoure einer Position → nur der anteilige Karten-Betrag wird am Terminal erstattet, Bar-Anteil separat verrechnet
- [ ] **Wichtig:** dies ist die erste echte Kartenerstattung in TOR POS - noch nicht gegen reale Acquirer/Terminal-Modelle abgenommen; insbesondere prüfen, ob der Acquirer eine `RefundAsync`-Gutschrift ohne Bezug zur Original-Zahlung überhaupt anstandslos verarbeitet

## 7. Ende-zu-Ende-Szenarien

- [ ] Kompletter Barverkauf: Scan → Zahlen → Bon inkl. Schnitt+Schublade → Kassenschublade zu
- [ ] Kompletter Kartenverkauf: Scan → Zahlen → Terminal → Bon inkl. Schnitt, keine Schublade
- [ ] Gemischte Zahlung (R101, neu): GEMISCHT-Button → Bar-Anteil eingeben → sofort kassiert → Rest exakt am Terminal belastet → Bon zeigt beide Anteile
- [ ] BON STORNO eines Barverkaufs (falls Berechtigung aktiv)
- [ ] Teilretoure eines Barverkaufs
- [ ] IMBISS: Bestellung annehmen (Abholnummer/Küchenbon) → später an der Kasse abkassieren
- [ ] Z-Bericht/Kassensturz am Tagesende druckt vollständig und lesbar, Bar/Karte-Summen stimmen auch bei gemischten Zahlungen

## 8. Digitaler Bon per QR-Code (R103, neu)

- [ ] Einstellungen → Bon & Rechnung → "Digitalen Bon per QR-Code anbieten" aktivieren, TOR POS neu starten
- [ ] Beim ersten Start: Windows-Firewall-Hinweis erscheint → "Zugriff zulassen" (privates Netzwerk) wählen; ohne diese Freigabe kann kein anderes Gerät die Kasse erreichen
- [ ] Verkauf abschließen, BON EIN/AUS vorher ausschalten → QR-Code erscheint auf dem Bildschirm
- [ ] Mit einem Handy **im selben WLAN** scannen → Bon-Webseite öffnet sich, Inhalt stimmt mit dem Verkauf überein (Artikel, Summe, MwSt., Zahlart)
- [ ] Mit einem Handy **im Mobilfunknetz (nicht WLAN)** scannen → Seite ist **nicht** erreichbar (das ist erwartetes Verhalten, kein Fehler - siehe Hinweistext auf der Seite selbst)
- [ ] BON EIN/AUS wieder einschalten → nächster Verkauf druckt normal, kein QR-Code erscheint
- [ ] Nach TOR-POS-Neustart ohne die Einstellung geändert zu haben: Funktion bleibt wie zuvor aktiv/inaktiv

## 9. Kundendisplay (R104, neu, z. B. HP L7010t)

- [ ] Einstellungen → Geräte: "Kundendisplay verwenden" aktivieren, Bildschirm wählen (0 = automatisch zweiter Bildschirm), TOR POS neu starten
- [ ] Kundendisplay öffnet sich fullscreen auf dem konfigurierten Bildschirm, zeigt "Willkommen" im Leerlauf
- [ ] Artikel scannen → Kundendisplay zeigt live Artikel/Menge/Preis und die laufende Summe, synchron zur Kasse
- [ ] Verkauf abschließen (Papierbon aktiv) → Kundendisplay zeigt kurz "Vielen Dank" + Endbetrag, dann zurück zu "Willkommen"
- [ ] Verkauf abschließen (BON EIN/AUS aus, digitaler Bon aktiv) → QR-Code erscheint auf dem **Kundendisplay**, nicht auf dem Kassenbildschirm
- [ ] Bestellmonitor (falls IMBISS + aktiv) und Kundendisplay gleichzeitig auf zwei verschiedenen Bildschirmen testen - keine Überschneidung/Verwechslung
- [ ] Kundendisplay-Fenster manuell schließen (Alt+F4 o. Ä.) → keine Absturzgefahr für die Hauptkasse, Fenster kann in Einstellungen erneut aktiviert werden (Neustart nötig)

## Ergebnis

- Technische Freigabe (Scanner/Drucker/TSE-Gerät/Terminal funktionieren zuverlässig): [ ] Ja / [ ] Nein, Grund: __________
- Gefundene Probleme (mit Fehler-ID, falls vorhanden): __________

> Dieser Test ersetzt keine fiskale/steuerliche Endabnahme. Die Punkte aus
> `COMMERCIAL-RELEASE-CHECKLIST-DE.md` (DSFinV-K, TAR-Export, Z-Abschluss,
> § 146a-Meldung) bleiben unabhängig davon bis zur echten TSE-Freigabe offen.
