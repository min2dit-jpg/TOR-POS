# TOR POS – Release-Index

<!-- TOR_RELEASE:R178|0.7.33.878|Merd-M -->

## Aktueller Release

**R178 · Merd-M · 0.7.33.878**

Verbindliche Quelle: `Desktop/src/TorPos.Core/ReleaseInfo.cs`.

### R178

- Müşteri update teslimatı TOR Cloud kurulumuna bağımlı olmaktan çıkarıldı. Her build varsayılan olarak `https://updates.torpos.de/` adresindeki resmî TOR update servisini kullanır; teknisyen isterse test amacıyla ayrı endpoint tanımlayabilir.
- İki dağıtım kanalı eklendi: `PILOT` kontrollü saha denemeleri için, `STABLE` normal müşteriler için. Eski istemcilerin kullandığı `manifest.json` STABLE olarak korunur; PILOT bağımsız `pilot-manifest.json` kullanır.
- Update sunucusu yalnızca etkin STABLE/PILOT manifestinin referans verdiği hash-adlı installer dosyalarını servis eder ve download anında SHA-256'yı tekrar doğrular.
- Yayın araçları `PUBLISH-UPDATE.ps1 -Channel STABLE|PILOT` ve `DISABLE-UPDATE.ps1 -Channel STABLE|PILOT` ile kanalları bağımsız yönetir.
- İstemci update kontrolünde edition yanında kanal bilgisini de gönderir. Normal müşteri varsayılanı `STABLE`; PILOT seçimi yalnızca teknik ayarlarda yapılır.
- İndirilen installer için mevcut SHA-256 + pinned Authenticode doğrulaması aynen korunur. TOR üretim code-signing sertifika thumbprint'i build içine sabitlenmeden uzaktan kurulum fail-closed kalır.
- Update kurulumu öncesi veritabanı backup'ı oluşturulur. TOR POS temiz şekilde kapandıktan sonra installer yükseltilmiş yetkiyle sessiz çalışır, exit code kaydedilir ve yalnızca başarılı kurulumdan sonra TOR POS otomatik yeniden açılır.
- Scanner saha düzeltmesi: satış ekranındaki gerçek `ScannerCapture` TextBox artık barkod verisinin birincil kaynağıdır. Scanner odaktayken ENTER/TAB suffix her durumda tüketilir; suffix bir Warengruppe/geri/default düğmesini tetikleyemez. Window-level TextInput fallback aynı karakterleri ikinci kez eklemez.
- Kassenschublade saha düzeltmesi: test butonu ve gerçek BAR/GEMISCHT ödeme aynı `drawer_channel` değerini kullanır. Başarılı fiziksel test çekmeceyi otomatik etkinleştirir ve kullanılan kanalı kalıcı ayarlara yazar; ödeme yolu ayarı cache yerine doğrudan kalıcı settings'ten yeniden okur.
- R178 için update-delivery regresyonları, pilot/stable yayın izolasyonu, resmî endpoint davranışı ve scanner/çekmece saha bulguları CI tarafından denetlenir.

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
