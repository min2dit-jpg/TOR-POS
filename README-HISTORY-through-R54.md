# Aktuell: R54 – Bestellübersicht, Deutsch, Stabilität

Siehe `../0-BURADAN-BASLA-R54.txt` und `../R54-INCELEME-VE-YOL-HARITASI.md`.
Nur Deutsch; keine mitgelieferten IMBISS-Produktbilder. TEST_ONLY bleibt aktiv.
Die folgenden Abschnitte dokumentieren frühere Entwicklungsstände.

> **Aktuell: R51.1 UI-Sprach-Hotfix.** Login-Sprache gilt jetzt auch für verschachtelte Menüs; Ausdrucke bleiben Deutsch. Details: `R51.1-CHANGELOG.md`.

> **Aktuell: R51 IMBISS Sortiment / Produktbilder / Login-Sprache / Stammdaten-Löschen.** Basis ist R50. Details: `R51-CHANGELOG.md`.

# Aktuell: R51 · IMBISS Bedienung & Stammdaten

R51-Schwerpunkte: DE/TR/EN direkt bei der Anmeldung wählen, IMBISS-Starterartikel sauber unter Speisen/Getränke und Döner/Burger/Fingerfood/Pizza/Getränke einordnen, KIOSK-Demogruppen im IMBISS ausblenden, Gruppe/Warengruppe/Artikel sicher deaktivieren und allen 57 IMBISS-Starterartikeln lokale Produktbilder geben. Bon/Küchenbon/Abholschein/Berichte/Fiskaltexte bleiben Deutsch.

---

> **Aktuell: R50 IMBISS Menü/Combo + Küchendrucker + UI-Sprache + wählbarer Bestell-/Abholnummern-Ablauf.** Basis ist die vom Benutzer bestätigte R48.1-Stabilitätsfassung. Details: `R49-CHANGELOG.md`.

# Aktuell: R49 · IMBISS Bestellablauf

Maßgeblich für diesen Stand: `../0-BURADAN-BASLA-R49.txt`, `R49-CHANGELOG.md` und `../verification/R49-TEST-RESULTS.md`.

R49-Schwerpunkt:
- Menü/Combo direkt im Artikelstamm, Komponentenbestand beim Verkauf
- optionaler zweiter Küchendrucker über die bestehende sichere Druckwarteschlange
- Programmsprache DE/TR/EN; Bon, Küchenbon, Berichte und fiskalische Ausdrucke bleiben Deutsch
- Abholnummer wählbar: OFF / SALE / ORDER
- ORDER: Nummer bei Bestellannahme, Küchenbon/Abholschein sofort, finaler Bon erst beim späteren Kassieren

---

> **Aktuell: R48 Artikelnummer + IMBISS Abholnummer.** Änderungen: `R48-CHANGELOG.md`. R47 Waren/Bestand sowie R46/R45/R43 Bedien- und Cloud-Funktionen bleiben erhalten.

# Aktuell: R48 · Automatische Artikel-Nr. + Abholnummer

Maßgeblich für diesen Stand: `../0-BURADAN-BASLA-R48.txt`, `R48-CHANGELOG.md` und `../verification/R48-TEST-RESULTS.md`.

R48-Schwerpunkt:
- Neue Artikel erhalten automatisch eine fortlaufende, stabile Artikel-Nr. ab 100000; bestehende Nummern bleiben unverändert.
- Bereits vorhandene Artikel ohne Artikel-Nr. werden beim Datenbank-Upgrade additiv nachnummeriert.
- IMBISS kann pro erfolgreichem Verkauf eine tägliche Abholnummer 001, 002, 003 … vergeben; am nächsten Kalendertag beginnt sie wieder bei 001.
- Abholnummer erscheint groß auf dem Bon sowie in Bon-Historie und TOR POS Cloud; sie ersetzt niemals die fiskale Bonnummer.
- TRAINING zeigt eine eigene sitzungsbezogene Abholnummer, ohne die spätere Produktivsequenz zu verbrauchen.
- R47 Schnellartikel, Einkaufspreis/Mindestbestand, Inventur-Scannerfluss und Cloud-Bestand bleiben erhalten.

Ältere Abschnitte unten dokumentieren historische Entwicklungsstände und dürfen nicht als aktueller Fertigstellungsstand gelesen werden.

# TOR POS Pro – Commercial Core v0.7.33

Bu paket, Python/Tkinter prototipinden ayrı olarak ticari ürün için yeni çekirdektir.

## Hedef müşteri
- Kiosk / Späti
- Imbiss / Döner
- Bäckerei / Café hızlı satış

Tisch ve Küchenbon bu ürün hattında yok.

## Teknoloji
- .NET 10
- C#
- Avalonia 12.1.2
- Microsoft.Data.Sqlite 10.0.11
- Windows ilk hedef, Linux ikinci hedef

## V0.1'de olanlar
- Kiosk / Imbiss mod düğmesi
- USB/Bluetooth HID scanner için hızlı EAN tamponu
- Barcode -> RAM dictionary araması
- Resimli ürün tuşları
- Produktverwaltung
- Ürün resmi dosyadan seçme ve TOR veri klasörüne kopyalama
- Barcode / EAN
- SKU
- MwSt.
- Pfand
- Birim
- Pizza vb. için Klein / Mittel / Groß varyant sistemi
- Sepet RAM'de
- BAR / KARTE satış kaydı
- SQLite WAL
- barcode unique index
- kısa satış transaction
- performance counters
- başlangıç demo verisi

## Özellikle henüz yok
- TSE
- DSFinV-K
- ZVT
- gerçek bon yazıcı modülü
- kasa çekmecesi
- Kundenanzeige
- stok
- cloud/dealer portal

TSE konuşulana kadar ekranda TEST / TSE NICHT VERBUNDEN kalır.

## Veri klasörü
Windows:
%APPDATA%\TOR-POS-Pro\

## Derleme
.NET 10 SDK kurulu bilgisayarda:

Windows:
build-windows.bat

Linux:
chmod +x build-linux.sh
./build-linux.sh

Bu ZIP kaynak koddur; hazır EXE değildir.


## Windows tek adım kurulum
`1-KURULUM-VE-BASLAT.bat` dosyasını çalıştırın.

