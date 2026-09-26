# TOR POS – Release-Index

<!-- TOR_RELEASE:R182|0.7.33.882|Merd-D -->

## Aktueller Release

**R182 · Merd-D · 0.7.33.882**

Verbindliche Quelle: `Desktop/src/TorPos.Core/ReleaseInfo.cs`.

## Unveröffentlicht (nach R182)

Diese Arbeit steht in `main`, ist aber **kein** Release: `ReleaseInfo` führt
weiterhin R182 / Merd-D / 0.7.33.882, und die fiskalische Produktionsfreigabe
bleibt geschlossen. Der Abschnitt wird beim nächsten Release in einen
Revisionsabschnitt überführt.

### Bedienoberfläche DE/TR/EN

- Die Bedienoberfläche läuft wahlweise auf **Deutsch, Türkisch oder Englisch**
  (Einstellungen → Alltag → Sprache). Deutsch ist Vorgabe und zugleich
  Nachschlagschlüssel: ein fehlender Eintrag zeigt das Original, eine
  unvollständige Übersetzung ist an einer echten Kasse damit harmlos.
- **Fiskalische Aufzeichnungen bleiben deutsch.** Bon, DSFinV-K, Z-Bericht,
  TSE-Prozessdaten und das Protokoll entstehen in Core/Infrastructure; beide
  Projekte referenzieren die Sprachschicht nicht, und eine Prüfung hält diese
  Grenze.
- Fachbegriffe behalten in jeder Sprache ihren Namen: Z-Bericht, X-Bericht,
  Z-Abschluss, DSFinV-K, TSE, DATEV, GoBD, § 146a. Betriebsdaten - Warengruppen,
  Artikelnamen, Gerätenamen, Beträge - werden nie übersetzt.
- Das Layout-Gate misst in allen drei Sprachen; eine Übersetzung, die einen Knopf
  zerschneidet, fällt jetzt in der CI auf statt an der Kasse.
- **TOR Restaurant und Restaurant Plus bleiben bewusst deutschsprachig.**

### TSE-Lebenszyklus an der Kasse

- **Start ohne betriebsbereite TSE**: jeder Zustand, der nicht signieren kann,
  schreibt eine Statuszeile und öffnet einmal pro Programmlauf ein
  Hinweisfenster. Zuvor sagten nur `Ready` und `Connected` überhaupt etwas -
  `NotFound`, der häufigste Fall einer Neuinstallation, erzeugte nichts.
- Die **Kasse wird dabei nicht gesperrt**: § 146a behandelt einen TSE-Ausfall als
  dokumentierten Ausfall, nicht als Grund den Betrieb anzuhalten. Das Fenster
  sagt beides - es kann weiterverkauft werden, und die Vorgänge sind in dieser
  Zeit nicht fiskal abgesichert.
- Eine **gesteckte TSE wird auch ohne SDK erkannt** (der Stick ist ein
  USB-Volume, bevor er eine API ist); der Zustand bleibt `SdkMissing`, es wird
  nichts signiert.
- **Stecken und Ziehen im laufenden Betrieb** werden bemerkt. Bewusst ohne
  Dialog: ein Modal mitten im Verkauf ist das, was eine Kasse nie tun darf.
- Das **TSE-AUSFALL-Badge** wird nach jedem Probe neu gezeichnet; zuvor zeigte
  die Kasse einen längst beendeten Ausfall weiter.
- **Ausfallliste** unter Erweitert / Techniker: Beginn, Ende oder „läuft noch“,
  Dauer und protokollierter Grund. `tse_outage_log` trug das seit jeher und ist
  gegen Löschen geschützt, aber es gab keinen Weg es anzusehen.
- **TSE-Uhr**: jeder Transaktionsrequest trug seit jeher ein `TimeAdminPin`-Feld,
  das keine der sechs Konstruktionsstellen je füllte. Eine TSE, die lange lag,
  scheiterte damit an jeder Signatur mit `0x1002`. Gespeichert wird ausschliesslich
  die TimeAdmin-PIN, opt-in und DPAPI-geschützt; Admin-PIN, PUK und
  Credential-Seed werden nirgends gespeichert.
