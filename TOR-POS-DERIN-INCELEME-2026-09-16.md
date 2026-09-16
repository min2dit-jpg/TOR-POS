# TOR POS Pro — Uçtan Uca Derin İnceleme Raporu

**Tarih:** 16.09.2026 · **İncelenen sürüm:** 0.7.33.812 (R112)
**Kapsam:** Desktop uygulaması + Cloud sunucusu + araçlar + dokümantasyon — projenin tamamı
**Yöntem:** Kaynak kod okuma, şema çıkarımı, test paketinin çalıştırılması, iddiaların tek tek doğrulanması

---

## 0. Yönetici Özeti

TOR POS Pro, ~45.000 satırlık, olağanüstü disiplinli yazılmış bir kasa yazılımı. 36.000 satır uygulama kodunda **tek bir TODO/FIXME/HACK yok**, SQL enjeksiyonu yok, parola türetme (PBKDF2-SHA256, 600.000 tur) sektör standardının üzerinde, veritabanı 23 ayrı değişmezlik tetikleyicisiyle korunuyor ve 558 güvenlik testi bugün de eksiksiz geçiyor.

Buna karşılık üç ayrı kategoride gerçek sorunlar buldum:

1. **Fiskal bütünlük (kritik):** TSE ayarı "AKTIV" değilken satışlar **sessizce imzasız** kaydediliyor — ne arıza kaydı açılıyor, ne denetim kaydı yazılıyor, ne de fişe yasal olarak zorunlu "TSE-AUSFALL" notu basılıyor. Ayrıca arıza tespiti için yazılmış `TseFailSafeService.ProbeAsync` hiçbir yerden çağrılmıyor ve açık bir arıza kaydı kasiyere **hiç gösterilmiyor**.
2. **Güvenlik (yüksek):** Güncelleme yolunda `localhost` için **tüm imza doğrulaması atlanıyor** ve kurulum dosyası yönetici hakkıyla başlatılıyor. Dijital fiş sunucusu tüm ağ arayüzlerine şifresiz ve kimlik doğrulamasız bağlanıyor.
3. **Cloud (yüksek):** Müşteri açmak için **arayüz yok** (elle SQL gerekiyor), güncelleme zincirinde sunucu tarafı doğrulama yok, giriş hız sınırlayıcısında tüm kullanıcıları kilitleyen bir kusur var.

Ayrıca kendi bulduğum bir işletme hatası: **Kassensturz takvim gününe göre hesaplıyor**, Z-Bericht ise son kapanışa göre. Gece yarısını geçen bir imbiss için bu, sayılan kasanın hatalı görünmesi demek.

**Sonuç:** Kod kalitesi ticari bir ürün için yeterince yüksek; ancak yukarıdaki fiskal bütünlük hataları düzeltilmeden ve dış bağımlılıklar (TSE donanımı/SDK lisansı, DSFinV-K, hukuki onay) kapatılmadan üretim kullanımı mümkün değil. Mevcut `FiscalRelease=false` kilidi bu nedenle **doğru ve yerinde**.

---

## 1. Kapsam, Yöntem ve Güvenilirlik

İnceleme dört koldan yürüdü: (a) kendi okumalarım, (b) üç paralel derin tarama, (c) test paketinin fiilen çalıştırılması, (d) **her kritik iddianın kaynak koddan tek tek doğrulanması**.

Son madde önemli: bu raporda yer alan her "kritik/yüksek" bulgu, ilgili dosya ve satır açılıp gözle teyit edildi. Teyit edilemeyen iki iddia rapordan **çıkarıldı** (bkz. Bölüm 11).

Bugün fiilen çalıştırılan doğrulama:

```
dotnet run --project Desktop\tests\TorPos.SafetyTests   →   ALL 558 CHECKS PASSED (exit 0)
```

---

## 2. Projenin Genel Görünümü

