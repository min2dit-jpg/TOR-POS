# TOR POS – Release-Index

<!-- TOR_RELEASE:R181|0.7.33.881|Merd-M -->

## Aktueller Release

**R181 · Merd-M · 0.7.33.881**

Verbindliche Quelle: `Desktop/src/TorPos.Core/ReleaseInfo.cs`.

### R181

- Scanner-Performance nach realem Kassentest: der frühere Standard `scanner.wait_ms=1000` führte bei HID-Scannern ohne empfangenen ENTER/TAB-Suffix zu rund einer Sekunde sichtbarer Verzögerung. Neuer Standard: **140 ms**.
- Bestehende Installationen mit dem unveränderten alten 1000-ms-Standard werden einmalig auf 140 ms migriert; anschließend bleibt eine bewusst gesetzte Benutzerkonfiguration erhalten.
- Schnelle Folgescans werden in einer begrenzten **FIFO-Warteschlange (max. 64 EAN)** gepuffert. Ein zweiter Barcode wird nicht mehr verworfen, nur weil der erste Artikel noch Preis-/Angebotslogik verarbeitet.
- Scannerabschluss über ENTER/TAB bleibt weiterhin der schnellste Pfad; der 140-ms-Fallback greift nur, wenn der Suffix nicht ankommt.
- Vier neue Regressionstests sichern Legacy-Migration, 140-ms-Fallback, FIFO-Verarbeitung und die Entfernung der früheren `_scanProcessing`-Capture-Sperre. Safety-Baseline: **1122 Checks**.
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