- **Zertifikatsende** wird bewertet und nicht mehr nur gelesen: Warnung ab 90
  Tagen, kritisch ab 30, und ein abgelaufenes Zertifikat blockiert den
  Transaktionsstart unabhängig vom Stand der Freigabeunterlagen.
- **Das Lesen der TSE löscht die BSI-Zertifizierungsnummer nicht mehr.** Die
  WORM API liefert sie nicht; die Einstellungsseite schrieb den leeren Wert nach
  jeder Aktivierung zurück, sodass das Programmierungsprotokoll dauerhaft
  „FEHLT“ meldete.

### Freigabe-Gates

- Die physische TSE-Freigabe ist **nach Generation getrennt** (1, 1.1, 2). Die
  Freigabe einer Generation öffnet keine andere; eine unklare oder
  widersprüchliche Generation ist fail-closed.
- Die Generation wird **nicht mehr aus Hardware-/Softwarerevision oder
  Gerätebeschreibung abgeleitet** - diese Felder sind keine Generationsangabe.
  Ohne belegte Zuordnung bleibt sie unbekannt.
- **Cloud-TSE** ist als Naht vorhanden, signiert aber nichts: jeder Aufruf, der
  fiskalische Daten erzeugen würde, verweigert und erfindet weder
  Transaktionsnummer noch Signaturzähler noch Signatur. Die Freigabe wird **pro
  Anbieter** geführt, ein unbekannter Anbieter gilt nie als freigegeben, und die
  Cloud-Qualifikation geht nicht in `FiscalRelease.Enabled` ein - eine
  USB-Kasse wird davon nicht blockiert.

### Abnahme und Dokumentation

- Das Zertifikat des Produkts ist in der Hardware-Abnahme festgehalten:
  Swissbit TSE 2.0, **BSI-K-TR-0800-2026**, TR-03153, ausgestellt 21.04.2026,
  gültig bis 20.04.2034.
- `verification/HARDWARE-E2E-TEMPLATE.md` ist wieder reine **Vorlage**; der
  Betreiberbericht vom 23.09.2026 liegt als datierte Kopie daneben. Sechs von
  neun Zeilen der Startprüfung sind gemeldet, drei bleiben offen - zwei davon,
  bis das Swissbit SDK vorliegt.

### TOR Cloud und Kasse: Prüfbericht vom 24.09.2026

- **C-4:** Ein von der Cloud abgelehntes Ereignis blockiert die Synchronisation
  nicht mehr. Die Kasse fragt ein Urteil pro Ereignis an (`partial`), parkt
  Abgelehntes in `cloud_outbox_rejected` (Migration 42) und zeigt die Anzahl im
  Cloud-Status. Ältere Kassen bekommen weiter die Alles-oder-nichts-Antwort.
- **C-2:** Eigener Update-Kanal pro Produkt (`PUBLISH-UPDATE.ps1 -Edition`,
  `manifest-<EDITION>.json`); RESTAURANT bekommt nie das gemeinsame Setup.
  Provisionierung kennt RESTAURANT.
- **O-19:** Unbekanntes Terminalprofil ist gesperrt statt AUTO_ZVT.
- **O-4:** fiskaltrust Sign bricht nach 10 s ab; ftState-Ausfallbits lassen
  das Ergebnis scheitern.
- **O-5:** fiskaly-Client behält nur die letzten 256 abgeschlossenen Vorgänge.
- **O-7:** Abgebrochene Tisch-Zahlung blockiert den Tischplan nicht mehr.
- **O-16:** Erstellzeit der Systemidentität wird als UTC gelesen.
- **Cloud §7:** Bon-Detail mit Buchungstyp, Bar/Karte-Summenprüfung, Indizes,
  Heartbeat- und Mail-Aufräumen, Mail-Quote ohne Wettlauf, Origin/Host ohne 500,
  Umleitung mit Query, Demo-Hinweis nur im Demo-Betrieb. Der Heartbeat der Kasse
  meldet eine offene TSE-Störung.

### TOR Cloud (C-1, C-3, G-5)