| Alan | Dosya | Satır | Not |
|---|---:|---:|---|
| TorPos.Core (alan modeli/arayüzler) | 13 | 1.935 | Saf domain, bağımlılıksız |
| TorPos.Application (kullanım senaryosu) | 1 | 217 | Sadece `CheckoutApplicationService` |
| TorPos.Infrastructure (SQLite/donanım/servis) | 39 | 17.054 | En büyük katman |
| TorPos.App (Avalonia arayüz) | 42 | 17.927 | 4 XAML + ~40 pencere C# içinde |
| SafetyTests | 51 | 5.764 | 558 kontrol |
| DbStress (yük testi aracı) | 1 | 582 | 10k ürün / 500k satış |
| Cloud (Node.js sunucu) | 7 | 1.523 | Sıfır bağımlılık |

**Teknoloji:** .NET 10 + Avalonia, SQLite (WAL, `synchronous=FULL`), Node 22+ (`node:sqlite`), Inno Setup kurulumu.
**Doküman seti:** ~140 adet `.md`/`.txt` (R9'dan R112'ye kadar sürüm günlükleri, hukuki gereksinimler, donanım kabul testi, lisans sistemi, ZVT uyumluluk vb.).

---

## 3. Mimari ve Veri Modeli

### Mimari
Katmanlar temiz ayrılmış: `Core → Application → Infrastructure → App`. `CheckoutApplicationService`'in yapıcısının **yalnızca Core tiplerini** alması bir güvenlik testiyle zorlanıyor — mimari kuralın teste bağlanması güçlü bir uygulama.

Tüm veritabanı erişimi `IoQueue` üzerinden **tek sıra hâlinde** (FIFO) yürüyor; iç içe çağrılar kilitlenmiyor (testle kanıtlı). Bu, SQLite üzerinde yarış koşullarının büyük kısmını yapısal olarak ortadan kaldırıyor.

### Veri modeli
**33 tablo**, şema sürümü **11**, geriye dönük açma koruması var (uygulamanın desteklediğinden yeni bir veritabanını açmayı reddediyor).

**23 değişmezlik tetikleyicisi** — ticari kasa yazılımları için alışılmadık derecede sağlam:

- `sales`, `sale_items`, `sale_operators`, `sale_tse_signatures` → UPDATE ve DELETE tamamen yasak
- `audit_log`, `pos_action_log`, `cash_movements`, `daily_closings`, `z_report_archive`, `tse_outage_log` → aynı şekilde korumalı
- `promotion_campaigns` → yalnızca devre dışı bırakmaya izin var
- `system_identity` → seri numarası değişmez

**4 benzersizlik indeksi** iş kurallarını veritabanı düzeyinde zorluyor: aynı anda tek açık kasa işlemi (`ux_checkout_one_open`), fiş başına tek storno (`ux_sales_one_storno_per_original`), satış başına tek belirsiz kart iadesi (`ux_card_refund_one_unknown_per_sale`), global benzersiz barkod.

---

## 4. Fiskal Durum (KassenSichV / DSFinV-K / TSE)

### Gerçek durum tablosu

| Bileşen | Durum | Ayrıntı |
|---|---|---|
| Swissbit TSE köprüsü | **Gerçek, ama DLL yok** | `SwissbitWormApiBridge.cs` 1.781 satır gerçek P/Invoke; 39 dışa aktarım tanımlı. `WormAPI.dll` projede **yok** → `IsAvailable=false`. Sahte/simülasyon TSE **hiç yok** (iyi). |
| TSE işlem döngüsü | Kodlanmış, donanımsız | start/update/finish, self-test, registerClient, TAR export yazılmış; gerçek cihazla hiç doğrulanmamış |
| DSFinV-K 2.4 export | **Hiç üretmiyor** | `ExportAsync` koşulsuz `NotSupportedException` atıyor; ~20 zorunlu dosyanın hiçbiri üretilmiyor. Ön kontrol `OFFICIAL_DESCRIPTOR` maddesiyle **yapısal olarak daima** "hazır değil". |
| §6 KassenSichV fiş alanları | Kodlanmış, bugün devre dışı | `ValidateFiscalReceipt` tüm zorunlu alanları kontrol ediyor, ama yalnızca `!FiscalTestMode` iken çağrılıyor — bugün her fiş `TESTBON` olduğu için doğrulayıcı **hiç çalışmıyor** |
| Z-Bericht / gün sonu | **Gerçek ve değişmez** | Atomik Z numarası, tek işlemde `z_report_archive` + `daily_closings`, tetikleyicilerle korumalı. Ancak sütunlar **TOR'a özgü**, DSFinV-K `cashpointclosing` biçiminde değil. |
| TSE arıza (Ausfall) kaydı | Kısmen bağlı | Kayıt/kapanış mantığı doğru yazılmış, fakat aşağıdaki iki hatayla fiilen devre dışı kalabiliyor |