- .NET 10 SDK yoksa WinGet ile `Microsoft.DotNet.SDK.10` paketini kurmayı dener.
- Sonra restore + build + self-contained publish yapar.
- Hazır EXE `publish\win-x64\` klasöründedir.
- Self-contained yayın nedeniyle müşterinin POS bilgisayarında .NET SDK kurulu olmak zorunda değildir.


## v0.2 – Office / Einstellungen

İki POSprom kılavuzundaki Einstellungen yapısı incelenerek Office bölümü yeniden tasarlandı.

Yeni bölümler:
- Allgemein
- Firmendaten
- Funktionen
- Zahlarten
- Steuern
- Bon & Rechnung
- Geräte-Manager
- Scanner
- Datensicherung
- Personal & Rechte
- System

Ayarlar `app_settings` SQLite tablosunda kalıcı tutulur.
Kasa ekranı işletme modu, ödeme butonları ve scanner davranışını ayarlardan okur.
Backup klasörü test edilebilir ve manuel SQLite backup oluşturulabilir.

Bu sürümde TSE / DSFinV-K / ZVT hala bilinçli olarak devre dışıdır.


## v0.4 – Installationsversion & TSE-Aktivierung

### KIOSK / IMBISS wird bei der Installation gewählt
`1-KURULUM-VE-BASLAT.bat` fragt vor der Installation:
- KIOSK
- IMBISS

Die Auswahl wird in `%APPDATA%\TOR-POS-Pro\edition.lock` gespeichert.
Wenn die Installation erneut gestartet wird, wird die vorhandene Auswahl übernommen.
Im laufenden Kassenprogramm kann die Version nicht geändert werden. Das Setup zeigt
die vorhandene Auswahl bei einem Update an und erlaubt dem Administrator, bewusst
zwischen KIOSK und IMBISS zu wechseln.

KIOSK startet mit Scanner-/Barcode-Fokus.
IMBISS startet mit Schnellwahl-Fokus.

### TSE-Aktivierung unter Einstellungen
Neuer Menüpunkt: `Einstellungen -> TSE-Aktivierung`

Vorbereitet sind:
- TSE-Status
- Hersteller
- TSE-Art (USB / SD / Cloud)
- TSE-Seriennummer
- BSI-Zertifizierungsnummer
- Client-ID / Kassen-ID
- Gerätepfad / Anschluss
- Aktivierungsdatum
- Zertifikatsablauf
- Auto-Connect
- Admin-PIN / PUK als temporäre Eingabe

Admin-PIN und PUK werden nicht gespeichert.

Wichtig: In v0.4 ist die echte TSE-Hardwareaktivierung noch bewusst gesperrt.
Der Button `TSE AKTIVIEREN` wird erst nach Implementierung und Test des konkreten
TSE-Adapters freigegeben.


## v0.4 – Swissbit Hardware-TSE standardı

TOR POS'un standart fiziksel TSE profili:
- Hersteller: Swissbit
- Produkt: Swissbit Hardware TSE 2
- Anschluss: USB
- Provider-ID: SWISSBIT_HARDWARE

Amaç: müşteri TSE'yi belirli bir TOR satıcısından almak zorunda kalmasın.
Uyum kontrolü satıcıya göre değil, gerçek Swissbit TSE donanımı + geçerli sertifika
+ resmi Swissbit SDK uyumluluğuna göre yapılacak.

Swissbit, Hardware TSE 1 / 1.1 / 2 nesilleri için tek ve aynı SDK arayüzünü kullandığını
açıklamaktadır. TOR adapteri bu ortak SDK katmanına göre tasarlanmıştır.

DİKKAT:
Her Swissbit markalı USB bellek TSE değildir.
TOR'un standart satın alma önerisi Swissbit Hardware TSE 2 USB'dir.

### Şu anki teknik durum
`ITseProvider` ve `SwissbitHardwareTseProvider` hazırdır.
Üretici SDK fonksiyon adları tahmin edilmemiştir.
Gerçek imzalama/aktivasyon için resmi Swissbit SDK/driver paketi temin edilip
`ISwissbitSdkBridge` arayüzüne bağlanmalıdır.

Admin-PIN ve PUK kalıcı olarak saklanmaz.


## v0.4.1 – Desktop installation

Kurulum artık derlenen programı geçici `publish` klasöründen çalıştırmak yerine şu kullanıcı
program klasörüne kopyalar:

`%LOCALAPPDATA%\Programs\TOR POS Pro\`

Kurulum sırasında kullanıcıya şu soru gösterilir:

`Desktop-Verknüpfung erstellen? [J/N]`

`J` seçilirse Windows'un gerçek Desktop klasörü otomatik bulunur ve şu kısayol oluşturulur:

- `TOR POS Pro - KIOSK`
- veya `TOR POS Pro - IMBISS`

Kısayol kurulu EXE'yi açar. Böylece ZIP/proje klasörü daha sonra taşınsa bile masaüstü
bağlantısı bozulmaz.


## v0.4.2 – Star mC-Print3 MCP31CBI

TOR POS'un standart Bondrucker profiline şu model eklendi:

- Star Micronics mC-Print3
- Model family: MCP31
- Kullanıcıdaki model: MCP31CBI
- 80 mm thermal receipt
- Windows ilk hedef
- Resmi Star Windows Software / printer driver üzerinden GDI/Spooler yazdırma

`Einstellungen -> Geräte-Manager` altında:
- STAR DRUCKER SUCHEN
- TESTBON DRUCKEN
- Windows printer name
- printer last test/error
- Auto-Cut driver setting
- cash drawer via mC-Print3 driver/DK port hazırlığı

Satıştan sonra `receipt.auto_print=true` ve Bondrucker aktifse bon otomatik olarak
TOR printer queue'ya gönderilir.

Önemli performans kararı:
Windows spooler çağrısı Avalonia UI thread üzerinde yapılmaz.
Yazıcı kapalı veya yavaş olsa bile satış ekranının donmaması hedeflenir.

StarPRNT SDK için adapter katmanı ileride doğrudan printer status, cutter ve drawer
kontrolleri için eklenebilir; temel bon baskısı için Windows driver yolu şimdiden hazırdır.


## v0.4.3 – Normaler Windows-Installer

Müşteri artık BAT dosyasıyla kurulum yapmaz.

Geliştirici / bayi bir kez:
`1-SETUP-ERSTELLEN.bat`

çalıştırır. Bu dosya:
1. TOR POS'u self-contained Windows uygulaması olarak publish eder.
2. Gerekirse Inno Setup 6'yı kurar.
3. `TOR-POS-Pro-Setup.exe` isimli normal Windows installer oluşturur.
4. Setup EXE'yi proje içindeki `installer-output` klasöründe tutar ve oradan başlatır; Desktop'a kopyalamaz.

Müşteriye sadece:
`TOR-POS-Pro-Setup.exe`
verilir.

Müşterinin gördüğü normal kurulum sihirbazı:
- Willkommen
- Installationsordner
- KIOSK / IMBISS seçimi
- Desktop-Verknüpfung checkbox (varsayılan seçili)
- Installieren
- Fertigstellen / TOR POS starten

KIOSK / IMBISS seçimi kurulumdan sonra `edition.lock` ile kilitlenir.
Mevcut TOR POS kurulumunda edition.lock varsa güncelleme sırasında sürüm seçimi değiştirilemez.

Güncel kurulum klasörü:
`C:\Program Files\TOR POS Pro`

Müşteri bilgisayarında .NET SDK gerekmez; final uygulama self-contained publish edilir.


## v0.5.0 – Windows kurulum + zorunlu giriş

Müşteri paketi artık `TOR-POS-Pro-Setup.exe` olarak tasarlanmıştır.

Kurulum sırası:
1. Windows Setup / Willkommen
2. Kullanım şartları – kabul edilmeden devam edilemez
3. Kurulum klasörü
4. KIOSK veya IMBISS seçimi
5. Admin şifresi ve 4 haneli PIN belirleme
6. Installieren
7. Desktop'a `TOR POS Pro` kısayolu ZORUNLU oluşturulur
8. Fertigstellen -> TOR POS Pro başlar
9. Program açılmadan önce Login ekranı gelir

Yeni kurulumda varsayılan değerler:
- Benutzername: `admin`
- Passwort: `admin`
- PIN: `1234`

Kurulum ekranında parola ve PIN değiştirilebilir. Değiştirilirse eski `admin` şifresi / `1234` PIN'i arka kapı olarak tutulmaz; yalnızca yeni bilgiler geçerli olur.

Güvenlik:
- şifresiz / girişsiz kullanım yok
- Admin tam yetkili
- password/PIN PBKDF2-SHA256 ile hashlenir
- installer'dan gelen geçici credential dosyası ilk program açılışında okunup silinir
- update sırasında mevcut admin şifresi/PIN üzerine yazılmaz

Desktop kısayolu optional değildir; Windows'un gerçek kullanıcı Desktop klasörüne setup tarafından her kurulumda oluşturulur.


## v0.5.1 – Windows build fix

Windows testinde görülen `CS1656` derleme hatası düzeltildi.

Sebep:
`using var _ = ...` ile `_` gerçek bir değişken haline geliyordu ve daha sonra
`_ = PrintReceiptAndReportAsync(...)` satırı bu değişkene atama olarak yorumlanıyordu.

Düzeltme:
performans scope değişkenleri açık isimlerle tanımlandı.

Ayrıca:
- App ve Infrastructure hedefi `net10.0-windows` oldu.
- Avalonia `Watermark` uyarıları `PlaceholderText` ile düzeltildi.
- Build script artık eski `bin/obj/publish` klasörlerini temizliyor.
- Derleme çıktısı `TOR-POS-build.log` dosyasına yazılıyor.
- Başarılı derlemeden sonra normal `TOR-POS-Pro-Setup.exe` oluşturulup açılıyor.


## v0.5.2 – Inno Setup compiler fix

Windows testinde Inno Setup derleyicisinde görülen hata düzeltildi:

`Unknown identifier 'IntToHex'`

Inno Setup Pascal Script içinde `IntToHex` kullanımı kaldırıldı.
Kurulum sırasında admin parolasını bootstrap dosyasına UTF-16 hexadecimal olarak
geçirmek için artık TOR'un kendi `HexDigit` / `Hex4` fonksiyonları kullanılıyor.

Bu değişiklik yalnızca installer script compiler hatasını düzeltir; parola yine
açık metin olarak kalıcı saklanmaz. Uygulama ilk açılışta bootstrap verisini okuyup
PBKDF2 hash'e dönüştürür ve geçici bootstrap dosyasını siler.


## v0.5.3 – Inno Pascal local-const fix

Windows/Inno Setup testinde görülen:

`BEGIN expected`

hatası düzeltildi.

Sebep:
Kullanılan Inno Setup Pascal Script derleyicisi `HexDigit` fonksiyonu içindeki
local `const` bloğunu kabul etmedi.

Düzeltme:
Local `const` tamamen kaldırıldı ve hexadecimal karakter dönüşümü yalnızca
standart `case` ifadesiyle yapılıyor.

Kurulumdaki kullanım şartları, KIOSK/IMBISS seçimi, admin şifre/PIN kurulumu,
zorunlu login ve Desktop kısayolu korunmuştur.


## v0.5.4 – Inno SetFileAttributes fix

Windows/Inno Setup testinde görülen:

`Unknown identifier 'SetFileAttributes'`

hatası düzeltildi.

Inno Setup Pascal Script içindeki desteklenmeyen `SetFileAttributes(...)`
çağrısı kaldırıldı.

İlk admin parolası/PIN için kullanılan geçici bootstrap dosyası yine sadece
ilk açılış için oluşturulur. TOR POS ilk güvenlik kurulumunda bu dosyayı okur,
parolayı/PIN'i PBKDF2-SHA256 hash olarak veritabanına yazar ve bootstrap dosyasını
hemen siler.

Dosyayı "hidden" yapmak güvenlik sağlamadığı için bu özelliğin kaldırılması
kimlik doğrulama güvenliğini düşürmez.


## v0.5.5 – Scanner ohne EAN-Feld-Fokus

Ana kasa ekranındaki sürekli EAN TextBox kaldırıldı.

Yeni kullanım:
- Barkod okuyucu için hiçbir kutuya tıklamak gerekmez.
- POS ana penceresi açık olduğu sürece scanner tuşları global olarak yakalanır.
- Scanner EAN gönderdiğinde ürün otomatik aranır ve sepete eklenir.
- Bilinmeyen EAN mevcut ayara göre hızlı ürün oluşturma ekranını açabilir.
- Scanner ENTER suffix gönderiyorsa işlem ENTER ile anında tamamlanır.
- ENTER suffix olmayan scanner için timeout tabanlı otomatik tamamlama da desteklenir.

Manuel arama için ana ekranda yalnızca:
`EAN SUCHEN`
butonu kalmıştır. Bu buton ayrı bir manuel EAN arama penceresi açar.

Böylece kasiyer günlük kullanımda EAN kutusuyla uğraşmaz.


## v0.5.6 – Verkaufsliste sadeleştirildi

Aktueller Verkauf listesindeki ürün satırları sadeleştirildi.

Eski görünüm:
`1 x 10,00 € = 10,00 €`

Yeni görünüm:
`1 x 10,00 €`

Böylece satır içinde gereksiz `= ...` tekrarını kaldırdık.
Toplam bilgisi aşağıda daha net gösterilir.

Alt bölümde artık:
- `Artikel / Stück` özeti
- `Zwischensumme`
- `Rabatt`
- `GESAMT`

ayrı ayrı gösterilir.

Birden fazla ürün olduğunda toplam aşağıda net şekilde takip edilir.


## v0.5.7 – Bon parken + Z-Sperre

### Bon parken
Kasa ekranına:
- `PARKEN`
- `GEPARKTE BONS (n)`

eklendi.

PARKEN:
- aktif sepeti ayrı `parked_receipts` tablosunda saklar,
- gerçek satış oluşturmaz,
- normal Bon numarası tüketmez,
- `sales` tablosuna girmez,
- bu nedenle satış/Z toplamlarına karışmaz.

GEPARKTE BONS:
- açık bonları numara, saat, ürün adedi ve toplamla listeler,
- seçilen bon sepete geri alınır,
- BAR/KARTE ile gerçek satış tamamlandığında parked kayıt `CASHED` olur.

Geri alınmış parked bon program kapanırsa OPEN kalır; yapılan değişiklikler kapanışta
tekrar parked kayda yazılmaya çalışılır.

### Z-Abschluss güvenlik kuralı
`DailyClosingGuard` eklendi.

Kural:
- bir tane bile `OPEN` geparkter Bon varsa Z-Abschluss `Allowed=false`,
- mesaj: geparkte Bons zuerst kassieren,
- tüm parked bonlar kasada tamamlanmadan Z raporu üretme akışı açılamaz.

Bu kural fiskal Z-Bericht modülü devreye alındığında zorunlu kapı olarak kullanılmalıdır.
Parked bonlar zaten `sales` tablosunda olmadığı için Z toplamına dahil edilmez.


## v0.6.0 – Deutschland Fiscal Hardening

Alman AO/KassenSichV/BMF/BZSt gereklilikleri baz alınarak yasal hazırlık katmanı eklendi.

Önemli:
Bu sürüm halen `TESTBETRIEB`tir. Gerçek Swissbit SDK/TSE ve DSFinV-K ihracı
tamamlanmadan üretim modu açılamaz.

Yeni:
- immutable TOR eAS serial
- `Recht & Fiskal` dashboard
- user-overridable olmayan production gate
- append-only audit_log
- sales/sale_items program içi UPDATE/DELETE engeli
- EINLAGE / ENTNAHME
- TSE outage log schema
- test receipt üzerinde açık `TESTBON - KEIN STEUERBELEG`
- VAT summary
- future TSE receipt fields
- §146a Kassenmeldung status/date documentation
- Verfahrensdokumentation template
- DSFinV-K 2.4 implementation plan


## v0.6.1 – Kartenzahlung / ZVT

Yeni Einstellungen bölümü:
`Kartenzahlung / Terminal`

Aktif teknik yol:
- ZVT
- TCP/IP
- Portalum.Zvt 3.4.0 (MIT)
- Amount transfer from TOR POS to payment terminal
- payment result required before card sale is committed
- terminal connection test
- ZVT registration test

TOR profilleri:
- AUTO_ZVT
- Ingenico ZVT
- CCV ZVT
- Verifone / TeleCash ZVT
- PAX / provider-dependent ZVT
- Other ZVT

Ayrı entegrasyon gerektirenler:
- SumUp
- Stripe Terminal
- Adyen Terminal API

Güvenlik:
TOR tam PAN, track data, PIN veya CVV/CVC saklamaz.


## v0.6.2 – Windows startup fix

Windows testinde installer başarıyla bitmesine rağmen TOR POS penceresinin
açılmaması sorunu için startup lifecycle yeniden düzenlendi.

Ana düzeltme:
`Application.OnFrameworkInitializationCompleted()` artık `async void` değildir.
Uygulama framework startup tamamlanmadan önce mutlaka bir `StartupLoadingWindow`
atar. Ağır DB/auth/catalog başlangıcı bu pencere açıldıktan sonra asenkron yapılır.

Bu sayede Avalonia'nın MainWindow henüz atanmamışken uygulamayı sonlandırması
engellenir.

Ayrıca:
- Başlangıçta TOR loading ekranı görünür.
- Startup ve crash log:
  `%LOCALAPPDATA%\TOR POS Pro\Logs\TOR-POS-startup.log`
- Hata olursa Startfehler penceresi log yolunu gösterir.
- Internal Windows publish artık single-file değildir.
  Müşteri yine sadece tek `TOR-POS-Pro-Setup.exe` alır.
- Bu yaklaşım SQLite/native/Avalonia bağımlılıklarında daha güvenlidir.
- Desktop ve Start Menu kısayollarına `WorkingDir={app}` eklendi.


## v0.6.3 – Star printer compile fix

Windows build testinde görülen iki C# derleme hatası düzeltildi:

- `ValidateFiscalReceipt` bulunamadı
- `BuildTaxSummary` bulunamadı

Sebep:
v0.6.0'da bu metodların çağrıları eklendi, fakat helper metodları dosyaya
eklenmeden kaldı.

v0.6.3'te helper metodları ve `TaxSummary` modeli `StarMcPrint3PrinterService`
içine gerçek olarak eklendi.


## v0.6.4 – Avalonia Window namespace fix

Windows build testinde görülen:

`CS0246: Der Typ- oder Namespacename "Window" wurde nicht gefunden`

hatası düzeltildi.

Sebep:
Startup lifecycle v0.6.2'de yeniden yazılırken `App.axaml.cs` içinde
`Window` tipi kullanılmasına rağmen `using Avalonia.Controls;` eksik kalmıştı.

Düzeltme:
`using Avalonia.Controls;` eklendi.

Bu hata installer hatası değil, C# compile hatasıydı; bu nedenle önceki
sürümde Setup EXE üretilemiyordu.


## v0.6.5 – Schneller Entwickler-Build

`1-SETUP-ERSTELLEN.bat` wurde auf inkrementellen Build umgestellt.

Vorher:
- bin/obj wurden bei jedem Lauf komplett gelöscht
- publish wurde komplett gelöscht
- `dotnet restore` lief separat jedes Mal
- danach wurde erneut `dotnet publish` ausgeführt

Jetzt:
- bin/obj bleiben als Build-Cache erhalten
- kein separates Restore; `dotnet publish` restored nur bei Bedarf
- Folge-Builds kompilieren nur Änderungen neu
- Inno Setup wird weiterhin normal erzeugt

Zusätzlich:
`2-SETUP-SAUBER-NEU.bat`

Dieser vollständige Clean-Build ist für Kunden-Releases, Paket-/SDK-Änderungen
oder bei verdächtigen Cache-Problemen vorgesehen.


## v0.6.6 – KIOSK / IMBISS Auswahl erzwingen

Kurulum ekranında artık KIOSK veya IMBISS otomatik işaretlenmez.

Yeni ilk kurulum:
- KIOSK = seçilmemiş
- IMBISS = seçilmemiş
- Kullanıcı iki seçenekten birini bilinçli olarak seçmeden "Weiter" ilerlemez.

Ayrıca eski test/kurulumdan AppData içinde kalmış `edition.lock`, program gerçekten
kaldırılmışsa yeni kurulumu artık yanlışlıkla KIOSK/IMBISS'e kilitlemez.

Gerçek update davranışı korunur:
Eğer `%LOCALAPPDATA%\\Programs\\TOR POS Pro\\TorPos.App.exe` halen kuruluysa mevcut
edition kilitli kalır ve update sırasında değiştirilemez.


## v0.6.7 – Kassenoberfläche

Ana kasa ekranı yeniden düzenlendi:

- `Z-BERICHT` artık ana panelde.
- Z için Einstellungen'a gitmek gerekmez.
- Açık geparkte Bon varsa Z butonu doğrudan engeller.
- Fiskal mod henüz TEST ise gerçek Z üretmez; bunu açık şekilde bildirir.
- `Sonstiges` kategori kaldırıldı.
- Eski test verilerindeki Sonstiges ürünleri kaybolmaması için Schnellwahl'a taşınır.
- +1 / -1 / STORNO / RABATT daha büyük.
- PARKEN / GEPARKTE BONS / EINLAGE-ENTNAHME daha büyük.
- Gesamt fiyat artık tüm panelin altında ayrı durmaz.
  Ürünlerin göründüğü `AKTUELLER VERKAUF` penceresinin en altına taşındı.


## v0.6.8 – Swissbit WORM API Runtime

Swissbit TSE katmanı artık yalnızca boş interface değildir.

Eklendi:
- WormAPI.dll dinamik yükleme
- SDK version / export diagnostics
- TSE Windows drive auto-detection
- real device metadata read
- guarded first activation
- Admin / TimeAdmin / Client handling
- self-test / CTSS / time update
- StartTransaction / UpdateTransaction / FinishTransaction
- transaction number / signature counter / signature / time read
- TAR export
- synchronized single-thread vendor SDK access
- SDK DLL auto-package support when official files are supplied

TOR yine Swissbit DLL'lerini dağıtmaz. Resmi SDK dosyaları:
`vendor\swissbit\windows64\`

Global fiscal production lock remains enabled until full end-to-end fiscal validation.


## v0.6.9 – C / Anzeige leeren

Ana kasa ekranına büyük kırmızı `C` butonu eklendi.

Davranış:
- mevcut sepeti tamamen temizler
- Rabatt ve Gesamt sıfırlanır
- yeni satış için ekran hemen hazır olur
- işlem Audit-Log'a yazılır

Güvenlik:
Eğer ekranda geri çağrılmış bir `geparkter Bon` varsa `C` bu parked kaydı
silmez. Sadece aktif görünümü temizler ve orijinal bon `GEPARKTE BONS`
listesinde açık kalır.


## v0.7.0 – Swissbit SDK automatisch finden

- `SDK AUF PC SUCHEN`: sucht nach kompatibler WormAPI.dll.
- `WORMAPI.DLL AUSWÄHLEN`: manuelle Auswahl.
- `SWISSBIT DOWNLOAD-CENTER`: öffnet die offizielle Swissbit-Seite.
- `TSE SUCHEN`: klare Bezeichnung für den TSE-Test.
- Gefundene DLL wird auf benötigte WORM-API-Exports geprüft.
- Der Pfad wird in `%APPDATA%\TOR-POS-Pro\swissbit-sdk-path.txt` gespeichert.
- Die DLL selbst wird nicht aus fremden Installationen kopiert.
- `4-SWISSBIT-SDK-AUF-PC-SUCHEN.bat` wurde ergänzt.


## v0.7.3 – Safe Stammdaten architecture / startup recovery

Bu sürüm v0.7.0'ın açılan/stabil temelinden yeniden oluşturuldu.

Önemli değişiklik:
`categories` tablosu artık ALTER edilmiyor.

Yeni Stammdaten ilişkisi tamamen ek tablolarla tutuluyor:
- `product_groups`
- `category_master_data(category_id, group_id, vat_rate)`

Böylece eski müşteri veritabanının mevcut `categories` ve `products`
tabloları yapısal olarak değiştirilmeden kullanılmaya devam eder.

Sıra:
1. GRUPPE
2. WARENGRUPPE
3. ARTIKEL
4. VARIANTEN / GRÖSSEN

Vergi:
- Warengruppe'de 7 veya 19 seçilir
- Artikel Warengruppe seçildiğinde bu oranı otomatik alır
- Artikel kaydında manuel vergi seçimi yoktur
- Warengruppe vergisi değişirse bağlı Artikel kayıtları güncellenir

Ayrıca:
`5-STARTPROBLEM-PRUEFEN.bat`
kurulu TOR POS'u başlatır ve startup logunun son satırlarını gösterir.


## v0.7.4 – Windows compile fix

Windows build ekranındaki gerçek hatalar düzeltildi:

- `CS1626`: `yield return` bir `try` bloğunda `catch` ile birlikte kullanılmıştı.
  - `CandidateLibraries()` artık normal bir `List<string>` oluşturup döndürüyor.
  - `EnumerateFilesBounded()` artık sonuçları listeye topluyor; iterator/yield kullanmıyor.
- `CS8604`: Star yazıcı PrintPage event'inde `e.Graphics` nullable uyarısı.
  - Graphics null kontrolü eklendi.

Bu değişiklikler Stammdaten yapısını değiştirmez.
`Gruppe → Warengruppe → Artikel` ve Warengruppe tabanlı 7/19 MwSt. mantığı aynen korunur.


## v0.7.5 – SettingsWindow FilePicker compile fix

Windows build ekranındaki iki `CS0246` hatası giderildi:

- `FilePickerOpenOptions` bulunamıyordu
- `FilePickerFileType` bulunamıyordu

Sebep:
`SettingsWindow.axaml.cs` içinde `Avalonia.Platform.Storage`
namespace'i eklenmemişti.

Eklenen satır:
`using Avalonia.Platform.Storage;`

Bu değişiklik yalnızca derleme hatasını düzeltir; TSE, Stammdaten,
Gruppe → Warengruppe → Artikel ve Warengruppe tabanlı MwSt. mantığı
değiştirilmemiştir.


## v0.7.6 – Premium Kassen-Bedienfeld

Ana kasa ekranının sağ alt kontrol bölümü referans tasarıma göre yeniden düzenlendi.

Yeni düzen:
- üst sıra: +1 / -1 / STORNO / RABATT / C
- orta bölüm: PARKEN / GEPARKTE BONS
- ikinci orta sıra: EINLAGE / ENTNAHME / Z-BERICHT
- alt sıra: BAR / KARTE

Tasarım:
- koyu lacivert premium panel
- eşit boşluklar ve hizalar
- 12–14 px yuvarlak köşeler
- nötr işlemler mavi-gri
- C kırmızı
- Z-BERICHT sarı/turuncu
- BAR yeşil
- KARTE mavi
- dokunmatik kullanım için daha büyük hedefler
- durum metinleri ayrı TextBlock üzerinden dinamik güncellenir


## v0.7.7 – Kassenpanel ikinci düzenleme

Kullanıcı geri bildirimine göre sağ kasa paneli yeniden hizalandı.

### Yeni sıra
1. `+1 | -1 | SOFORT STORNO | RABATT | C`
2. `BON STORNO | PARKEN | GEPARKTE BONS`
3. `EINLAGE / ENTNAHME | Z-BERICHT`
4. `BAR | KARTE`

### Aktueller Verkauf
Alt işlem alanlarının toplam yüksekliği küçültüldü ve sağ panel 560 px yapıldı.
Böylece `AKTUELLER VERKAUF` alanı önceki sürüme göre daha geniş ve daha yüksek.

### Storno ayrımı
- `SOFORT STORNO`: sadece açık satıştaki seçili satırı kaldırır ve audit-log yazar.
- `BON STORNO`: tamamlanmış bon için ayrı işlemdir. Üretim TSE/DSFinV-K entegrasyonu
  tamamlanmadığı için v0.7.7'de gerçek fiskal karşı kayıt oluşturmaz; kullanıcıya
  açıkça TESTBETRIEB uyarısı gösterir.
- Tamamlanmış satışlar silinmez/değiştirilmez.


## v0.7.8 – Fiskal / Fail-Safe / ZVT Hardening

Kullanıcı tarafından iletilen teknik gözlem maddeleri incelendi.

### Gerçekten eksik olan ve tamamlananlar
- append-only Audit-Log için CSV export eklendi
- TSE outage repository + otomatik open/close mekanizması eklendi
- TSE fail-safe wrapper eklendi
- TSE-Ausfall fiş validasyonu düzeltildi:
  - gerçek outage durumunda olmayan TSE alanları uydurulmaz
  - fiş açık şekilde `TSE-AUSFALL` olarak işaretlenebilir
- ZVT payment connect timeout eklendi
- payment command timeout artık `UNGEKLAERT_TIMEOUT` döndürür
  - otomatik tekrar ödeme yapılmaması için açık uyarı verir
- Star Windows-driver testbonuna `Ä Ö Ü ä ö ü ß €` karakter testi eklendi
- startup preflight:
  - `PRAGMA quick_check`
  - Swissbit SDK/runtime warning log
- `DsfinvkExportService` ve Preflight eklendi
  - sahte/eksik DSFinV-K ZIP üretmez
  - Z-Kassenabschluss, per-sale TSE data ve resmi descriptor eksikse exportu bloklar

### Zaten mevcut olan
- Swissbit TAR export mekanizması zaten v0.6.8'den beri mevcut
- append-only local audit_log zaten vardı
- yazıcı zaten background queue üzerinde çalışıyordu
- Avalonia donanım event'leri doğrudan UI'yi cross-thread değiştirmiyordu

### Bilinçli olarak hâlâ kapalı
Tam DSFinV-K 2.4 üretim exportu.
Resmi field-order/index.xml descriptor ve gerçek Z/TSE veri modeli tamamlanmadan
TOR hiçbir zaman "yaklaşık DSFinV-K" üretmeyecek.


## v0.7.9 – Letzter Bon / Bon Ein / Bon Aus

Kasa paneline yeni Bon satırı eklendi:

`LETZTER BON | BON EIN | BON AUS`

### LETZTER BON
- veritabanındaki son tamamlanmış satışı yükler
- yeni satış oluşturmaz
- Bon numarasını artırmaz
- aynı bonu `BON-KOPIE · LETZTER BON` olarak yazdırır
- işlem audit-log'a `LAST_RECEIPT_REPRINT` olarak yazılır

### BON EIN
- `receipt.auto_print=true`
- satış tamamlanınca otomatik Bon basılır
- aktif buton yeşil olarak gösterilir

### BON AUS
- `receipt.auto_print=false`
- satış tamamlanınca otomatik Bon basılmaz
- aktif buton kırmızı/koyu kırmızı olarak gösterilir
- `LETZTER BON` manuel baskısı çalışmaya devam eder

Panel alt satırları biraz sıkıştırıldı; `AKTUELLER VERKAUF` alanının
mümkün olduğunca büyük kalması hedeflendi.


## v0.7.10 – SettingsWindow CS7036 compile fix

Windows build logundaki gerçek hata düzeltildi:

`CS7036: SettingsWindow(...) için currentUser parametresine karşılık gelen argüman yok`

Sebep:
v0.7.8'de `SettingsWindow` constructor'ına `IDsfinvkExportService`
eklenmişti, ancak `MainWindow.OnSettingsClick()` içindeki çağrı aynı sıraya
güncellenmemişti.

Düzeltme:
`_compliance` ile `_audit` arasına `_dsfinvkExport` eklendi.

Doğru sıra:
- settings
- backup
- performance
- tseProvider
- receiptPrinter
- paymentTerminal
- compliance
- dsfinvkExport
- audit
- currentUser

v0.7.9'daki LETZTER BON / BON EIN / BON AUS özellikleri korunmuştur.


## v0.7.11 – Kassenpanel final layout refinement

Kasa paneli kullanıcının onayladığı referans düzene göre yeniden hizalandı.

- Sağ kasa alanı 560 px → 610 px genişletildi.
- AKTUELLER VERKAUF daha geniş ve daha yüksek yapıldı.
- Sepet alanı ayrı iç panel olarak büyütüldü.
- GESAMT alanı ve 0,00 € daha büyük ve daha okunaklı.
- +1 / -1 / SOFORT STORNO / RABATT / C aynı genişlikte.
- BON STORNO / PARKEN / GEPARKTE BONS aynı genişlikte.
- LETZTER BON / BON EIN / BON AUS aynı genişlikte.
- EINLAGE / ENTNAHME ve Z-BERICHT aynı genişlikte.
- BAR ve KARTE diğer işlemlerden daha büyük ve birbirine eşit.
- Panel aralıkları küçültüldü ve hizalar düzeltildi.
- v0.7.10 fonksiyonları değiştirilmedi; sadece görsel düzen güncellendi.

## v0.7.13 – Düzenli kasa paneli ve PFAND

- Sağ panel üç net bölüme ayrıldı: Aktueller Verkauf, işlem butonları ve ödeme.
- Aktueller Verkauf yüksekliği ekranla birlikte esner; düşük çözünürlükte butonlarla çakışmaz.
- Tüm ikincil işlem butonları aynı yükseklikte ve kendi satırlarında eşit genişliktedir.
- BAR ve KARTE en altta daha büyük, eşit iki ödeme butonu olarak kalır.
- PFAND seçim penceresi 8, 15 ve 25 Cent ile Kiste leer 1,50 € ve Kiste voll 3,30 € seçeneklerini içerir.
- PFAND seçimi sepete eklenir ve audit log'a yazılır.

## v0.7.14 – Tam dolu eşit panel ve ilk kurulum seçimi

- Kasa panelindeki 16 buton 4 x 4 düzene alındı.
- BAR ve KARTE dahil bütün panel butonları aynı genişlik ve yüksekliktedir.
- Butonlar arasında boşluk yoktur; kontrol alanı tamamen kullanılır.
- Yeni kurulumda KIOSK / IMBISS seçim sayfası etkin ve zorunludur.
- Doğrudan EXE çalıştırılan ilk açılışta da program içi KIOSK / IMBISS seçim ekranı açılır.
- Seçim yalnızca ilk kurulumda yapılır; mevcut kurulum güncellenirken kilit korunur.

## v0.7.15 – Gerçek UniformGrid panel

- Sabit 320 px buton alanı kaldırıldı.
- 16 panel butonu gerçek 4 x 4 UniformGrid içine taşındı.
- Her buton Stretch, Margin 0 ve aynı hücre ölçüsünü zorunlu kullanır.
- İşlem alanı ekran yüksekliğinin kalan bölümünü otomatik doldurur.
- Butonlar arasında RowSpacing, ColumnSpacing veya dış boşluk bulunmaz.

## v0.7.16 – IMBISS seçimi, sabit Artikel kaydı ve ticari lisans temeli

- Setup güncelleme sırasında mevcut KIOSK/IMBISS seçimini gösterir; seçim kilitli
  değildir ve IMBISS bilinçli olarak seçilebilir.
- Doğrudan ilk çalıştırmada da KIOSK/IMBISS seçim penceresi korunur.
- Artikel formunun sağ tarafı kaydırılabilir; `NEUER ARTIKEL`,
  `ARTIKEL SPEICHERN` ve `SCHLIESSEN` düğmeleri sabit alt çubukta kalır.
- `Einstellungen → Lizenzierung` altında installations-ID, aktivasyon isteği,
  RSA imzalı lisans dosyası içe aktarma ve süre/edition doğrulaması eklendi.
- Lisans üretim aracı `dealer-tools` altındadır ve müşteri Setup'ına eklenmez.
- Ticari lisans TSE/DSFinV-K ürün onayı değildir ve TESTBETRIEB kilidini kaldırmaz.
- Doğrudan ve dolaylı üçüncü taraf bileşenler için dağıtım kontrolü
  `THIRD-PARTY-NOTICES.md` ve `COMMERCIAL-RELEASE-CHECKLIST-DE.md` içinde tutulur.

## v0.7.17 – Kunden-Nr. + tek-PC aktivasyonu

- Aktivasyon isteğinde bayi tarafından verilen zorunlu `Kunden-Nr.` kullanılır.
- Program, Installations-ID + Windows MachineGuid + sistem diski seri bilgisinden
  geri döndürülemez SHA-256 tabanlı bir `PC-Gerätecode` üretir.
- Lisans dosyası Kunden-Nr., müşteri adı, Installations-ID, PC-Gerätecode,
  KIOSK/IMBISS seçimi ve geçerlilik tarihini birlikte RSA ile imzalar.
- Başka bilgisayara kopyalanan lisans `WRONGINSTALLATION` olarak reddedilir.
- Ham MachineGuid veya disk seri numarası aktivasyon dosyasına yazılmaz.
- Bilgisayar ya da Windows kurulumu değişirse bayi tarafından yeni lisans
  düzenlenmesi gerekir.

## v0.7.18 – Program Files kurulumu ve TOR büyüteç ikonu

- TOR POS yönetici onayıyla normal Windows program dizinine kurulur:
  `C:\Program Files\TOR POS Pro`.
- Önceki AppData kurulum yolu güncellemede otomatik olarak tekrar kullanılmaz.
- Setup EXE artık build sonrasında Desktop'a kopyalanmaz.
- Desktop'ta yalnız `TOR POS Pro` uygulama kısayolu oluşturulur; Start Menu
  kısayolu oluşturulmaz.
- EXE, Setup, kaldırma kaydı ve Desktop kısayolu büyüteç içinde `TOR` yazılı
  yeni turkuaz/lacivert ikonla gelir.

## v0.7.23 – Edition lisansı, IMBISS Extras, kullanıcı hakları ve günlük Bon-Historie

- KIOSK ve IMBISS lisansları birbirinden ayrıdır; edition bilgisi RSA imzasının içindedir.
- Yanlış edition, farklı PC, eksik veya süresi dolmuş lisans BAR/KARTE işlemini engeller.
- İlk kurulumdaki edition seçimi güncellemelerde kilitli kalır.
- KIOSK ana panelinde PFAND bulunur; IMBISS’te aynı hücre EXTRA olur.
- IMBISS Stammdaten sırası Gruppe → Warengruppe → Artikel → Extras → Varianten şeklindedir.
- Extra Käse, Extra Ketchup ve Extra Fleisch başlangıç örnekleri eklenir; fiyatlar düzenlenebilir, MwSt. Warengruppe’den otomatik alınır.
- Admin dışında tam üç çalışan hesabı bulunur. Admin; satış, Rabatt, Storno, Parken, Einlage/Entnahme, Z-Bericht, Stammdaten, Bon-Historie ve Training izinlerini ayrı ayrı düzenler.
- Login ekranındaki TRAININGSMODUS gerçek satış, gerçek bon numarası, TSE, yazıcı ve kart terminali oluşturmaz; yalnız audit kaydı bırakır.
- LETZTER BON hücresi BON-HISTORIE oldu. Günlük/son Z’den sonraki bonlar listelenir ve seçilen bon kopya basılabilir.
- Z görünümü sıfırlasa bile tamamlanmış satışlar silinmez; denetim ve DSFinV-K için değiştirilemez olarak korunur.


## v0.7.23 – Kassenpanel / Nummernblock / Edition-Auswahl

### Obere Schnellleiste
Direkt nach **EAN SUCHEN** wurden folgende Funktionen angeordnet:
1. Z-BERICHT
2. EINLAGE / ENTNAHME
3. BON-HISTORIE
4. RABATT
5. GEPARKTE BONS
6. BON EIN/AUS
7. BON STORNO

AUTO-SCAN und Performance-Text werden nicht mehr als sichtbare Elemente belegt.
Der dauerhafte Lizenzhinweis überschreibt die Bedienstatus-Zeile nicht mehr; die
kommerzielle Lizenzprüfung und Kassier-Sperre bleiben vollständig aktiv.

### BON EIN/AUS
BON EIN und BON AUS sind jetzt **ein gemeinsamer Button**. Beim Antippen öffnet
sich ein Auswahlfenster für BON EIN oder BON AUS. Der aktuelle Zustand wird
farblich und im Buttontext angezeigt.

### Nummernblock im Arbeitsfeld
Das frei gewordene Arbeitsfeld enthält einen echten Nummernblock:
- 0 bis 9
- Dezimalkomma (,)
- Multiplikation (×)
- C
- +1 / -1
- SOFORT STORNO
- PFAND bzw. EXTRA
- PARKEN
- BAR / KARTE

`Zahl + ×` setzt die Menge der ausgewählten Verkaufsposition. Ist keine Position
ausgewählt, wird die Menge vorgemerkt und auf den nächsten Artikel/Scan angewendet.
C löscht zuerst eine aktive Zahleneingabe; erst ohne Zahleneingabe leert C den Verkauf.

### Ersteinrichtung KIOSK / IMBISS
Bei einer echten Neuinstallation ist jetzt **keine Edition vorausgewählt**.
KIOSK oder IMBISS muss ausdrücklich gewählt werden. Der Installer erzwingt die
Auswahl weiterhin über die Weiter-Prüfung. Bestehende installierte Editionen
bleiben bei Updates gesperrt und werden nicht versehentlich geändert.


## v0.7.23 – Login / Edition / Training / Standard-Zugänge

- KIOSK und IMBISS werden bei einer frischen Installation direkt im Anmeldefenster ausgewählt.
- Bei frischer Installation ist **keine Edition vorausgewählt**.
- Nach der ersten erfolgreichen Anmeldung wird die gewählte Edition in `edition.lock` gespeichert.
- Bestehende Installationen bleiben an ihre bereits gespeicherte Edition gebunden.
- TRAININGSMODUS verlangt den 4-stelligen Standard-Code `0000`.
- Admin Standard: `admin` / Passwort `admin` / PIN `1234`.
- Mitarbeiter Standard: `kassierer1`, `kassierer2`, `kassierer3` / Passwort `1234` / PIN `1234`.
- Bereits vom Kunden konfigurierte Mitarbeiter-Zugangsdaten werden durch Updates **nicht überschrieben**.
- Unkonfigurierte alte Mitarbeiter-Slots werden einmalig auf den neuen Standard `1234` migriert und aktiviert.
- Mitarbeiter-Passwörter können ab 4 Zeichen geändert werden.
- In `Mitarbeiter & Rechte` gibt es zusätzlich einen `ADMIN-ZUGANG`-Tab zum Ändern von Admin-Passwort und Admin-PIN.


## v0.7.23 – Kompakter Login / Training-Zugang

- Login-Fenster von 620x790 auf 600x680 verkleinert.
- Kopfbereich oben deutlich kompakter gemacht.
- Hauptinhalt ist bei hoher Windows-Skalierung scrollbar.
- Ein zentraler großer **ANMELDEN**-Button bleibt unten immer sichtbar.
- Passwort/PIN-Tabs enthalten nur noch das jeweilige Eingabefeld; dadurch weniger Höhe.
- Training-Code bleibt `0000`.
- ENTER im Training-Code löst ebenfalls die Anmeldung aus.
- Standardzugänge aus v0.7.21 bleiben unverändert:
  - admin / admin
  - kassierer1/2/3 / 1234
  - PIN 1234
  - Training 0000


## v0.7.23 – Login / Edition / Training Fix

- Login ekranındaki **PIN** sekmesi kaldırıldı; yalnızca Passwort ile normal giriş kaldı.
- KIOSK ve IMBISS artık her girişte boş ve seçilebilir gelir; eski `edition.lock` seçimleri önceden işaretlemez veya diğer seçeneği kilitlemez.
- Kullanıcının giriş ekranında seçtiği KIOSK/IMBISS değeri başarılı girişten sonra aktif Edition olarak yazılır.
- **TRAININGSMODUS** artık normal kullanıcı parolası istemez. `0000` Training-Code yeterlidir.
- Training oturumu ayrı `training` kullanıcısı olarak açılır ve yalnızca simülasyon satış yetkilerine sahiptir.
- Training satışları ticari lisans kontrolüne takılmaz; gerçek satış/TSE/kart işlemi oluşturmaz.
- Normal varsayılan girişler korunur: `admin / admin`, `kassierer1 / 1234`.


## v0.7.25 – Abmelden / Programm beenden / Exit-Backup

- Im Hauptfenster oben rechts wurde **ABMELDEN** ergänzt.
- ABMELDEN öffnet wieder das Anmeldefenster, ohne das gesamte Programm zu beenden.
- Ein offener Verkauf wird beim Abmelden nicht still verworfen; zuerst kassieren, PARKEN oder mit C leeren.
- Das Anmeldefenster hat jetzt zusätzlich **PROGRAMM BEENDEN**.
- Beim vollständigen Programmende wird die konfigurierte Datensicherung einmal auf Anwendungsebene erstellt.
- Die Drucker-Queue wird erst beim echten Programmende geschlossen, nicht beim Benutzerwechsel.
- Dadurch bleibt ein erneutes Anmelden nach ABMELDEN technisch sauber möglich.


## v0.7.25 – Lizenz-Deaktivierung

- **Einstellungen → Lizenzierung**: neuer Button `LIZENZ DEAKTIVIEREN`.
- Sicherheitsabfrage vor dem Deaktivieren.
- Die aktive Lizenz-ID wird lokal als deaktiviert registriert.
- Dieselbe Lizenzdatei/Lizenz-ID kann auf diesem PC danach nicht erneut aktiviert werden.
- Desktop-Deaktivierungsbeleg wird erzeugt.
- Audit-Ereignis: `COMMERCIAL_LICENSE_DEACTIVATE`.
- Für Wiederaktivierung ist eine neue Lizenz-ID erforderlich.
- Veraltete Versionsnummer in der Aktivierungsanfrage wurde auf 0.7.25 korrigiert.


## v0.7.32 – Warenverwaltung / Einstellungen / Berichte

- Neue Hauptmenüs: **Warenverwaltung**, **Einstellungen**, **Berichte**.
- Warenverwaltung: Artikelverwaltung, Duplikate, EAN-Etiketten-PDF, CSV-Import/Export, Import aus TOR-Datenbank, Inventur und Programm beenden.
- Einstellungen: direkte Sprungpunkte für Programm, Funktionen, Personal, Artikeloptionen, USt., Zahlungen, EAN, Stornogründe, Backup, Geräte, Bon/Rechnung, Pfand/Leergut und manuelle Datenbank-Sicherung.
- Berichte: **X-Bericht**, Kassensturz, Kassenjournal, Buchungsdaten, Warenbestand, unveränderbares Z-Abschluss-Journal, Umsatz-/Monats-/Artikel-/Bediener-/Personal-/Stornoberichte.
- Z-Berichte werden bei einem tatsächlich freigegebenen produktiven Z-Abschluss in `z_report_archive` unveränderbar archiviert und können später als PDF nachgedruckt werden.
- Programmierungsprotokoll erzeugt ein PDF mit Betrieb, eAS, Kasse, Lizenz, TSE, Steuer- und Geräteinformationen.
- Fiskal-Prüfung, GDPdU/GoBD-Hilfspaket, DSFinV-K-2.4-Preflight/Export-Gate und TSE-TAR-Export wurden in das Berichtsmenü integriert.
- Inventurfelder (`stock_quantity`, `last_inventory_at`) werden additiv migriert.
- KIOSK-Pfand-/Leergutwerte sind in den Einstellungen änderbar und werden von den direkten Pfand-Tasten verwendet.

**Wichtig:** Die bestehende produktive Fiskal-Sperre bleibt unverändert. DSFinV-K wird weiterhin absichtlich nicht als scheinbar vollständiger Export erzeugt, solange die offizielle 2.4-Struktur und vollständige TSE-Transaktionspersistenz nicht validiert sind.


## v0.7.32 – Windows Compile-Fix

Der von Windows gemeldete Compilerfehler `CS0176` wurde behoben.

Ursache:
`BusinessManagementService.OpenFile(string)` ist eine statische Methode,
wurde aber an sechs Stellen über die Instanz `_management` aufgerufen.

Behoben in:
- `ManagementWindows.cs`
- `MainWindow.axaml.cs`

Die Aufrufe verwenden jetzt korrekt:
`BusinessManagementService.OpenFile(path);`

Die Warnungen zu `TextBox.Watermark` sind nur Warnungen und blockieren den Build nicht.


## v0.7.32 – Konfigurierbare Waren-/Artikeltasten auf der Hauptkasse

Unter `Einstellungen → Programmeinstellungen` gibt es jetzt einen eigenen
Bereich `Kassenoberfläche · Waren- und Artikeltasten`.

Konfigurierbar:
- Artikeltasten Spaltenzahl: 2–8
- Artikeltasten Zeilenzahl: 2–12
- Warengruppentasten Spaltenzahl: 2–8
- Warengruppentasten Zeilenzahl: 1–5
- Tastenschriftgröße: 11–28
- Artikelbilder bei großen Tasten ein/aus

Standard:
- 4 × 3 = 12 Warengruppen gleichzeitig
- 4 × 10 = 40 Artikel je gewählter Warengruppe/Seite

Die Hauptkasse zeigt Warengruppen jetzt als festes Touch-Raster. Nach Auswahl
einer Warengruppe werden deren Artikel darunter ebenfalls als Raster angezeigt.
Bei mehr Warengruppen oder Artikeln als in das konfigurierte Raster passen,
erscheinen automatisch `◀ / ▶` Seitentasten.

Änderungen an Spalten/Zeilen werden nach dem Schließen der Einstellungen ohne
Neustart auf die Hauptkasse angewendet.


## v0.7.32 – Branded Startup Splash

Der Programmstart wurde kundenfreundlicher gestaltet:

- keine schwarze/dunkle Ladefläche mehr
- helles TOR-Kassensysteme Startfenster
- TOR-Logo wird aus `Assets/TorPos-Magnifier.png` geladen
- randloses, zentriertes Startfenster
- Fortschrittsanzeige während Datenbank/Kasse/Sicherheit geladen werden
- humorvoller Hinweis:
  `Einen kleinen Moment – TOR POS zählt schon mal bis drei. Gleich kann kassiert werden.`
- Splash bleibt mindestens ca. 0,9 Sekunden sichtbar
- danach erscheint automatisch das Login-Fenster
- die eigentliche Kundenanwendung bleibt `WinExe`; es wird kein Konsolenfenster benötigt

Hinweis:
`1-SETUP-ERSTELLEN.bat` ist weiterhin nur das Entwickler-/Setup-Buildwerkzeug.
Der Kunde startet später ausschließlich die installierte TOR-POS-Verknüpfung.


## v0.7.32 – Avalonia 12 Startup Compile-Fix

Windows derlemesinde görülen gerçek blokaj giderildi:

- Hata: `CS0176` in `StartupDiagnostics.cs`
- Eski kullanım:
  `SystemDecorations = SystemDecorations.None;`
- Avalonia 12 için doğru kullanım:
  `WindowDecorations = Avalonia.Controls.WindowDecorations.None;`

Ayrıca compiler logunda görünen `TextBox.Watermark` uyarıları temizlendi ve
`PlaceholderText` kullanımına geçirildi.

Önemli:
`1-SETUP-ERSTELLEN.bat` geliştirici build aracıdır ve bu nedenle siyah konsol
penceresi normaldir. Müşteri bu BAT dosyasını çalıştırmaz. Build başarıyla
bittiğinde Setup otomatik açılır; kurulumdan sonra müşteri sadece masaüstündeki
`TOR POS Pro` kısayolunu çalıştırır. Orada konsol yerine TOR logo splash görünür.


## v0.7.32 – Warengruppen-Schnellwahl wie im Kassenalltag

Die Hauptkasse wurde im Warengruppen-/Artikelbereich neu organisiert:

- Beim Öffnen der Kasse sieht der Bediener zuerst ausschließlich die großen
  `WARENGRUPPEN`-Tasten auf der gesamten Schnellwahlfläche.
- Eine Warengruppe wird angetippt; danach erscheinen auf derselben Fläche
  nur die Artikel dieser Warengruppe.
- Mit `◀ WARENGRUPPEN` geht der Bediener sofort wieder zur Gruppenübersicht.
- Warengruppen und Artikel liegen nicht mehr gleichzeitig übereinander in
  zwei kleineren Bereichen. Dadurch sind die Tasten größer und ruhiger.
- Seitenwechsel für viele Warengruppen und viele Artikel bleibt erhalten.
- Die vorhandene Kassen-, Nummernblock- und Zahlungsseite bleibt unverändert.
- Warengruppen-Zeilen können jetzt 1–10 eingestellt werden.
- Neue Installation: Standard Warengruppen 4 × 6 = 24 Tasten.
- Artikeltasten bleiben separat konfigurierbar, z. B. 4 × 10 = 40 Artikel.

Die Darstellung ist von der Bedienlogik klassischer Gastro-/Handels-Kassen
inspiriert, bleibt aber im TOR-POS-Design.


## v0.7.32 – Warengruppen als echte große Kacheln

Die vom Benutzer freigegebene Warengruppen-Optik wurde in die echte
TOR-POS-Oberfläche übertragen.

- Warengruppennamen stehen jetzt **innerhalb der großen Raster-Kacheln**.
- Die kleinen, frei wirkenden Beschriftungsbuttons sind entfernt.
- Jede Warengruppe füllt ihre komplette Grid-Zelle.
- Name ist groß, weiß, fett und mittig ausgerichtet.
- Lange Warengruppennamen dürfen auf zwei Zeilen umbrechen.
- Jede Kachel besitzt einen dezenten runden Kennbuchstaben als visuelle
  Orientierung, ohne externe Icon-Bibliothek oder zusätzliche Abhängigkeit.
- Hover/Pressed-Zustände sind deutlich sichtbar.
- Leere Rasterfelder sehen wie echte, deaktivierte Kacheln aus.
- Navigation bleibt: WARENGRUPPEN → Artikel → ◀ WARENGRUPPEN.
- Anzahl der Warengruppen-/Artikel-Kacheln bleibt über Programmeinstellungen
  konfigurierbar.


## v0.7.33 – Bedienkomfort / Frontend Simplification

- Günlük kasiyer ekranı sadeleştirildi.
- Doğrudan şeritte yalnız Rabatt, Geparkte Bons ve Bon Ein/Aus bırakıldı.
- Bon-Historie, Einlage/Entnahme, Z-Bericht ve Bon-Storno üstteki KASSE menüsüne taşındı.
- Amaç: sık işlem tek dokunuş, seyrek/yönetim işlemi en fazla üç dokunuş.
- Sonraki adım: Einstellungen bölümünü Basit ve Erweitert olarak ayırmak.


## R44 / R45 – Update, Lizenzwarnung und Cloud-2FA

- TOR POS prüft einen konfigurierten TOR-Update-Server bzw. die vorhandene Cloud-URL im Hintergrund.
- Neue Versionen werden nur bei sicherem Kassenstatus angeboten; offene Verkäufe und unklare Zahlungen blockieren den Installationsweg.
- Das Setup wird vor dem Start per SHA-256 geprüft. Remote/produktive Updates benötigen zusätzlich eine gültige Authenticode-Signatur mit freigegebenem Thumbprint.
- Vor dem Update wird automatisch eine SQLite-Sicherung angelegt; der Installer startet erst nach Ende des TOR-POS-Prozesses.
- Lizenzablauf wird ab 30 Tagen als ruhige Statusanzeige sichtbar; 7/3/1 Tage werden deutlicher, ohne Verkaufs-Popup.
- TOR POS Cloud unterstützt TOTP-2FA und einmalige Recovery-Codes.
- SumUp-Verbindungscode wurde in R44/R45 nicht verändert.

Details: `R44-CHANGELOG.md`, `R45-CHANGELOG.md`.


## R46 – Software & Update / automatischer Cloud-Bestand

Siehe `R46-CHANGELOG.md`.


## R47 – KIOSK Schnellartikel / Mindestbestand / Warenwert

- **SCHNELLARTIKEL** liegt direkt neben der EAN-Suche. Bezeichnung, freier Preis und 7/19 % MwSt. werden in einem kleinen Dialog erfasst; es wird kein Artikelstammsatz angelegt.
- Artikelstammdaten enthalten jetzt **Einkaufspreis** und **Mindestbestand**. 0 beim Mindestbestand deaktiviert die Warnung.
- Die Hauptkasse zeigt bei unterschrittenem Mindestbestand einen kompakten **BESTAND · n NIEDRIG**-Hinweis; Klick öffnet direkt Inventur/Warenbestand.
- Inventur ist scanner-first: Barcode lesen → Menge eingeben → **ENTER** speichert → Fokus zurück zum Scannerfeld.
- Warenbestand zeigt EK, VK, Mindestbestand und Warenwert. Der Warenbestandsbericht enthält Einkaufs- und Verkaufswarenwert.
- Cloud-Snapshot überträgt Einkaufspreis und Mindestbestand; das Kundenportal markiert niedrigen Bestand nicht mehr mit einem festen Grenzwert, sondern pro Artikel.
- Bestehende Datenbanken werden ausschließlich additiv um `min_stock_quantity` und `purchase_price_cents` erweitert.

Details: `R47-CHANGELOG.md`.


## R48 – Automatische Artikelnummer / IMBISS Abholnummer

- Neue Artikel erhalten automatisch eine fortlaufende numerische Artikelnummer ab 100000.
- Bestehende Artikel ohne Artikelnummer werden beim Update einmalig und additiv nummeriert; bestehende Nummern bleiben unverändert.
- Im Artikel-Editor ist die Artikelnummer bewusst schreibgeschützt, damit keine versehentlichen Doppel-/Umnummerierungen im Tagesbetrieb entstehen. CSV-/TOR-Importe mit vorhandener Artikelnummer bleiben möglich; neue Importartikel ohne Nummer erhalten automatisch eine.
- IMBISS kann unter `Einstellungen → Bedienfunktionen` die tägliche **Abholnummer** aktivieren/deaktivieren. Standard: aktiv.
- Abholnummern beginnen jeden lokalen Kalendertag bei `001`, laufen `002`, `003` … weiter und sind ausdrücklich **nicht** die fiskale Bonnummer.
- Die Abholnummer wird groß auf dem Bon gedruckt, nach dem Kassieren in der Statuszeile angezeigt, in der Bon-Historie erhalten und in TOR POS Cloud mitgeführt.
- Trainings-/Entwicklungsbons zeigen eine separate Sitzungs-Test-Abholnummer; dadurch wird keine produktive Tagessequenz verbraucht.

Details: `R48-CHANGELOG.md`.