- **C-1:** Caddy begrenzt `/api/v1/devices/mail/send` jetzt auf 12 MiB statt
  pauschal 2 MB; TOR-Mail-Berichte und DATEV-Anhänge bis 8 MB kommen wieder an.
  Alle anderen Routen bleiben bei 2 MB. Mit Caddy 2.10.2 geprüft.
- **C-3:** `/updates/*` und `/trial/*` hashen den Installer per Stream und
  merken sich das Ergebnis pro Dateiidentität; die Event-Loop blockiert nicht
  mehr. Die Prüfung gegen das Manifest bleibt bei jedem Download.
- **G-5:** Ist 2FA aktiv, verlangt ein Wechsel der Authenticator-App Passwort
  und aktuellen Code. Jeder TOTP-Code gilt nur einmal. Erfolgreiche Anmeldungen
  zählen nicht mehr zur Sperre pro E-Mail, und eine unbekannte E-Mail läuft
  durch dasselbe scrypt wie eine bekannte.

Safety-Baseline: **1410** Checks.

## Release-Historie

### R182

- **Produkttrennung:** aus demselben geprüften Quellcode entstehen zwei feste Produkte, **TOR Einzelhandel** und **TOR Gastro**. Getrennt sind Executable (`TOR-Einzelhandel.exe` / `TOR-Gastro.exe`), Windows-AppId, Installationsordner, Startmenü-/Desktop-Identität, Prozess-Mutex, Benutzer-Datenverzeichnis und maschinenweiter Lizenz-/Demo-Pfad. Beide Produkte sind parallel installierbar; eine Deinstallation löscht weder Daten noch das andere Produkt.
- Die gespeicherten Editionscodes bleiben **KIOSK** und **IMBISS**. Datenbank, Lizenzen, Cloud-Payloads und `edition.permanent.lock` auf Kundenrechnern tragen diese Werte, und die R181-Übernahme gleicht darauf ab.
- Der gemeinsame R181-Build bleibt als Rückfallweg erhalten und wird weiterhin gebaut und paketiert.
- **Datenübernahme aus R181:** backup-first und copy-once. Vor der Kopie entsteht ein gegen den Quellstand geprüftes Backup, die Zielinstallation erhält einen Provenance-Marker, und der Quellstand unter `%APPDATA%\TOR-POS-Pro` wird ausschließlich gelesen. Eine Übernahme erfolgt nur bei exakt passender, dauerhaft gebundener Edition.
- Eine nicht sauber beendete R181-Kasse behält committed Transaktionen in `torpos.db-wal`. Die Staging-Kopie wird deshalb **vor** jeder Datenbankprüfung und über `torpos.db` **und** `torpos.db-wal` verglichen; andernfalls wurde eine fehlerfreie Kopie als Quelländerung abgewiesen und das Produkt beendete sich ohne Fenster.
- Rollback-Qualifikation unterscheidet weiterhin einen verlustfreien Rückweg vor dedizierten Schreibvorgängen von einem gesperrten automatischen Rejoin danach; WAL-Inhalte zählen dabei als persistenter Stand.
- **Startfehler behoben:** Login- und Kassenfenster sowie der Startbildschirm luden das Markenbild über eine fest verdrahtete `avares://TorPos.App/...`-Adresse. Ein dediziertes Produkt benennt die Assembly um, und avares-Adressen sind an den Assemblynamen gebunden - die Produkte konnten dadurch überhaupt nicht starten. Die Fenster verwenden jetzt assemblyrelative Pfade.
- Zwei Fail-open-Wege geschlossen: der gemeinsame Build übernimmt keine von außen gesetzte `TOR_POS_PRODUCT_EDITION` mehr, und `InstallationEdition.EnforceAsync` weist in einem dedizierten Build jede abweichende Edition ab, statt sie anzuwenden.
- **Auslieferungszugang:** eine Kasse startet arbeitsbereit mit `admin` / `admin`, Personal-PIN `1234` und Trainingscode `0000`. Das Setup fragt keine Zugangsdaten mehr ab, und der erste Start wird nicht mehr durch einen erzwungenen Zugangsdialog blockiert. Wer den Zugang ersetzt, braucht mindestens vier Zeichen und genau vier PIN-Ziffern - kein 10-Zeichen-Zwang und kein Verbot von 1234/0000. Damit lässt sich in der Benutzerverwaltung erstmals auch ein vier- bis neunstelliges Admin-Passwort setzen.
- Eine Sitzung auf Auslieferungszugangsdaten bleibt im Audit-Log sichtbar (`ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED`); die Spur wird nun bei Verwendung des Auslieferungspassworts bzw. der -PIN geschrieben. Mitarbeiterkonten bleiben unverändert deaktiviert ausgeliefert.
- Das 7-Tage-Demo gilt **einmal pro PC und pro Produkt**; die maschinenweite Demo-Identität übersteht eine Deinstallation.
- Eine fest gebundene Kassenart wird auf dem Anmeldebildschirm mittig über die ganze Zeile und in Lesegröße dargestellt statt in einer Hälfte des Auswahlrasters.
- **CI startet die Produkte jetzt wirklich:** bauen und paketieren allein hat nie bewiesen, dass ein dediziertes Produkt hochkommt - genau deshalb konnte ein Build ausgeliefert werden, der beim Laden des Anmeldefensters abbrach. Die realen Fenster jedes dedizierten Builds, einschliesslich Startbildschirm und Anmeldung mit fest gebundener Kassenart, werden nun in der CI gerendert und geprüft.
- Der Hinweis auf dem Anmeldebildschirm nennt keine Mitarbeiter-Zugangsdaten mehr, die so nicht funktionieren: Mitarbeiterkonten werden weiterhin deaktiviert ausgeliefert und zuerst in der Benutzerverwaltung aktiviert.
- CI baut, published und paketiert beide Produkte und liefert `TOR-Einzelhandel-Setup.exe` und `TOR-Gastro-Setup.exe` als Artefakt. Safety-Baseline: **1159 Checks**.
- Keine fiskalische Produktionsfreigabe: alle sechs `FiscalRelease`-Nachweisflags bleiben geschlossen. Die erste physische Windows-Installationsabnahme wird über `verification/R182-SPLIT-INSTALL-ABNAHME.md` geführt; TSE-, Terminal- und Druckerabnahme bleiben davon getrennt.

