# Stand R51

R48 baut auf R47/R46/R45/R43/R42 auf:
- [x] Automatische fortlaufende Artikel-Nr. für neue Artikel (ab 100000)
- [x] Additive Nachnummerierung bestehender Artikel ohne Artikel-Nr.
- [x] Bestehende/importierte Artikelnummern bleiben erhalten
- [x] IMBISS: tägliche Abholnummer 001, 002, 003 … pro abgeschlossenem Verkauf
- [x] Abholnummer auf Bon, Bon-Historie und TOR POS Cloud
- [x] TRAINING nutzt nur eine lokale Sitzungssequenz und verbraucht keine Produktiv-Abholnummer
- [x] Cloud Regression 15/15
- [ ] Windows-Build / echter Bon-Druck mit großer Abholnummer auf 58/80 mm am Kassen-PC
- [ ] 10.000 Artikel / 500.000 Verkauf Lasttest und realer Scanner-/Drucker-/Terminal-End-to-End-Test

Nächste IMBISS-Prioritäten: Menü/Combo, zweiter Küchenbon/Küchendrucker und Mehrsprachigkeit. Lieferplattformen folgen als getrennte Integrationsschicht.
Fiskalische Produktion bleibt bis realer TSE-/DSFinV-K-End-to-End-Abnahme gesperrt.

# Roadmap

## Core v0.2
[x] .NET cross-platform temel
[x] SQLite WAL
[x] RAM product cache
[x] barcode scanner
[x] product images
[x] variants / sizes
[x] Pfand
[x] BAR / KARTE local sales
[x] Kiosk / Imbiss
[x] performance counters

## Core v0.2
[x] Schnellartikel / freie Preiseingabe
[x] Menü / Combo (bereits seit R49 fertig, Checkbox war nur nicht aktualisiert)
[x] Extras / Zutaten
[x] Im Haus / Außer Haus tax rules (R95: §12 UStG - ImHausVat.Effective hebt 7% auf 19% an, Bruttopreis bleibt gleich, Umschalter nur im IMBISS-Betrieb; R96: Auswahl überlebt Parken/Absturz-Wiederherstellung; R97: pro Warengruppe abschaltbar; R98: CSV/DB-Import in bestehende Warengruppen vererbt die Einstellung korrekt statt sie zurückzusetzen; R99: CSV-Export + DB-zu-DB-Import tragen die Einstellung jetzt ebenfalls mit, voller Roundtrip auf einer frischen/wiederhergestellten Datenbank)
[x] R100: Artikel-Import zwischen zwei unabhängig erstellten TOR-POS-Installationen scheiterte immer an einem globalen Barcode-Unique-Index vs. scope-gefilterter Duplikatsprüfung (beide Installationen seeden denselben Demo-Barcode unter KIOSK-Scope) - Duplikatsprüfung jetzt konsistent mit dem echten globalen Constraint
[x] Rückgabe / Storno journal (R88: bestehender "STORNOBERICHT" erfasste Teilretouren und SOFORT-STORNO nie korrekt - neu aufgebaut als "STORNO- UND RETOURENJOURNAL")
[x] kullanıcı / PIN / yetki
[x] gerçek bon queue
[x] bon yeniden yazdırma
[x] stok temeli
[x] thumbnail generation (R87: Bitmap.DecodeToWidth statt Volldekodierung für Artikelkacheln)
[x] crash recovery
[x] 10k ürün / 500k satış benchmark (R84: PASS, 12,4s - Checkbox war nur nicht aktualisiert, siehe unten "R48 ve sonrası")

## Fiscal v0.4
(Diese ganze Sektion war eine frühe grobe Planungsliste, nie aktualisiert, nachdem die eigentliche Arbeit unter "Swissbit Runtime v0.6.8"/"Deutschland Fiscal Hardening v0.6.0" viel detaillierter erledigt wurde - Checkboxen waren nur nicht nachgezogen worden.)
[x] TSE abstraction (`ITseProvider`, siehe Swissbit Runtime v0.6.8)
[x] TSE vendor adapter (`SwissbitHardwareTseProvider`, siehe Swissbit Runtime v0.6.8)
[x] transaction lifecycle (StartTransaction/UpdateTransaction/FinishTransaction, siehe Swissbit Runtime v0.6.8)
[x] TSE fail mode (`TseFailSafeService`/`TseOutageRepository`, R78 TSE-Ausfall-Log Schema)
[x] fiscal receipt data (KassenSichV-Fiskalfelder im Receipt-Modell, siehe Deutschland Fiscal Hardening v0.6.0)
[ ] DSFinV-K (echter Export bleibt Entwurf/gesperrt - siehe Deutschland Fiscal Hardening v0.6.0 und Swissbit Runtime v0.6.8, "DSFinV-K 2.4 end-to-end validation")
[x] audit log (`IAuditLog`/`audit_log`, durchgängig genutzt seit den ersten Revisionen)

## Payment v0.4
(Gleiche Situation wie "Fiscal v0.4" oben - siehe "Payment Terminal v0.6.1" für die eigentliche, aktuelle Tracking-Sektion.)
[x] ZVT (siehe Payment Terminal v0.6.1)
[x] terminal config (IP/Port/Timeouts in Einstellungen → Zahlarten → Kartenterminal)
[x] success/failure state machine (`checkout_operations`: PREPARED→SENT→APPROVED/NOT_CHARGED/UNKNOWN→COMMITTED)
[x] mixed payment (R101: GEMISCHT-Button, Bar-Anteil sofort kassiert, Karten-Anteil exakt an Terminal, TSE ProcessData/Berichte/Kassensturz korrekt aufgeteilt; Storno/Retoure vorerst wie KARTE gesperrt)
[x] timeout isolation (`CommandTimeoutSeconds`/`CancellationTokenSource`, siehe ZvtPaymentTerminalService)