### Üretime geçiş için açılması gereken kilitler

| Kilit | Yer | Beklediği şey |
|---|---|---|
| `FiscalRelease.Enabled => false` | `Core/CheckoutSafety.cs:6` | TSE + DSFinV-K + fiş onayı |
| 5 adet `const false` (fiscalReleaseBuild, dsfinvk…, ksichv…, parkedOrderTse…, pfandTax…) | `FiscalComplianceServices.cs:300-304` | her biri ayrı gerçek doğrulama |
| `OFFICIAL_DESCRIPTOR` daima engelleyici | `DsfinvkExportService.cs:100` | resmî 2.4 tanımlayıcısı |
| `ExportAsync` istisnası | `DsfinvkExportService.cs:113` | tam export kodlaması |
| `UpdateSignerThumbprint = ""` | `Core/ReleaseInfo.cs:12` | kod imzalama sertifikası |
| `FiscalProcessData` taslak uyarısı | `Core/FiscalProcessData.cs:11-18` | BSI TR-03153 bayt düzeyi doğrulama |

---

## 5. Ödeme Zinciri

**ZVT kart terminali:** Her bağlantı ve komut çağrısı ayrı `CancellationTokenSource` ile zaman aşımına bağlı — arayüzün donması yapısal olarak engellenmiş. Kart verisi (PAN/track) **hiçbir yerde ayrıştırılmıyor ve loglanmıyor**.

**Belirsiz ödeme koruması:** `checkout_operations` + `ux_checkout_one_open` ile aynı anda tek açık işlem; belirsiz sonuç "Zahlung ungeklärt. Nicht erneut kassieren." ile kilitleniyor ve elle mutabakat gerektiriyor. Bu, kasa yazılımlarındaki en tehlikeli hata sınıfına (çift çekim) karşı doğru tasarım.