### R181

- Scanner-Performance nach realem Kassentest: der frühere Standard `scanner.wait_ms=1000` führte bei HID-Scannern ohne empfangenen ENTER/TAB-Suffix zu rund einer Sekunde sichtbarer Verzögerung. Neuer Standard: **140 ms**.
- Bestehende Installationen mit dem unveränderten alten 1000-ms-Standard werden einmalig auf 140 ms migriert; anschließend bleibt eine bewusst gesetzte Benutzerkonfiguration erhalten.
- Schnelle Folgescans werden in einer begrenzten **FIFO-Warteschlange (max. 64 EAN)** gepuffert. Ein zweiter Barcode wird nicht mehr verworfen, nur weil der erste Artikel noch Preis-/Angebotslogik verarbeitet.
- Scannerabschluss über ENTER/TAB bleibt weiterhin der schnellste Pfad; der 140-ms-Fallback greift nur, wenn der Suffix nicht ankommt.
- Einzelhandel/Gastronomie sind als Betriebsprofile getrennt: Firmenname, Betreiber, Adresse, E-Mail/Telefon, Steuer-/USt-ID und Kassenbezeichnung werden pro Edition separat gespeichert. Im lizenzfreien Testbetrieb kann zwischen beiden Profilen gewechselt werden, ohne dass Stammdaten in die andere Edition durchschlagen.
- Die Ersteinrichtung wird pro Edition separat abgeschlossen; ein Testlauf in Einzelhandel markiert Gastronomie nicht mehr automatisch als eingerichtet.
- Sobald eine gültige kommerzielle Lizenz eine Edition bindet, wird eine dauerhafte Installationssperre geschrieben. Auf dem Login ist die andere Edition vollständig ausgeblendet und kann auch programmatisch nicht aktiviert werden. Die Bindung bleibt auch bei später abgelaufener/deaktivierter Lizenz bestehen.
- Zehn neue R181-Regressionsprüfungen sichern Scanner-Latenz/FIFO sowie Profiltrennung und Lizenz-Edition-Lock. Safety-Baseline: **1128 Checks**.
- R180 unknown-EAN/Caret-Fix sowie R179 Kassenschublade, Cloud- und Retourenänderungen bleiben erhalten.