## Office / Einstellungen v0.2
[x] Profesyonel sol navigasyon
[x] Firmendaten
[x] Funktionen
[x] Zahlarten
[x] Steuer / DATEV hazırlığı
[x] Bon ayarları
[x] Geräte-Manager konfigürasyonu
[x] Scanner ayarları
[x] Datensicherung + path test + manuel backup
[x] Personal/Rechte çerçevesi
[x] Sistem ve performance görünümü


## v0.4 – Installationsprofil / TSE
[x] KIOSK / IMBISS Auswahl im Installer
[x] Installationsversion dauerhaft sperren
[x] Versionswechsel aus Kassenoberfläche entfernen
[x] Versionswechsel aus Einstellungen entfernen
[x] KIOSK Scanner-Fokus
[x] IMBISS Schnellwahl-Fokus
[x] Einstellungen -> TSE-Aktivierung
[x] TSE-Metadatenfelder
[x] PIN / PUK nicht persistent speichern
[ ] Echter TSE-Geräteadapter
[ ] TSE-Verbindungstest gegen reale Hardware
[ ] TSE-Aktivierung / Admin-Authentifizierung
[ ] TSE-Transaktionslebenszyklus
[ ] DSFinV-K mit realen TSE-Daten


## Swissbit Fiscal Adapter v0.4
[x] Swissbit Hardware TSE 2 standard provider
[x] USB as TOR standard form factor
[x] ITseProvider abstraction
[x] Swissbit SDK bridge boundary
[x] TSE metadata auto-read fields
[x] Admin-PIN / PUK non-persistent
[x] Settings fixed to Swissbit standard
[ ] Obtain official Swissbit SDK / drivers
[ ] Implement documented native SDK bridge
[ ] Real Hardware TSE 2 USB detection test
[ ] Real activation test
[ ] Transaction start/finish signing
[ ] TSE removal / timeout / recovery tests
[ ] Fiscal receipt fields
[ ] DSFinV-K real-data validation


## Printer v0.4.2
[x] Star mC-Print3 MCP31CBI standard receipt printer
[x] Windows installed-printer discovery
[x] MCP31 / mC-Print3 auto detection
[x] 80 mm receipt layout
[x] Test receipt
[x] Auto receipt after sale
[x] Dedicated background print queue
[x] Printer failure does not block UI thread
[ ] Direct StarPRNT SDK status
[x] Direct cutter command (R86: StarPRNT/ESC-POS GS V via Windows RAW spooler passthrough, no StarIO10 SDK needed)
[x] Direct DK cash-drawer command (R86: ESC p, cash sales only, opt-out in Einstellungen)
[~] Paper-out monitoring (R89: Live-Statuscheck vor jedem einzelnen Druckauftrag, kein kontinuierliches Push-Monitoring während der Warteschlange - dafür weiterhin StarIO10-SDK oder direkte Portanbindung nötig)