**Mixed Payment (R101), Kart Storno/Retoure (R102), çift iade kilidi (R106/R107):** Bu oturumda eklendi ve test edildi. R107 ile storno ve teilretoure artık birbirinin varlığını kontrol ediyor (100 €'luk fişte 30 € iade sonrası tam storno artık engelleniyor).

**SumUp:** Yalnızca eşleştirme, durum sorgulama ve sabit 1 €'luk cihaz testi var; **normal satış akışına bağlı değil** (kodun kendi yorumu da bunu söylüyor). Satış akışı tamamen ZVT üzerinden.

---

## 6. Güvenlik

### Sağlam bulunan temel (endişelenmeyin)

- **SQL enjeksiyonu yok.** Tüm kod tabanında yalnızca 3 interpolasyonlu sorgu var, üçü de derleme zamanı sabitleriyle kuruluyor. Geri kalan her şey parametreli.
- **Parola/PIN:** PBKDF2-HMAC-SHA256, **600.000 tur**, 32 bayt rastgele tuz, sabit zamanlı karşılaştırma, bilinmeyen algoritmada fail-closed.
- **Lisans:** Gerçek RSA-SHA256/PKCS1 imza doğrulaması; özel anahtar hiçbir zaman istemciye girmiyor.
- **Sırlar:** Cloud jetonu ve SMTP parolası DPAPI ile korunuyor; SumUp API anahtarı hiç saklanmıyor; hiçbir sır loglara düşmüyor.
- **TLS:** Hiçbir yerde sertifika doğrulaması devre dışı bırakılmamış; SMTP'de STARTTLS zorunlu.
- **Yedek geri yükleme:** Dizin aşımına karşı korumalı, dosya başına SHA-256, zip-bomb sınırları, `PRAGMA integrity_check`.
- **Dijital fiş jetonu:** 144 bit rastgele; yol aşımı imkânsız (jeton yalnızca SQL parametresi); HTML kaçışı tüm veritabanı kaynaklı alanlarda uygulanmış (XSS yok).

### Bulgular

**[YÜKSEK] G1 — Güncellemede localhost istisnası imza zorunluluğunu tamamen kaldırıyor**
`TorUpdateService.cs:204` ve `:309` — her iki imza kapısı da `if (!IsLoopback(root))` içine alınmış. Sunucu adresi `127.0.0.1`/`localhost`/`::1` ise sabitlenmiş sertifika kontrolü ve Authenticode doğrulaması **hiç çalışmıyor**; geriye yalnızca aynı sunucunun kendi manifestosuna karşı SHA-256 kontrolü kalıyor (kendi kendini doğrulama, güvenlik değeri yok). Ardından kurulum dosyası `-Verb RunAs` ile **yönetici haklarıyla** başlatılıyor (`:243`). Sunucu adresi `%APPDATA%` altındaki SQLite ayarlarından okunuyor.
*Hafifletici:* `-Verb RunAs` bir UAC onayı çıkarır ve uzak (non-loopback) yol **doğru biçimde fail-closed**. Yine de bu, geliştirme kolaylığı için bırakılmış ve üretimde kaldırılması gereken bir kapı.

**[YÜKSEK] G2 — Dijital fiş sunucusu tüm arayüzlere açık, şifresiz, kimliksiz**
`DigitalReceiptService.cs:44` → `new TcpListener(IPAddress.Any, 8099)`, düz HTTP. Misafir Wi-Fi'si veya WAN'a bakan bir arayüzü olan kasada içerik ağdan okunabilir. Ek olarak bağlantı işleme sınırsız ve **soket zaman aşımı yok** → basit bir slowloris saldırısıyla kaynak tüketimi mümkün. Jetonlar hiç süresi dolmuyor.

**[ORTA] G3 — PIN deneme kilidi sayacı sıfırlıyor**
`AuthenticationService.cs:855`: kilit anında `failed_attempts` **0'a çekiliyor**. 5 dakikalık kilit bitince saldırgan yeniden 5 hak kazanıyor; artan gecikme (backoff) veya kalıcı kilit yok. 4 haneli PIN için sürdürülebilir deneme ≈ günler mertebesinde.

**[ORTA] G4 — Varsayılan yönetici kimlikleri**
Bootstrap dosyası yoksa `admin`/`admin`, PIN `1234` oluşturuluyor (`AuthenticationService.cs:39-41`). `mustChangePassword` doğru şekilde zorlanıyor, ancak kimlik doğrulama katmanında **admin için** bu bayrak kontrol edilmiyor (yalnızca arayüzde) — savunma derinliği açığı.

**[ORTA] G5 — Yedek şifreleme anahtar türetme**
Kurtarma kodundan anahtar türetirken **tuzsuz düz SHA-256** kullanılıyor (`BackupEncryptionService.cs:260`); yalnızca kodun 128 bit rastgele olması koruyor. DPAPI sarmalama `LocalMachine` kapsamında ve entropi sabiti binary içinde gömülü → makinedeki herhangi bir yerel süreç açabilir (kasıt belgelenmiş: koruma makine dışına çıkan kopyalar için).

**[DÜŞÜK]** Bozuk `.tpe` dosyasında denetimsiz uzunluk tahsisi (çökme/OOM); eğitim modu sabit `0000` kodu.

---

## 7. Cloud Sunucusu (Node.js)

Sıfır bağımlılıklı, tek dosyalık (980 satır) `node:http` sunucusu; `node:sqlite` üzerinde WAL. 25 uç nokta. Güçlü yanları gerçekten var: olay doğrulamada **aritmetik tutarlılık kontrolleri** (ara toplam − indirim = toplam, satır çarpımları), kiracı (tenant) izolasyonu, TOTP sırrı ve Google jetonu **AES-256-GCM ile şifreli**, manifest yayınında kilitli ve atomik dosya değişimi, tüm SQL parametreli, HTML kaçışı yapılmış.

### Bulgular

**[YÜKSEK] C1 — Güncelleme zincirinde sunucu tarafı doğrulama yok**
Authenticode kontrolü yalnızca `PUBLISH-UPDATE.ps1` içinde; sunucu servis ederken dosyayı **yeniden hash'lemiyor**. Güncelleme dizinine yazabilen biri bu PowerShell kapısını tamamen atlar. İstemci tarafındaki tek gerçek güven çıpası (`UpdateSignerThumbprint`) ise boş. Yani zincir **iki ucundan da kopuk** — pratikte bugün uzaktan güncelleme zaten kapalı, ama açılmadan önce her iki uç da kapatılmalı.

**[YÜKSEK] C2 — Giriş hız sınırlayıcısında toplu kilitlenme**
`server.js:696`: `if(loginAttempts.size>10000) return true;` — harita 10.000 kaydı aşınca fonksiyon **herkes için** true döner, yani tüm kullanıcılar girişe kapanır. Farklı e-postalarla harita doldurulabilir. Ayrıca anahtar `req.socket.remoteAddress` (`:697`) — zorunlu ters vekil sunucu arkasında tüm kullanıcılar **tek IP kovasını** paylaşır.

**[YÜKSEK] C3 — Müşteri açma arayüzü yok**
İşletme, şube, kasa, kullanıcı ve cihaz jetonu yalnızca doğrudan SQL ile oluşturulabiliyor. Ödeme yapan bir müşteriyi devreye almak elle SQLite komutları gerektiriyor. Ayrıca `role` sütunu dekoratif: `requireUser` `OWNER` dışındaki her rolü reddediyor.

**[ORTA] C4 — İşletim olgunluğu prototip düzeyinde**
TLS yok (ters vekil zorunlu ama tanımsız), günlükleme tek satır (yığın izi/yol/istek kimliği yok), süreç yöneticisi/servis tanımı yok, SQLite dosyası için yedekleme yok, `SIGTERM` ile düzgün kapanma yok. `fs.createReadStream(...).pipe(res)` için `'error'` dinleyicisi yok → istemci bağlantıyı koparırsa **süreç çökebilir**. Süresi dolan oturumlar yalnızca erişildiğinde siliniyor → tablo sınırsız büyür.

**[ORTA] C5 — Demo kimlikleri paket içinde**
`demo@torpos.local` / `TorDemo2026!` ve demo cihaz jetonu kodda sabit; demo modunda giriş sayfasında **ekranda gösteriliyor**. `TOR_CLOUD_DEMO` ile kapalı, ama sevk edilen üründe gerçek varsayılan olarak duruyor.

**Test durumu:** 17 + 3 test; Google jeton akışı, 2FA kapatma, kurtarma kodu ile giriş, `cash.movement`/`z.closed` alımı, 413 reddi, oturum süresi ve yol aşımı **kapsanmıyor**. `tests/portal-dom.cjs` elle `jsdom` kurulumu istediği ve glob dışında kaldığı için fiilen ölü. README "15/15" diyor — güncel değil.

---

## 8. Bu İncelemede Bulunan Fiskal Bütünlük Hataları

Bunlar raporun en önemli kısmı: **kodlanmış ama yanlış çalışan** şeyler, eksik olanlar değil.

**[KRİTİK] F1 — Sessiz imzasız satış**
`SaleFiscalSigningService.cs:51-56` (ve `OrderFiscalSigningService` aynısı): `tse.status != "AKTIV"` ya da `tse.client_id` boşsa fonksiyon **hiçbir şey yapmadan geri dönüyor**. Sonuç: arıza kaydı açılmıyor, denetim kaydı yazılmıyor, `sale_tse_signatures` satırı oluşmuyor ve `sale.TseOutage` **false kalıyor**. `TseOutage=false` olduğu için de fişe yasal olarak zorunlu "TSE-AUSFALL / Vorgang ohne TSE-Signatur" notu **basılmıyor**. Yani ayar yanlışlıkla "AKTIV" dışına düşerse kasa, hiçbir iz bırakmadan imzasız satış yapar.

**[KRİTİK] F2 — Arıza tespiti fiilen devre dışı**
`TseFailSafeService.ProbeAsync` yazılmış, fakat **hiçbir yerden çağrılmıyor**: tüm probe noktaları (`MainWindow:3796`, `FirstRunSetupWindow:150`, `DiagnosticsWindow:518`, `SettingsWindow:1928`, `BusinessManagementService:932`) ham `ITseProvider`'ı çağırıyor. Dolayısıyla açılışta veya tanıda erişilemeyen bir TSE **arıza kaydı açmıyor**. Buna ek olarak `ITseOutageRepository.GetOpenAsync`'in üretimde **hiç çağıranı yok** — açık bir arıza kasiyere hiçbir zaman gösterilmiyor ve hiçbir işlemi engellemiyor.

**[YÜKSEK] F3 — Retoure yanlış vorgang tipiyle imzalanıyor**
`FiscalProcessData.cs:63`: `"RETURN" => "AVBelegabbruch"`. `AVBelegabbruch` **iptal edilmiş (yarıda kesilmiş) fiş** demektir; parasal bir iade değil. DSFinV-K'da iade, negatif pozisyonlu normal bir belge olarak modellenir. Bu haliyle imzalanan her Teilretoure yanlış sınıflandırılır. (Dosya zaten "taslak" etiketli, ama gerçek TSE'ye geçmeden düzeltilmeli.)