### R180

- Scanner-Hotfix nach realem Kassentest: unbekannte EAN öffnen Stammdaten nicht mehr automatisch. Der Verkaufsbildschirm bleibt aktiv und zeigt die exakt empfangene EAN mit „EAN NICHT GEFUNDEN“ an.
- Bekannte EAN zeigen nach erfolgreichem Lookup „SCAN OK · … · EAN …“ und werden direkt dem Warenkorb hinzugefügt.
- Bei fokussiertem Scannerfeld ist TextInput die einzige Zeichenquelle; KeyDown sammelt die Ziffern nicht zusätzlich. Dadurch werden HID-Scans auf Geräten vermieden, die KeyDown und gebündeltes TextInput gleichzeitig liefern und sonst eine doppelte/falsche EAN erzeugen können.
- Der Scanner behält sein Fokusziel, der sichtbare blinkende Caret wird jedoch ausgeblendet.
- Die veraltete Einstellung für das automatische Unbekannt-EAN-Dialogfenster wurde entfernt; Stammdaten werden nur bewusst über WAREN geöffnet.
- 4 neue Regressionstests sichern dieses Verhalten. Safety-Baseline: 1118 Checks.

### R179

- Teilretoure: manueller Bon-Rabatt sowie Bar-/Kartenanteile werden kumulativ über bereits erfolgte Retouren verteilt. Einzelne Teilretouren können dadurch keine zusätzlichen oder fehlenden Rundungs-Cents mehr erzeugen; die letzte Teilretoure absorbiert den Rest exakt.
- TOR Cloud: STORNO und RETURN werden nun zusammen mit der lokalen Gegenbuchung in die persistente Cloud-Outbox geschrieben. In der Cloud erscheinen sie als Gegenbuchungen; der zugehörige Lagerverbrauch wird umgekehrt und Bestand entsprechend zurückgeführt.
- TOR Cloud: GEMISCHT wird im Portal als „Gemischt“ angezeigt. Cash-/Card-Anteile werden für neue Ereignisse ausdrücklich übertragen und für Auswertungen getrennt geführt.
- TOR Cloud: gewichtete Aktionsartikel werden mit Listenpreis, Aktionsrabatt und bereits vom Desktop berechnetem Positionsbetrag validiert. Gültige line-level Rundungen werden nicht mehr durch eine abweichende quantity×unit_price-Neuberechnung abgewiesen.
- Scanner: das 1×1-px transparente Fokusziel wurde durch ein sichtbares, fokussierbares EAN-/Barcode-Feld ersetzt. Scanner-Timing orientiert sich an der konfigurierten Wartezeit und verwirft suffixlose HID-Scans nicht mehr über die alte 170-ms-Heuristik.
- Scanner-Einstellungen: in diesem Build wird nur der tatsächlich implementierte HID-Keyboard-Wedge-Pfad angeboten; COM wird nicht mehr irrtümlich als produktiv auswählbarer Scannerpfad dargestellt.
- Kassenschublade: Aktivierung und DK-Ausgang werden in der Drucker-Zentrale gemeinsam gespeichert. Der echte Zahlungsweg verwendet denselben kanonischen Schalter; für R169–R178-Installationen gibt es eine begrenzte Legacy-Migration, damit bestehende funktionsfähige Konfigurationen nicht durch den früher standardmäßig falschen Schalter deaktiviert bleiben.
- Kassenschubladen-Test verwendet den gespeicherten DK-Ausgang 1/2 statt implizit immer Ausgang 1.
- 10 neue R179-Regressionsprüfungen sichern kumulative Retourenverteilung, Cloud-Gegenbuchungen, gewichtete Aktionsdaten, Scanner-Fokus/Timing und die vereinheitlichte Kassenschubladenkonfiguration. Safety-Baseline: 1114 Checks.
- Die sechs fiskalischen Produktionsfreigabe-Flags bleiben unverändert geschlossen. Scanner und Kassenschublade benötigen zusätzlich die reale Hardware-Gegenprobe am Zielsystem.