## Deutschland Fiscal Hardening v0.6.0
[x] eAS-Seriennummer einmalig / unveränderbar
[x] Recht & Fiskal Statusseite
[x] Produktivbetrieb ohne Fiskalprüfung gesperrt
[x] Append-only Audit-Log
[x] Abgeschlossene Verkäufe programmintern unveränderbar
[x] Einlage / Entnahme mit Grund
[x] Kassensturz-Sollbestand technische Basis
[x] TSE-Ausfall-Log Schema
[x] Testbon klar als nicht produktiv gekennzeichnet
[x] MwSt.-Zusammenfassung auf Testbon
[x] KassenSichV-Fiskalfelder im Receipt-Modell vorbereitet
[x] §146a Meldestatus dokumentierbar
[x] Verfahrensdokumentations-Vorlage
[ ] Offizielles Swissbit SDK / reale TSE-Signierung
[ ] Parken als TSE Bestellung / SonstigerVorgang
[x] DSFinV-K 2.4 vollständiger Export (R131: alle 20 Dateien je Kassenabschluss, offizielle index.xml/DTD unverändert, Wertprüfung gegen index.xml, Protokoll mit SHA-256 und offen genannten Lücken - Abnahme mit Prüfsoftware/realer TSE steht aus, siehe "DSFinV-K 2.4 end-to-end validation")
[ ] TSE TAR Export
[x] R79: BON STORNO (volle Gegenbuchung eines abgeschlossenen Bons); R102: KARTE-Storno/Teilretoure per ZVT RefundAsync (manuelle Gutschrift, Karte erneut vorgelegt) umgesetzt - keine Beschränkung mehr auf BAR
[x] R103: Digitaler Bon per QR-Code - lokaler Webserver auf der Kasse (kein Cloud-Hosting, keine Admin-Rechte nötig), QR erscheint nur wenn BON EIN/AUS ausgeschaltet ist, kein Papier nötig (mit R145 durch TOR Cloud ersetzt)
[x] R104: Kundendisplay (z. B. HP L7010t) - echter zweiter Bildschirm zeigt Warenkorb/Summe live und "Vielen Dank" inkl. R103-QR-Code; "Kundenanzeige"-Einstellung existierte vorher nur als leeres Gerüst (COM-Port-Feld, nie implementiert)
[x] R106: eigene Quellcode-Prüfung des Nutzers (1. Runde) - Retoure-Rabattanteil unprorated, digitaler Bon-MwSt bei Rabatt falsch, keine dauerhafte Sperre gegen doppelten Kartenerstattungsversuch - alle drei behoben
[x] R107: eigene Quellcode-Prüfung des Nutzers (2. Runde) - Kartenerstattung lief vor Prüfung auf bereits erfolgte Stornierung/Retoure (Terminal wurde zuerst belastet), Storno/Teilretoure prüften sich gegenseitig nicht (Doppelerstattung möglich, z. B. 130 € Erstattung auf 100 € Bon), digitaler Bon verwechselte TSE-Ausfall mit Testbon - alle drei behoben; zusätzlich reject()-Testmethodik um Nachrichtenprüfung erweitert (RejectMessage), damit ein Test nachweislich das GEMEINTE Gate prüft, nicht ein früheres
[x] R108: selbstgesteuerte Prüfung (Nutzer: "sen karar ver") - dieselbe VAT-pro-Steuersatz-Formel wurde ein DRITTES Mal unabhängig nachgebaut gefunden, diesmal im TSE-ProcessData-Payload selbst (FiscalProcessData.BuildKassenbeleg/BuildBestellung) - ignorierte den manuellen Rabatt komplett, sodass die UStNormal-Summe nicht mit Betrag-Summe übereinstimmte; behoben mit demselben VatSummaryCalculator. Außerdem zwei extern generierte Architektur-Bewertungen gegen den echten Code geprüft - Startup-Blockierung, schwache Lizenzierung, ungebundene Hardware-Timeouts, fehlende Offline-First-Synchronisation und "Windows/Linux-Cross-Platform" waren alle bereits widerlegt/vorhanden, keine Codeänderung nötig
[x] R109: letzte offene R105-Lücke geschlossen - Sepet-Panel war fest 570px (56% des Fensters bei der R105-Untergrenze 1024px). Jetzt proportional (0.42*, MinWidth 380/MaxWidth 650) mit an ~1920px kalibriertem Verhältnis, damit die gewohnte Optik am normalen Desktop unverändert bleibt, das Panel auf kleinen Tablets aber wirklich schrumpft
[x] R110: App-weite UI-Politur (Nutzerwunsch "Design/Arayüz modernisieren") - neue zentrale AppTheme-Klasse ersetzt ~140 verstreute, teils widersprüchliche Hex-Literale über ~15 Dateien (~40 Fenster im Code-behind ohne bisheriges gemeinsames Theme-System); gleiches dunkles Farbschema, nur konsolidiert statt neu erfunden
[x] R111: Nutzer-Feedback nach R110 ("değişen birşey yok gibi") - Hauptkasse-Bildschirm bewusst nicht Ziel von R110 (Warengruppen-Farben sind Admin-Daten, Header/Toolbar hatte schon keine Drift). Jetzt gezielt aufgefrischt: Hover/Pressed-Feedback für action/topaction/keypad-Buttons (fehlte komplett), sanftes Hover-Wachsen der Kacheln, weiche Schlagschatten auf Header/Kategorie/Sepet-Panel - Kachelfarben selbst unangetastet
[x] R126: Kleine Bildschirme (Nutzerfotos vom HP-Kassenbildschirm und Laptop) - Kopfzeile war EIN nicht schrumpfbarer StackPanel: auf dem HP waren AUSSER HAUS, GEMISCHT und ABMELDEN nicht erreichbar, bei 1366px war ABMELDEN abgeschnitten, der Firmenname verschwand (das TSE-AUSFALL-Badge aus R113 nahm den letzten Platz). Jetzt eigene, nie abgeschnittene Spalte für die Pflichtknöpfe und Statustexte in drei Längen (HeaderDensityPolicy). Ziffernblock: Ziffern wurden unten abgeschnitten ("0" wie "n") - Text passt sich jetzt per Viewbox an, Nummernblock hat eine Mindesthöhe. Außerdem: C/EXTRA/SCHNELLARTIKEL blieben nach jedem Start deaktiviert, bis der Warenkorb einmal geändert wurde (daher graue Tasten am Laptop, aktive am HP). Neues Werkzeug tools/TorPos.UiSnapshot rendert das Kassenfenster bei 5 Bildschirmgrößen ohne echten Bildschirm und prüft mit --check die Lage der Pflichtknöpfe - schlägt gegen das alte Layout nachweislich fehl, läuft jetzt in der CI. Warengruppenfarben: kein Fehler, neue Warengruppen bekommen Standardblau
[x] R124: Abholnummer lief pro Kalendertag ("pickup.20260916") und sprang bei einem über Mitternacht geöffneten Imbiss mitten im Service auf 001 zurück - jetzt pro Betriebsperiode vom letzten Tagesabschluss an (dieselbe Grenze wie der Z-Bericht seit R117), Umbruch 999 -> 001; drei Kopien der Zähler-SQL zu PickupSequence.NextAsync zusammengeführt. R48ReviewTests hatte "Counter resets on Berlin midnight" als Sollverhalten festgeschrieben (fünfter solcher Fall). Aktionszeiträume bewusst unverändert: Start- und Enddatum gelten laut R71-Regel 4 inklusive, ein beworbener Preis darf nicht über sein Enddatum hinaus gelten
[x] R123: Deutsche Zahlenformate auf allen Ausgaben unabhängig von der Windows-Sprache - PR #1 fand im STORNOBERICHT "5.00 EUR" unter englischem Windows; bei der Prüfung fand sich dasselbe auf dem gedruckten Kassenbon selbst (Beträge, Mengen, MwSt-Sätze), Küchenbon, digitalen Bon, Warenbestand/Verkaufsstatistik, Etiketten-PDF und Kassensturz. Gemeinsame Regel TorPos.Core.GermanFormat; Tests laufen unter en-US und schlagen gegen den alten Code nachweislich fehl
[x] R122: mittlere/niedrige Auditbefunde G4/G5/F5/F6/İ6 - (G4) must_change_password wurde beim Admin NUR vom Anmeldefenster durchgesetzt; jede andere Stelle musste dieselbe Regel selbst nachbauen. AuthenticatedUser.Can() liefert für ein Konto mit Werkszugangsdaten jetzt grundsätzlich false, die Anmeldung selbst bleibt möglich (sonst wäre der Änderungsdialog unerreichbar) und hinterlässt einen Auditeintrag. (G5) Der Wiederherstellungscode der Sicherung wurde mit einem einzelnen, ungesalzenen SHA-256 zum Schlüssel; jetzt gesalzenes PBKDF2-SHA256 (600.000 Runden), Salt und Rundenzahl IM Container, weil eine Wiederherstellung auf einem Ersatzrechner nur Datei und Code hat. Format 1 bleibt dauerhaft lesbar - bestehende Kundensicherungen müssen weiter funktionieren; eine Altinstallation wird als solche gemeldet und ein einziges NEU ERSTELLEN stellt um. Dazu: ein beschädigtes .tpe wird nicht mehr in der angegebenen Größe alloziert. (F5) Der digitale Bon druckte "Elektronischer Beleg gem. §6 KassenSichV" bedingungslos und ließ fehlende Pflichtfelder einfach weg - Pflichtfeldliste jetzt gemeinsam in TorPos.Core.FiscalReceiptFields; der Drucker verweigert weiterhin, die Webseite zeigt den Bon, benennt aber die fehlenden Angaben statt Konformität zu behaupten. (F6) Der Kommentar zu Migration 8 behauptete das Gegenteil des Codes; korrigiert, und ein zweites TSE-Ergebnis zum selben Beleg wird mit klarer Regel abgelehnt statt mit einem rohen UNIQUE-Fehler. Zusätzlich: Training-Code ist konfigurierbar (training.access_code) statt der Konstante 0000, Toter Code entfernt (GetDailySinceLastZAsync, Cloud qrSvg)
[x] R129: Fiskalentscheidung R122/F6 (Nachsignieren nach TSE-Ausfall) nach Rechtslage geschlossen - KEIN Nachsignieren. AEAO zu § 146a Nr. 1.14 (BMF 30.06.2023, durch die Änderung vom 17.03.2026 nicht berührt) verlangt bei TSE-Ausfall: Ausfallzeiten und -grund dokumentieren, Ausfall auf dem Beleg erkennbar machen, Weiterbetrieb zulässig, Ursache unverzüglich beheben; Nr. 2.7: Belegausgabepflicht bleibt. Eine nachträgliche Signierung sieht weder AO, KassenSichV, AEAO noch DSFinV-K vor, und sie würde nichts heilen: § 2 KassenSichV verlangt den Transaktionsstart "unmittelbar", die Zeitpunkte legt das Sicherheitsmodul fest - eine spätere Signatur trüge die spätere TSE-Zeit. DSFinV-K sieht für solche Vorgänge TSE_TA_FEHLER vor. TOR erfüllt die vier Punkte bereits (tse_outage_log mit Beginn/Ende/Grund, TSE-AUSFALL-Hinweis auf dem Bon seit R113, Ausfall-Badge im Kassenkopf, Weiterverkauf); sale_tse_signatures bleibt pro Beleg endgültig. Nur Kommentare und Doku geändert, kein Programmverhalten
[x] R130: TSE-processData nach DSFinV-K 2.4 Anhang I (offizielle Spezifikation vom BZSt gelesen) - TOR erzeugte ein eigenes Format ("Beleg^Zeitstempel^Betrag-Summe:..^UStNormal:..^Beleg-Nr:.."), übergab die Daten schon bei StartTransaction und signierte einen Storno als AVBelegstorno, das Anhang B/I für TSE-gesicherte Kassen ausschließt. Jetzt Kassenbeleg-V1 "Beleg^19%_7%_§24_§24_0%^Betrag:Bar_Betrag:Unbar" (Storno/Retoure mit umgekehrten Vorzeichen), Bestellung-V1 "Menge;\"Bezeichnung\";Preis" mit CR, Start leer; nicht zuordenbarer MwSt-Satz öffnet keine Transaktion und wird als TSE-Ausfall dokumentiert. Sieben Alttests (R78/R80/R82/R83/R101/R108/R121) hatten das erfundene Format festgeschrieben (sechster solcher Fall); R130ReviewTests prüft gegen die Beispiele aus Anhang I selbst
[x] R135: Training als AVTraining nach AEAO zu § 146a Nr. 1.11.1 / DSFinV-K 4.2.6 - auf einer echt buchenden Kasse werden Trainingsverkäufe in eigenen unveränderbaren Tabellen erfasst (nie in sales), als Kassenbeleg-V1 mit AVTraining TSE-signiert und im Export ohne Wirkung auf den Kassenabschluss geführt. Bestellmodus mit R142 nachgezogen
[x] R142: jede Handlung im Trainingsmodus abgesichert und als AVTraining gekennzeichnet (DSFinV-K Anhang B) - Trainingsbestellungen (Annahme/Änderung/Storno) als Bestellung-V1, abgebrochener Trainingsvorgang als AVTraining statt AVBelegabbruch
[x] R134: Einlage/Entnahme nach AEAO zu § 146a Nr. 1.10.2 - Art ist Pflicht (Geldtransit, Privateinlage/-entnahme, Lohnzahlung, sonstige Ein-/Auszahlung), echte Buchungen werden als Kassenbeleg-V1 TSE-signiert (0-%-Container, Bar), ein endgültiger TSE-Eintrag je Bewegung, keine Belegpflicht (Nr. 2.5.5); Export mit GV_TYP und TSE-Daten. Offen: DifferenzSollIst aus dem Kassensturz buchen
[x] R136: TSE-Transaktion startet mit der ersten Position (AEAO zu § 146a Nr. 2.2.2, § 2 KassenSichV) statt nach der Zahlung - Zahlung beendet dieselbe Transaktion (Kassenbeleg-V1), Bestellannahme mit Bestellung-V1, Parken hält sie offen, geleerter Warenkorb / gelöschter geparkter Bon endet als AVBelegabbruch (Anhang I) mit unveränderbar gespeicherten Positionen; offene Transaktionen überstehen einen Neustart, Z-Bericht nur ohne offenen Vorgang (Nr. 2.2.3.3). BON_START und TSE_TA_START werden gespeichert und exportiert, Bon zeigt Vorgangsbeginn und -ende (§ 6 KassenSichV)
[x] R137: Bestellannahme, Bestelländerung und Bestellstorno je als eigene Bestellung-V1-Transaktion (DSFinV-K 4.2.3) - Änderung nur mit der Differenz, Storno mit umgekehrtem Vorzeichen, unveränderbar in order_bestellungen; abweichende Positionen bei der Abholung werden vor dem Beleg abgesichert; aufgerufene Bestellung startet erst bei Änderung/Zahlung einen Vorgang, Bon zeigt Bestellbeginn (2.7.2); Export je Datensatz AVBestellung mit BON_STORNO/Referenz und Im-Haus-Steuersatz. BUG behoben: Testkasse signierte jede Bestellung, protokollierte TSE-Ausfälle und erzwang dadurch automatische Z-Berichte bei Stammdatenänderung/Update
[x] R138: jeder geparkte Bon wird als Bestellung-V1 abgesichert (AEAO zu § 146a Nr. 2.2.3.6.2, BMF Kassen-FAQ "länger als einen Tag") - keine Kassenbeleg-Transaktion bleibt offen, solange ein Bon wartet; Zahlung startet den Kassenbeleg (DSFinV-K 2.7.2). BUG behoben: ein gebuchter Verkauf, dessen TSE-Transaktion nach einem Absturz offen blieb, wurde beim nächsten Start als AVBelegabbruch beendet - jetzt mit seinen eigenen Belegdaten beendet. Rabatt als Bestellposition, damit alle Bestellungen dem Rechnungsbetrag entsprechen (BMF-FAQ)
[x] R139: Kassensturz bucht die Differenz als DifferenzSollIst (DSFinV-K Anhang C) - Überschuss als Einlage, Fehlbetrag als Entnahme, TSE-gesichert, im Export; Soll-Bestand läuft ab dem letzten bestätigten Kassensturz ohne Rücksetzen beim Z-Bericht weiter (Anfangsbestand/Geldtransit-Modell), Bestätigungsdialog mit NEU ZÄHLEN
[x] R140: Bon-QR-Code im Format der DSFinV-K Anhang I Tz. 2 (V0;Client-ID;processType;processData;TANR;SigZ;Start;Ende;Algorithmus;Zeitformat;Signatur;Public Key) statt TOR-eigenem Format (AEAO zu § 146a Nr. 2.4.1); TSE-Zeiten unverändert in UTC mit Millisekunden statt lokal/abgeschnitten (Nr. 2.4.4); Bon zeigt die von der TSE protokollierte Client-ID als Seriennummer. R81-Test hatte das eigene Format festgeschrieben (achter Fall)
[x] R143: während der Erfassung stornierte Positionen (SOFORT STORNO, -1, MENGE ×) gehören zum Beleg (DSFinV-K 4.2.3, AEAO zu § 146a Nr. 1.11.1) - gespeichert mit Verkauf/Training/Abbruch, im Export als erfasste Position plus Gegenposition mit negierter Menge; per Position geleerte gesicherte Bestellung wird als Bestellstorno abgesichert (R137-Lücke)
[x] R144: eine Seriennummer der Kasse (Kennung auf 30 Zeichen) für TSE-Client-ID, Bon, QR-Code, DSFinV-K KASSE_SERIENNR und Mitteilung nach § 146a Abs. 4 AO (AEAO Nr. 1.16.2.5, 2.2.3.1, 2.4.4 Nr. 6); Pflichtpunkt TSE-Client-ID = Kassen-Seriennummer; Menü Kassenmeldung mit den Mitteilungsdaten (AEAO Nr. 1.16.2) als Vorlage für Mein ELSTER
[x] R145: Digitaler Kassenbon über TOR Cloud - Kunde wählt nach dem Verkauf Papierbeleg oder Digitalbeleg (QR, Zustimmung nach AEAO zu § 146a Nr. 2.5.3, im Audit festgehalten); Seite "Ihr digitaler Kassenbon" auf eigener Domain bon.<domain> ohne Login mit PDF herunterladen/Teilen/Drucken. Der elektronische Beleg wird nach AEAO zu § 146a Nr. 2.5.6 in einem standardisierten Datenformat zur Verfügung gestellt; TOR bietet standardmäßig einen PDF-Download an. Getrennt vom Fiskalarchiv: Kassen-DB/TSE/DSFinV-K bleiben auf der Kasse (§ 147 AO), die Cloud-Kopie wird nach 90 Tagen gelöscht (Datenminimierung, keine gesetzliche 30-90-Tage-Regel). Digitalbeleg aus demselben Druckauftrag wie der Papierbeleg, nur Belegangaben, 256-Bit-Token (nur Hash gespeichert), TLS/HSTS, noindex, no-store; Cloud nicht erreichbar -> sofort Papierbeleg (Nr. 2.5.7). Lokaler WLAN-Bon-Server (R103/R115) entfernt
[x] R146: vor der Bestellannahme, bei einer Bestelländerung oder beim Bestellstorno stornierte Positionen gehören zum Bestelldatensatz (DSFinV-K 4.2.3) - als erfasste Position plus Gegenposition mit negierter Menge in Bestellung-V1 und Export, Summen unverändert; beim Parken abgebrochene Vorgänge behalten sie ebenfalls (R143-Lücke)
[x] R147: Trainingsbeleg mit seiner Trainingsbestellung verknüpft (DSFinV-K 2.7.1) - Schema 19 training_receipt_orders (unveränderbar), Export gibt TR-n den Abrechnungskreis der Bestelldatensätze wie beim echten Beleg
[ ] Echte Swissbit-TSE: viele Transaktionen, Absturz mit offener Transaktion
[x] R132: DSFinV-K 3.2 umgesetzt - Z-Bericht speichert die Stammdaten, unter denen er erfasst wurde (z_report_archive.master_data, Migration 12); Änderung der Firmendaten bei wartenden Vorgängen erzeugt vorher automatisch einen Kassenabschluss (unter den alten Daten), SettingsRepository verweigert jeden anderen Weg; nach einem Software-Update werden wartende Vorgänge beim Start unter der alten Version abgeschlossen; Export nutzt je Abschluss dessen eigene Stammdaten
[x] R131: DSFinV-K 2.4 Export gebaut - Menüpunkt schrieb bisher nie etwas ("GESPERRT"). Jetzt je Z-Bericht die 20 CSV-Dateien (Einzelaufzeichnung, Stammdaten, Kassenabschluss) + unveränderte offizielle index.xml/gdpdu-DTD (byteweise eingebettet) + TOR-EXPORTPROTOKOLL.txt; Spaltenlayout wird zur Laufzeit aus der offiziellen index.xml gelesen, jeder Wert gegen Länge/Typ geprüft. Storno = Beleg mit BON_STORNO=1 und Bon_Referenzen, Retoure = Beleg negativ, Pfand eigene Position, Angebot über Bonpos_Preisfindung, manueller Rabatt als Rabatt-Position je Steuersatz, Einlage/Entnahme als Einzahlung/Auszahlung, signierte Bestellungen als AVBestellung mit Abrechnungskreis, TSE-Ausfall mit Grund in TSE_TA_FEHLER (nicht weggelassen, nicht nachsigniert). Sperrt bei fehlenden Firmendaten/Steuernummer, unbekanntem Steuersatz, Storno ohne Ursprung in einem Abschluss. Unterwegs gefunden: Z-Bericht "Umsatz nach Storno/Retouren" war auf 0 geklemmt (Math.Max) - ein Zeitraum mit mehr Storno als Umsatz druckte 0,00, während die MwSt-Zeilen negativ waren; behoben
[x] R133: TSE-Stammdaten (Zertifikat, Public Key, Signaturalgorithmus, Zeitformat) aus dem TSE-eigenen TAR-Export (TR-03153/TR-03151) statt aus ungeprüften nativen Aufrufen; Zertifikat nur, wenn SHA-256 des Schlüssels = TSE-Seriennummer; nach Aktivierung und bei jedem TSE-Export übernommen, unveränderbar gespeichert (tse_master_data). Im Haus/Außer Haus je Verkauf (sales.im_haus), Export INHAUS 1/0
[x] DSFinV-K Datenerfassung (R131-Hinweise): mit R132-R137 erledigt; Hinweise erscheinen nur noch für Altdaten
[ ] R133: ersten TAR-Export einer echten Swissbit-TSE gegen den Leser prüfen (Hinweis TSE_STAMMDATEN muss verschwinden)
[x] R141: Z-/X-Bericht - Zahlarten Bar/Karte netto nach Storno/Retoure (Bar + Karte = Umsatz nach Storno/Retouren, wie DSFinV-K Z_Zahlart), neuer Abschnitt KASSENBEWEGUNGEN (Einlagen, Entnahmen, Kassendifferenzen) mit Bar-Saldo des Zeitraums
[x] R121: letzte offene Audit-Befunde F3/F4 sowie İ5/İ4 - (F3) eine Teilretoure wurde als "AVBelegabbruch" signiert, also als ABGEBROCHENER Vorgang ohne Beleg und ohne Geldbewegung; eine Retoure ist das Gegenteil: ein abgeschlossener Beleg mit negativen Positionen, jetzt "Beleg" mit unveränderter Referenz-Beleg-Nr (ein Alttest, R82, hatte auch dieses Fehlverhalten als Sollverhalten festgeschrieben - der dritte Fall in diesem Audit nach R78/R83). (F4) Vorgangsende kam bei fehlender TSE-Logzeit aus der PC-Uhr, ein erfundener Wert also, ausgegeben als TSE-Zeit - und die Belegprüfung auf Vorgangsende konnte deshalb nie anschlagen; jetzt wird die TSE-Logzeit unverändert durchgereicht und nur bei signierten Belegen verlangt (bei TSE-Ausfall erklärt der Ausfallhinweis das Fehlen). (İ5) manifest.json meldete Version 0.7.33.630, "Windows, Linux" und TSE/DSFinV-K/ZVT als "nicht enthalten", dazu ein doppelter revision-Schlüssel, der den korrigierten still überschrieb; Checkliste und Cloud-README nachgezogen. (İ4) Git ist auf diesem Rechner nicht installiert, daher kein Repository angelegt, aber alles vorbereitet: .gitignore, .github/workflows/ci.yml, 10-ALLE-TESTS.bat und VERSIONSKONTROLLE-UND-CI.md
[x] R120: Cloud-Befunde C1/C2/C4 geschlossen - ausgelieferte Update-Datei wird beim Download erneut gegen den Manifest-Hash geprüft (409 bei Abweichung); Rate-Limiter sperrte ab 10000 Einträgen ALLE Nutzer aus (jetzt Verdrängung) und zählte hinter dem Reverse Proxy alle auf eine IP (X-Forwarded-For nur mit TOR_CLOUD_TRUST_PROXY=true); abgebrochener Download konnte den Prozess beenden, Logging ohne Pfad/Stack, kein sauberes Herunterfahren - alles behoben. C5 (Demo-Zugang) geprüft und bewusst unverändert: server.js:43 verhindert Demobetrieb auf einem extern erreichbaren Host. Zusätzlich R119-Nachtrag: Abschnitt BLOCKIERTE KÜCHENBONS im Diagnose-Fenster mit Wiedervorlage
[x] R119: Audit-Befund İ2 geschlossen - Bestelldruck-Warteschlange brach beim ersten Fehler ab, ohne Versuchszähler und ohne Sicht darauf: ein Auftrag an einen nicht mehr existierenden Drucker blockierte ALLE folgenden Küchenbons dauerhaft. Jetzt max. 5 Versuche, danach Status FAILED (Warteschlange läuft weiter), mit Versuchszähler/Fehlergrund und Wiedervorlage
[x] R118: Audit-Befund G3 geschlossen - Anmeldesperre setzte failed_attempts beim Sperren auf 0 zurück, schenkte also nach jeder 5-Minuten-Sperre wieder fünf Versuche (~1440 Rateversuche/Tag). Zähler steigt jetzt bis zum erfolgreichen Login, Wartezeit verdoppelt sich (5/10/20/40, gedeckelt bei 60 Min) - Regel in TorPos.Core.LoginLockoutPolicy
[x] R117: Audit-Befund İ1 geschlossen - Kassensturz rechnete nach KALENDERTAG, Z-Bericht seit letztem Tagesabschluss. Schlimmer beim Beheben gefunden: GetOpenPeriodAsync startete bei max(Mitternacht, letzter Abschluss), sodass bei einem über Mitternacht geöffneten Betrieb die Umsätze zwischen letztem Abschluss und 00:00 in GAR KEINEM Z-Bericht auftauchten. Beide laufen jetzt ohne Mitternachtsgrenze ab dem letzten Tagesabschluss
[x] R116: Audit-Befund İ3 geschlossen - Verkaufssperre war invertiert: eine UNLIZENZIERTE Kasse wurde als "Entwicklungs-Testmodus" durchgelassen, eine LIZENZIERTE mit "KASSIEREN GESPERRT" abgewiesen (FiscalRelease ist ja immer false). Regel jetzt in TorPos.Core.SaleModePolicy; echter Verkauf braucht weiterhin Lizenz+Freigabe+Bereitschaft, alles andere verkauft als deutlich markierte Simulation statt gar nicht
[x] R115: Audit-Befund G2 geschlossen - Bon-Server lauschte auf IPAddress.Any (alle Schnittstellen, auch Gäste-WLAN/WAN) und lieferte Belegdaten unverschlüsselt aus; ohne Lese-Timeout, ohne Größenlimit, ohne Verbindungsobergrenze; Token liefen nie ab, obwohl die 404-Seite das immer behauptete. Jetzt: genau eine Bind-Adresse (konfigurierbar, sonst LAN-IPv4, sonst kein Start), 10s Anfrage-Timeout, max. 16 Verbindungen/8 KB Request/50 Header, Token-Lebensdauer receipt.digital_qr.ttl_hours (Standard 24h)
[x] R114: Audit-Befund G1 geschlossen - TorUpdateService hatte Zertifikats-Pinning UND Authenticode-Prüfung für Loopback-Update-Server komplett übersprungen (Server-URL steht in app_settings unter %APPDATA%, für das Kassiererkonto beschreibbar; das Setup wird danach mit "-Verb RunAs" elevated gestartet). Entscheidung jetzt als reine Funktion TorPos.Core.UpdateTrustPolicy; die Ausnahme gilt nur noch in Debug-Builds, ein Kunden-Release erzwingt Signaturprüfung überall
[x] R113: zwei KRITISCHE Fiskal-Befunde aus dem Gesamtaudit behoben - (F1) Verkauf/Bestellung wurde bei nicht-AKTIVER TSE oder fehlender Client-ID STILL unsigniert abgeschlossen: kein Ausfall-Eintrag, kein Audit-Eintrag und - da TseOutage=false blieb - KEIN gesetzlich vorgeschriebener TSE-AUSFALL-Hinweis auf dem Beleg; (F2) TseFailSafeService.ProbeAsync hatte nirgends einen Aufrufer und GetOpenAsync keinen Produktivaufrufer, ein offener Ausfall war also unsichtbar. Jetzt: ReportUnavailableAsync + Ausfall-Badge im Kassenkopf. Zwei Alttests (R78/R83) hatten das Fehlverhalten als Sollverhalten festgeschrieben und wurden korrigiert
[x] R112: Nutzerwunsch "etwas mehr neon" - Kacheln/Logo/KASSIEREN/GEMISCHT leuchten jetzt in ihrer jeweiligen Akzentfarbe, stärker beim Hover/Pressed; Header/Panels auf farbige statt schwarze Ambient-Glows umgestellt. Lehrreicher Build-Fehler unterwegs: BoxShadow existiert nur auf Border, nicht auf Button - via Effect/DropShadowEffect gelöst
[x] R78: direkter Kassenverkauf (KIOSK/IMBISS SALE) ruft TSE Start+Finish als ein Beleg auf; ProcessData-Format ist Entwurf, noch nicht gegen BSI TR-03153 verifiziert
[ ] Produktiver Z-Abschluss
[x] R148: Pfand der PFAND/LEERGUT-Taste im DSFinV-K-Export als Geschäftsvorfall Pfand statt Umsatz (Anhang C), Kassenabschluss trennt Pfand vom Umsatz
[ ] Pfand-Steuerlogik fachlich final validieren - offen: Leergut-Rücknahme (PfandRueckzahlung) nicht erfassbar; Flaschenpfand der Taste immer 19 % (bei 7-%-Ware falsch)
[ ] End-to-End Kassen-Nachschau Test