**[YÜKSEK] F4 — Vorgangsende sistem saatinden uyduruluyor**
`MainWindow.axaml.cs:2678`: `sale.TseLogTime ?? DateTimeOffset.Now`. TSE imzası yoksa fişteki "Vorgangsende" alanına **PC saati** yazılıyor ve TSE kaynaklı bir zaman damgası gibi sunuluyor. `ValidateFiscalReceipt`'in bu alan için yaptığı kontrol de böylece hiçbir zaman başarısız olamıyor.

**[ORTA] F5 — Dijital fişte doğrulayıcı yok**
`DigitalReceipt.cs:219-234` "Elektronischer Beleg gem. §6 KassenSichV" ibaresini basıyor, ancak basılı fişteki `ValidateFiscalReceipt` benzeri bir zorunlu-alan kontrolü yok; alanlar boşsa sessizce atlanıyor.

**[ORTA] F6 — Sonradan imzalama (Nachsignieren) yapısal olarak imkânsız**
`sale_tse_signatures.sale_id` birincil anahtar ve tablo UPDATE'e kapalı (`Infrastructure.cs:2812`). Arıza sırasında `outage=1` satırı yazıldıktan sonra o satış **hiçbir zaman** yeniden imzalanamaz; ikinci deneme UNIQUE hatası verir. Bilinçli bir değişmezlik tercihi olabilir, ama migration 8'in yorumu bunun tersini söylüyor (belge/kod çelişkisi).