### R178

- Release-/Qualifikationsdokumentation auf den aktuellen Main-Stand synchronisiert; veraltete R149-/881-Referenzen werden entfernt.
- Architektur dokumentiert fiskaltrust/Swissbit nun korrekt als auf `main` integrierten Diagnose-/Client-Code, dessen produktiver Laufzeitpfad bis zur Realhardware-Abnahme weiterhin gesperrt bleibt.
- Verfahrensdokumentations-Statusmatrix mit 40 Prüffeldern ergänzt; geprüfte Bereiche, offene Nachweise und externe Hardware-/Fiskalabnahmen werden getrennt ausgewiesen.
- Storno/Retoure-Dokumentation präzisiert: `Kassenbeleg-V1`, umgekehrte Vorzeichen für Gegenbuchungen und `Bon_Referenzen` auf den Ursprungsbeleg werden getrennt beschrieben; die Ursprungsreferenz ist nicht Bestandteil der TSE-processData.
- Keine fiskalische Produktionsfreigabe: alle sechs `FiscalRelease`-Nachweisflags bleiben unverändert geschlossen. Der automatische Safety-/Regression-Baseline bleibt bei 1104 Checks.
- CI erzeugt nach erfolgreicher Prüfung weiterhin Kunden-Setup, Windows-Testpaket und ein ZIP des exakt committed Source-Trees für externe Code-Reviews.

### R177

- Kassenschublade: der bei Geräte → Kassenschublade erfolgreiche RAW-Impuls wird jetzt auch im echten Zahlungsweg verwendet. Die Lade öffnet nach dem dauerhaften SALE-Commit bei BAR bzw. GEMISCHT mit Baranteil; Bon-Ausgabe, Digitalbeleg und BON EIN/AUS beeinflussen die Öffnung nicht mehr.
- Die alte Kopplung der Schublade an `PrintReceipt` wurde entfernt, damit ein Papierbon die Lade nicht doppelt öffnet und ein Digitalbeleg die Öffnung nicht verhindert.
- Hardware-Test/Simulation verwendet denselben Zahlungs-Schubladenpfad (außer Training), damit die Funktion auch vor fiskaler Produktivfreigabe real getestet werden kann.
- Scanner: die Kassieroberfläche besitzt nun ein transparentes echtes TextBox-Fokusziel wie die Artikelverwaltung. Nach Touch/Klick, Dialog-Rückkehr und Checkout wird der Scannerfokus automatisch wiederhergestellt.
- Scanner: Enter/Tab bleibt sofortiger Abschluss; wenn der HID-Suffix unter Windows/Avalonia verloren geht, wird ein schneller numerischer Barcode-Block nach kurzem Idle trotzdem verarbeitet.
- 6 neue R177-Regressionsprüfungen sichern Scanner-Fokus/Suffix-Fallback und die einmalige, bonunabhängige Schubladenöffnung.

### R176