## Payment Terminal v0.6.1
[x] IPaymentTerminalService
[x] ZVT TCP/IP adapter
[x] Portalum.Zvt 3.4.0 stable package
[x] ZVT connection test
[x] ZVT registration test
[x] amount transfer on KARTE
[x] card sale only after terminal success
[x] Ingenico / CCV / Verifone profiles
[x] PAX / other ZVT test profile
[x] PCI-safe logging without PAN/PIN/CVV
[ ] Real Ingenico Germany acceptance test
[ ] Real CCV Germany acceptance test
[ ] Real Verifone/TeleCash acceptance test
[ ] ZVT serial/USB transport
[x] Refund/reversal UI (R102: RefundAsync via BON STORNO/Teilretoure, real Terminal-Akzeptanztest noch offen)
[x] Terminal end-of-day UI (R94: ZVT End-of-Day 06 50 - eigener Settings-Bereich, getrennt vom Verbindungsstatus)
[ ] SumUp official adapter
[ ] Stripe Terminal adapter
[ ] Adyen Terminal API adapter


## Swissbit Runtime v0.6.8
[x] WormAPI.dll dynamic loading
[x] official SDK folder/package hook
[x] API version/export diagnostics
[x] Windows TSE drive detection
[x] TSE info/serial/certificate expiry read
[x] guarded worm_tse_setup flow
[x] client registration
[x] self test / CTSS / time update
[x] StartTransaction
[x] UpdateTransaction
[x] FinishTransaction
[x] transaction response fiscal fields
[x] TAR export
[x] synchronized native SDK calls
[ ] official Hardware TSE 2 SDK package supplied to TOR test environment
[ ] real Hardware TSE 2 acceptance test
[ ] secure persistent TimeAdmin credential strategy
[x] fiscal ProcessData/ProcessType generator (Entwurf, siehe FiscalProcessData - nicht final verifiziert)
[x] Parken → Bestellung TSE lifecycle (R83: eigener "Bestellung-V1"-Vorgang bei ORDER-Annahme, getrennt vom Kassenbeleg-V1 bei Zahlung)
[x] sale → Kassenbeleg TSE lifecycle (R78, direkter Verkauf; R83 ergänzt IMBISS ORDER-Annahme)
[ ] DSFinV-K 2.4 end-to-end validation