---

## 9. İşletme ve Süreç Bulguları

**[ORTA] İ1 — Kassensturz ile Z-Bericht farklı gün sınırı kullanıyor** *(bu incelemede bulundu)*
`GetExpectedCashCentsAsync` beklenen nakdi **takvim gününe** göre hesaplıyor (`substr(created_at,1,10) = bugün`), Z-Bericht ise **son kapanıştan** bu yana. Gece 01:00'de Kassensturz yapan bir imbiss'te beklenen tutar yalnızca gece yarısından sonraki satışları içerir, oysa çekmecedeki para tüm akşamın parasıdır → çok büyük sahte "fazla" görünür. Aynı aile: **abholnummer (pickup) sayacı** ve **kampanya geçerlilik tarihi** de gece yarısında sıfırlanıyor/bitiyor — servis ortasında sıra numarası 1'e döner.

**[ORTA] İ2 — Mutfak yazdırma kuyruğunda baş tıkanması**
`OrderPrintOutbox`: kuyruk `ORDER BY rowid` işleniyor ve ilk hatada `break` ediliyor. Deneme sayacı, ölü-mektup kutusu veya arayüzde kuyruk görünümü yok. Artık var olmayan bir yazıcı adına takılan tek bir iş, sonraki **tüm** mutfak fişlerini süresiz bloklar; kasiyer bunu yalnızca yinelenen hata bildiriminden anlar.