- Teilretoure: gramajlı/kampanyalı bir satırın birden fazla iadeye bölünmesinde her parçayı bağımsız yuvarlamak yerine kümülatif cent dağıtımı kullanılır; son parça kalan cent'i emer ve tüm iadelerin toplamı orijinal satıra tam eşit olur.
- Kart iadesinden önce tutar artık doğrudan repository'nin `QuoteReturnAsync` sonucundan alınır; terminale gönderilen kart iadesi ile daha sonra veritabanına yazılan Retoure aynı hesap yolunu kullanır.
- Kart refund güvenliği: terminalden APPROVED gelen Storno/Retoure kilidi, ilgili veritabanı karşı kaydı başarıyla commit edilene kadar kaldırılmaz; aradaki crash/DB hatası ikinci refund denemesini otomatik olarak engeller.
- Kampanya işletme günü: hiç Z-Abschluss bulunmayan eski/aktarılmış veritabanlarında ilk tarihî satış işletme gününü sabitlemez; ilk Z sınırı oluşana kadar yerel gün kullanılır.
- fiskaltrust için yerel Middleware v1 `/json/v1/Sign` istemcisi eklendi. Start-/POS-Receipt durumları, DE Charge-/Pay-Case sabitleri ve TSE imza alanlarının ayrıştırılması tipli modellerle hazırlanır.
- Normal SALE için ChargeItems/PayItems eşlemesi hazırlandı: 19/7/0 MwSt., Außer-Haus flag'i, Pfand, manuel Rabatt, gemischte Menü-MwSt., kg/gramaj ve Bar/Karte split'i cent bazında reconcile edilir.
- TOR'un genel `Karte` bilgisi debit/credit diye tahmin edilmez; fiskaltrust kart türü açıkça bilinmiyorsa işlem fail-closed olur. STORNO/RETURN fiskaltrust eşlemesi de doğrulanmadan etkinleştirilmez.
- AccessToken kod içine alınmaz; R176 istemcisi yalnızca yerel loopback Queue'ya izin verir.
- FiscalComplianceService artık tüm readiness kontrolünü global SQLite IoQueue içinde tutmaz; identity/settings repository'leri kendi DB erişimini serialize eder, böylece readiness kontrolü gereksiz global kuyruk kilidi ve gelecekteki nested-lock riskini taşımaz.
- Satış ekranı scanner girişi güçlendirildi: TextBox odaklı Artikelverwaltung dışında da çalışan HID keyboard-wedge cihazları için rakamlar artık Window-level KeyDown üzerinden de toplanır; aynı tuş için TextInput da gelirse çift EAN oluşmaması için dedupe uygulanır.
- Kassenschublade/Auto-Cut protokolü düzeltildi: Epson için ESC/POS (ESC p / GS V), Star için StarPRNT (ESC BEL + BEL/SUB / ESC d) ayrı kullanılır. Drucker-Zentrale'de Ausgang 1/2 seçimi vardır ve RAW spooler hatalarında Win32 hata kodu gösterilir.
- Gerçek fiskaltrust transaction runtime-provider seçimi ve Physical-TSE-E2E bayrağı kapalı kalır. Yeni Swissbit TSE ile client registration ve gerçek Start/Finish/TAR/DSFinV-K kabul testi tamamlanmadan produktif fiskal sürüm açılmaz.

### R175

- fiskaltrust + Swissbit yolu için read-only tanılama eklendi: ProgramData altındaki fiskaltrust servis konfigürasyonu okunur, Swissbit SCU/devicePath tespit edilir, TSE_INFO.DAT varlığı kontrol edilir, SCU portu ve Queue /json/v1/Echo bağlantısı sınanır.
- Tanılama hiçbir AccessToken okumaz/göstermez; client registration, TSE activation, PIN/PUK değişikliği veya fiskal transaction çağrısı yapmaz.
- 21.09.2026 manuel testinde fiskaltrust Queue REST ve Swissbit SCU gerçek Swissbit USB TSE'ye ulaştı; mevcut başka kasaya ait TSE client kayıtlı olmadığı için `Client not registered` aşamasında bilinçli olarak duruldu.
- FISKALTRUST_SWISSBIT manifestte alternatif sağlayıcı olarak belgelendi; gerçek transaction path ve physical TSE E2E bayrakları kapalı kalır.
- R175 regresyon kontrolleri bu read-only sınırı ve fiskal release gate'inin kapalı kalmasını CI'da kilitler.

### R174

- Kampanya takvimi artık açık Z-/işletme dönemini izler; gece yarısında devam eden servis sırasında kampanya tarihi istemeden değişmez.
- Gramajlı ürünlerde kampanya desteği cent-exact line-level hesapla etkinleştirildi; checkout ve Teilretoure aynı matematiği kullanır.
- Checkout snapshot gramajlı satırlarda kg birimini korur.
- PowerShell 5.1 simulator doğrulaması uyumlu hale getirildi; kaynak ZIP'inde repository hygiene açıkça skip edilirken CI -RequireGit ile fail-closed kalır.
- R174 için 8 yeni regresyon kontrolü eklendi; safety baseline 1070 kontrol olarak güncellendi.

### R173