## v0.7.33 – Bedienkomfort / Frontend Simplification
[x] Rakip inceleme yönü: POSprom + LaCash + KasseSpeedy + BlitzHandel + Blitzkasse
[x] Tasarım ilkesi: günlük kasiyer ekranında yalnız günlük işlemler
[x] Nadir işlemleri ana hızlı işlem şeridinden kaldır
[x] Bon-Historie / Einlage-Entnahme / Z-Bericht / Bon-Storno -> KASSE menüsü
[x] Rabatt / Geparkte Bons / Bon Ein-Aus doğrudan erişimde kalır
[x] Einstellungen ana navigasyonunu Basit / Erweitert olarak sadeleştir
[x] İlk kurulum sihirbazı: Firma -> Drucker -> TSE -> Kartenterminal -> Fertig
[x] Kasiyer ekranında bağlama duyarlı işlem görünürlüğü (Checkbox war nur nicht aktualisiert - bereits umfassend vorhanden: `RefreshSalesActionState` steuert Zahl-/Mengen-/Storno-/Rabatt-/Park-Buttons nach Warenkorbzustand+Rechten, der Settings-Reload-Block in `ReloadSettingsAsync` nach Rechten/Trainingsmodus/Business-Modus, dazu `RefreshImHausToggle`/`RefreshReceiptModeButtons`/`RefreshLicenseWarning` für ihre jeweiligen Zustände - kein fehlendes Feature, R100-Session-Audit bestätigt)
[x] Hata mesajlarını müşteri dili + Techniker-Details şeklinde ayır (R85: ScannerStatus zeigt nur Kategorie + Fehler-ID; volle Exception nur im CrashLog, per Fehler-ID-Suche im Diagnose-Fenster abrufbar)
[x] 3-tık kuralı: sık işlemler <= 1, yönetim işlemleri <= 3 dokunuş