**[ORTA] İ3 — Lisans mantığı tersine dönmüş**
`MainWindow.axaml.cs:4302`: `IsUnlicensedDevelopmentTestMode()` — yani **lisans yokken true** — kontrolü, lisans+FiscalRelease kontrolünden **önce** `return true` yapıyor. Sonuç: lisanssız kurulum simülasyon satışı yapabiliyor, **lisanslı kurulum ise "KASSIEREN GESPERRT" alıyor**. Gerçek bir satış hiçbir durumda işlenmiyor (ikisi de simülasyon/engel), dolayısıyla mali risk yok; ama para ödeyen müşteri bugün kasayı hiç deneyemez. Kodun kendi yorumu: "Remove/disable for production builds."

**[ORTA] İ4 — Sürüm kontrolü ve CI yok**
Projede `.git` **yok**, CI tanımı **yok**. 45.000 satırlık ticari bir ürün, sürüm geçmişi ve otomatik test kapısı olmadan ZIP kopyalarıyla yönetiliyor. 558 testin her değişiklikte otomatik koşmaması, bu oturumda bulunan hata sınıflarının tekrar sızmasına açık kapı bırakıyor.

**[DÜŞÜK] İ5 — Belge/sürüm kayması**
`Desktop\manifest.json` sürümü `0.7.33.630` (güncel: `0.7.33.812`) ve hâlâ "excluded_now: TSE, DSFinV-K, ZVT" diyor — üçünün de ciddi kodu var. `COMMERCIAL-RELEASE-CHECKLIST-DE.md` başlığı `0.7.19`. Cloud `README` "15/15 test" diyor. `verification/` klasöründeki kanıt kayıtları R41–R55 döneminde donmuş.

**[DÜŞÜK] İ6 — Ölü kod**
`TseFailSafeService.ProbeAsync` (çağrısız), `GetDailySinceLastZAsync` (çağrısız), Cloud'da `qrSvg` (kullanılmıyor), `tests/portal-dom.cjs` (glob dışı), `torpos-integration/` (tek satırlık "artık geçersiz" notu).

---

## 10. Doğrulanmış Sağlam Alanlar

Bu bölüm bilinçli: neyin **endişe gerektirmediğini** bilmek de en az hata listesi kadar değerli. Aşağıdakileri bizzat kod okuyarak doğruladım.