- Drucker-Zentrale: Windows-Spooler-Metadaten werden konsequent über OpenPrinterW/GetPrinterW als Unicode gelesen; fehlerhafte CJK-/Mojibake-Zeichen bei Treiber/Port werden verhindert.
- Epson-Erkennung: Office-/Multifunktions-/Fax-Drucker wie ET-4850 gelten nicht mehr als Bondrucker. Nur bekannte Epson-TM-Profile bzw. eindeutig POS-/Receipt-typische Epson-Warteschlangen werden freigegeben.
- Nicht geeignete Office/PDF/Fax-Drucker bleiben zur Diagnose sichtbar, können aber nicht als Bondrucker übernommen, getestet oder für die Kassenschublade verwendet werden.

### R172

- SQLite: verbliebene unabhängige Schreibpfade für Karten-Erstattungsstatus, DATEV-Outbox/Kassenbuch und Schema-Migrationen laufen über die zentrale FIFO-IoQueue; WAL/busy-timeout bleiben aktiv.
- Swissbit: native WORM-API Geräte-, Transaktions-, Aktivierungs- und TAR-Aufrufe laufen standardmäßig in einem isolierten Hilfsprozess mit Hard-Timeout; bei Timeout/Abbruch wird der Worker-Prozess beendet und der bestehende TSE-Ausfallpfad greift.
- Mengen/Bestand: neue Primärspeicherung als skalierte INTEGER-Milli-Einheiten (1 kg = 1000, also 1 g = 1); REAL-Spalten bleiben nur als Abwärtskompatibilitäts-Mirror/Fallback für Altbestände erhalten.
- CI: R172-Regressionsvertrag prüft FIFO-Single-Writer, Watchdog-Isolation, Fixed-Point-Pfade und erzeugt nach erfolgreicher Prüfung ein ZIP des exakt committed Source-Trees.

### R171

- DSFinV-K 2.4 Export: frei wählbare Von-/Bis-Kalendertage, Preflight und Zielordnerauswahl; Export bleibt auf vollständige Z-Abschlusszeiträume begrenzt.
- Nach erfolgreichem DSFinV-K-Export bietet TOR direkte Weitergabe an: kompletter Ordner auf USB/Datenträger oder vollständiger Export als ZIP per aktivem TOR-Mail/Google/SMTP-Versand an frei wählbare E-Mail, inklusive Steuerberater-Schnellauswahl.
- Release-Metadaten nach R149-Stagnation auf R171 / 0.7.33.871 synchronisiert; Versionsprüfung wird gegen neue Review-Revisionen gehärtet.
- R169/R170 Funktionen (Drucker-Zentrale, Kassenschubladen-Test, Gewichtsartikel/Waage) sind damit erstmals in einer fortgeschriebenen zentralen Release-Revision enthalten.

## Warum es viele R*-Dateien gibt

Die einzelnen `R*-CHANGELOG.md`-, Review- und Startdateien dokumentieren jeweils den historischen Stand einer bestimmten Änderung. Sie werden bewusst nicht als „aktuelle Version“ interpretiert.

Ein älteres Dokument wie R75.1 ist daher **kein Versionskonflikt**, solange die zentrale Versionsquelle und ihre Spiegeldateien auf denselben aktuellen Release zeigen.

## Versionskonsistenz

Bei jedem CI-Lauf werden mindestens diese Angaben gegeneinander geprüft:

- `TorRelease.Version`
- `TorRelease.Revision`
- `TorRelease.ReleaseName`
- `manifest.json`
- `TorPos.App.csproj`
- `TOR-POS-Pro-Setup.iss`
- Release-Marker in `README.md` und `CHANGELOG.md`

Bei einer Abweichung schlägt CI fehl. Damit kann z. B. ein neuer R150-Commit nicht mehr versehentlich mit R149-Installer- oder Manifestdaten ausgeliefert werden.

## Pflege bei einem neuen Release

1. Zuerst `Desktop/src/TorPos.Core/ReleaseInfo.cs` aktualisieren.
2. Die gespiegelten Versionsfelder in Manifest, App-Projekt und Installer angleichen.
3. Die Release-Marker in `README.md` und `CHANGELOG.md` aktualisieren.
4. CI ausführen; erst bei grüner Versionsprüfung releasen.

Historische Changelog-Dateien bleiben unverändert erhalten.