## R48 ve sonrası – KIOSK / IMBISS rekabet boşlukları
[x] Yeni Artikel için otomatik, kalıcı Artikel-Nr.
[x] Mevcut numarasız Artikel kayıtlarını additive migration ile numaralandır
[x] IMBISS günlük Abholnummer / sıra numarası (Bon + Historie + Cloud)
[x] IMBISS Menü / Combo (örn. Döner + Pommes + Getränk) - R49'da zaten tamamlanmış, bu liste güncellenmemişti
[x] İkinci Küchenbon / Küchen-Drucker routing (R77: Warengruppe -> Küchenstation)
[ ] Arayüz dili: DE + TR + EN; ardından AR için RTL uygunluğu
[x] Sağlayıcı/middleware değerlendirmesi tamamlandı (`LIEFERPLATTFORM-INTEGRATION-BEWERTUNG-DE.md`) - öneri: Deliverect/allO gibi tek bir aggregator, doğrudan 3 platform entegrasyonu değil
[ ] Lieferando / Wolt / Uber Eats entegrasyon katmanı - aggregator seçimi + "TOR POS Cloud" webhook-endpoint kararı kullanıcıya ait, henüz verilmedi
[x] Tamamlanmış satış için gerçek Rückgabe / Storno fiskal karşı-vorgang (R79 tam bon, R82 kısmi/tekil ürün Retoure - ikisi de sadece BAR)
[x] Direct cutter / drawer via StarPRNT RAW spooler commands (R86)
[~] Paper-out: pre-print Windows status check added (R89); real Direct StarPRNT SDK status still needs official StarIO10 SDK or direct port access
[x] 10.000 Artikel / 500.000 Verkauf benchmark (R84: PASS, 12,4 s Gesamtdauer, 500k Sales+Items in 8,7 s; siehe verification/R74/)
[ ] Gerçek Windows PC + Scanner + Bon-Drucker + Kartenterminal uçtan uca saha kabul testi