- **Stok geri alma doğru.** Hem tam storno hem kısmi retoure aynı `ReverseStockAsync` yardımcısını kullanıyor ve **menü/kombo bileşenlerini** de doğru çözüyor. (Tam storno'nun stok iade etmediğinden şüphelendim — yanlış çıktı, kod doğru.)
- **Geparkte sipariş çifte tahsil edilemiyor.** Satış işlemiyle aynı transaction içinde `... WHERE id=$id AND status='OPEN'` + etkilenen satır kontrolü; ikinci deneme istisna atıyor.
- **Z-Bericht atomik ve tutarlı.** Z numarası transaction içinde artırılıyor, arşiv + gün kapanışı tek transaction'da yazılıyor, `IoQueue` serileştirmesi nedeniyle araya satış giremiyor.
- **Kampanya seçimi belirlenimci.** Etkin + tarih aralığı + kapsam eşleşmesi, ardından yüzde DESC → kapsam özgüllüğü DESC → id DESC, `LIMIT 1`; sonuç satışa değişmez bir anlık görüntü olarak yazılıyor.
- **Mutfak baskısı yinelenmiyor.** Sipariş transaction'ıyla birlikte kuyruğa yazılıyor; gönderim, `PrintJobJournal` üzerinden sahiplik devriyle yapılıyor, aynı iş iki kez basılmıyor.
- **36.000 satırda tek bir TODO/FIXME/HACK yok.**

---

## 11. Rapordan Çıkarılan İddialar (yanlış çıktı)

Derin incelemenin dürüstlük testi budur:

1. **"Sevk edilen metin dosyalarında bozuk kodlama (mojibake) var" — YANLIŞ.** İlk tarama 160 dosyada bozulma gösterdi. Bayt düzeyinde kontrol ettiğimde çift kodlanmış tek bir dizi bile çıkmadı: dosyalar temiz UTF-8. Bozulma benim okuma aracımın (PowerShell 5.1'in ANSI varsayılanı) yarattığı bir yanılsamaydı. Rapora girmedi.
2. **"Tam storno stok iade etmiyor" — YANLIŞ.** Kodun kendisi doğru (yukarıda).

---

## 12. Üretime Hazırlık Değerlendirmesi

Daha önce sizin kendi değerlendirmenizde belirttiğiniz üç aşama hâlâ geçerli; bu inceleme onu **doğruluyor ve bir madde ekliyor**:

**Aşama 1 — Hata giderme (yazılımla kapatılabilir, bugün yapılabilir)**
F1, F2 fiskal bütünlük hataları; G1 güncelleme kapısı; G2 fiş sunucusu bağlanma adresi; İ1 gün sınırı; İ2 kuyruk tıkanması; İ3 lisans mantığı. Bunların hepsi dış bağımlılık gerektirmiyor.

**Aşama 2 — Eksik işlevler (dış bağımlılığa bağlı)**
DSFinV-K tam export + resmî tanımlayıcı; Swissbit SDK lisansı ve gerçek `WormAPI.dll`; F3/F4 fiskal veri biçimi düzeltmeleri; kod imzalama sertifikası; Cloud müşteri açma arayüzü ve işletim olgunluğu; SumUp'ın satışa bağlanması (istenirse).

**Aşama 3 — Gerçek kullanım doğrulaması (yalnızca sahada)**
Gerçek TSE, yazıcı, kart terminali ile uçtan uca; elektrik kesintisi, bağlantı kopması, yedekten dönüş, gün sonu mutabakatı; KIOSK ve IMBISS ayrı ayrı. Projenin kendi `HARDWARE-ABNAHMETEST-DE.md` ve `COMMERCIAL-RELEASE-CHECKLIST-DE.md` belgeleri bu aşamayı zaten doğru tarif ediyor — **her iki listede de tek bir kutu işaretli değil.**

**Eklediğim dördüncü nokta:** Sürüm kontrolü (git) ve testleri her değişiklikte koşan bir CI, ticari sevkiyattan önce kurulmalı. Bu oturumda bulunan hataların birçoğu "bir yerde düzeltilmiş, başka yerde aynı formül yanlış kalmış" türündendi; bunları yakalayan tek şey testlerin disiplinli koşturulması oldu.

---

## 13. Önerilen Öncelik Sırası

1. **F1 + F2** — imzasız satışın sessizliğini kaldır: `tse.status != AKTIV` durumunda arıza aç, denetim kaydı yaz, `TseOutage=true` ver (fişteki yasal not otomatik gelir), açık arızayı kasiyere göster. Testle kanıtla.
2. **G1** — loopback imza istisnasını `#if DEBUG` ile sınırla veya tamamen kaldır.
3. **G2** — fiş sunucusunu yalnızca seçili yerel ağ arayüzüne bağla, soket zaman aşımı ve jeton ömrü ekle.
4. **İ3** — lisans mantığındaki tersliği düzelt (lisanslı kurulum en azından simülasyon yapabilmeli).
5. **İ1** — "işletme günü" kavramını tek yerde tanımla; Kassensturz, abholnummer ve kampanya sınırlarını ona bağla.
6. **G3** — PIN kilidinde artan gecikme.
7. **İ2** — mutfak kuyruğuna deneme sayacı + tanı ekranında kuyruk görünümü.
8. **C2, C4, C5** — Cloud hız sınırlayıcı düzeltmesi, günlükleme/TLS/süreç yönetimi, demo kimliklerinin üretim paketinden çıkarılması.
9. **F3 + F4** — gerçek TSE'ye geçmeden önce vorgang tipi ve Vorgangsende alanlarını düzelt.
10. **İ4** — git + CI (558 testi her değişiklikte koştur).
11. **İ5** — sürüm/belge kaymalarını temizle.

---

*Bu rapor 16.09.2026 tarihinde, sürüm 0.7.33.812 (R112) üzerinde hazırlanmıştır. Tüm "kritik" ve "yüksek" bulgular kaynak koddan satır düzeyinde doğrulanmıştır. Hukuki veya vergisel danışmanlık değildir.*