## R49 – IMBISS Bestellablauf / Menü / Küche / Sprache
- [x] Menü/Combo direkt im Artikelstamm
- [x] Combo-Bestandteile werden für Lagerverbrauch verwendet
- [x] Zweiter Küchendrucker als eigener Windows-Drucker
- [x] Küchenbon über persistente TOR-Druckwarteschlange
- [x] UI-Sprache DE / TR / EN
- [x] Bon / Küchenbon / Berichte / fiskalische Ausdrucke bleiben Deutsch
- [x] Abholnummer optional: OFF / SALE / ORDER
- [x] ORDER: Abholnummer bei Bestellannahme, finaler Bon erst bei späterer Zahlung
- [ ] Weitere UI-Sprachen schrittweise ergänzen (z. B. AR/FR/ES)
- [x] Küchenrouting nach Warengruppe/Station (Grill, Fritteuse, Getränke) - R77
- [ ] Gemischte MwSt.-Menüs erst nach fiskaler Endvalidierung für Produktivbetrieb freigeben

## R51 – IMBISS Produktbilder / Login-Sprache / Stammdaten

- [x] Programmsprache DE/TR/EN direkt auf der Anmeldung auswählbar und persistent.
- [x] Gedruckte/fiskale Ausgaben bleiben Deutsch.
- [x] IMBISS-Starterhierarchie Speisen/Getränke repariert; KIOSK-Demogruppen im IMBISS ausgeblendet.
- [x] Gruppe/Warengruppe/Artikel als auditierte Soft-Deaktivierung löschbar (Admin).
- [x] 57 lokale Produktbilder für das IMBISS-Starter-Sortiment; Kundenbilder werden nicht überschrieben.
- [ ] Windows/.NET-Compile und echter Geräte-Abnahmetest.
