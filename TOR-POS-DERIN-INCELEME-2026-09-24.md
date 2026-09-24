# TOR POS — Uçtan Uca Derin İnceleme Raporu (2. tur)

**Tarih:** 24.09.2026 · **İncelenen sürüm:** `main`, ReleaseInfo = R182 · 0.7.33.882 (R183+ yayımlanmamış değişiklikler dahil)
**Kapsam:** Desktop (Core, Application, Infrastructure, App), Cloud (Node sunucu + portal + deploy), testler, araçlar, CI, kurulum betikleri, dokümantasyon
**Önceki rapor:** `TOR-POS-DERIN-INCELEME-2026-09-16.md` (R112)

---

## 0. Yönetici Özeti

Kod tabanı R112'den bu yana çok büyüdü (restoran sürümü, el terminali API'si, bulut TSE temelleri, sürüm bölünmesi, DATEV, çok dilli arayüz). Önceki raporun kritik bulgularının çoğu düzeltilmiş: güncelleme yolundaki localhost muafiyeti kaldırılmış, dijital fiş sunucusu buluta taşınmış, TSE arıza kaydı ve fişteki "TSE-AUSFALL" notu yerinde, Kassensturz dönemi Z ile aynı sınırı kullanıyor, Cloud'daki global kilitleme hatası giderilmiş.

Bu turda **dört kritik** ve **on iki yüksek** önemde yeni sorun buldum. En önemlileri:

1. **Tartılı ürün + kampanya → fiş tutarsızlığı (kritik, fiskal).** `CartLine.Unit` iki kopyalama yardımcısında kayboluyor (`kg` → `Stück`). Sepette gösterilen tutar ile ödenen/imzalanan tutar 1 cent farklı olabiliyor. Kaydedilen satış yeniden yüklendiğinde satır toplamları fiş toplamını tutmuyor; bu da yeniden basımı, DSFinV-K kapanışını ve Z/DATEV KDV dağılımını bozuyor.
2. **Fabrika yöneticisi `admin/admin` + PIN `1234` yeniden "kullanılabilir varsayılan" (kritik, güvenlik, R182 gerilemesi).** Restoran sürümünde bu PIN ağdaki eşleştirilmiş her el terminalinden yönetici oturumu açmaya da yetiyor.
3. **Restoran Bestellung → TSE sırası (kritik, fiskal).** Masa kalemi önce veritabanına yazılıyor, ardından TSE ile güvenceye alınıyor. Arada bir hata olursa masa **kalıcı olarak kilitleniyor** ve bu durumu onaracak bir yol yok. DSFinV-K'da restoran iptalleri de var olmayan bir fişe (`BE-0-1`) referans veriyor.
4. **Swissbit TimeAdmin PIN'i otomatik tekrar deneniyor (kritik, donanım).** Kayıtlı PIN yanlışsa her satışta yeniden login deneniyor ve deneme sayacı hiç okunmuyor. Birkaç satış sonra TimeAdmin PIN'i bloke oluyor; açmak için PUK gerekiyor.

Yüksek önemdeki diğer başlıklar: el terminalinden yetkisiz kalem iptali, yazılım güncellemesinden sonra korumasız otomatik Z, güncelleme kurulumunda imza kontrolü ile çalıştırma arasındaki TOCTOU açığı, lisans/deneme süresinin kullanıcı tarafından yazılabilir dosyalarla aşılabilmesi, bulutta 2FA'nın yeniden kaydı, bulutta ek dosyalı e-postaların üretimde 413 alması ve restoran sürümünün güncelleme kanalının hiç olmaması.

**Genel değerlendirme:** Mühendislik disiplini hâlâ yüksek. Değişmezlik tetikleyicileri, yayın kilitleri (`FiscalRelease=false`), idempotent komut günlüğü ve ayrıntılı açıklama satırları ürünün güçlü yanları. Yeni hataların büyük kısmı **alanlar arası kopyalama** (Unit), **sıralama** (önce DB, sonra TSE) ve **yeni özelliklerin eski güvenlik kapılarını atlaması** (el terminali, otomatik Z) türünden. Üretim kilidinin kapalı tutulması doğru bir karar; aşağıdaki kritik maddeler kapanmadan açılmamalı.

---

## 1. Kapsam, Yöntem ve Sınırlar

- **Okunan kod:** Desktop'taki tüm `src` projeleri (~78.600 satır), SafetyTests'in yapısı ve örnekleri (~20.900 satır), Cloud sunucusu ve portalı (~3.000 satır JS), Caddy/systemd/PowerShell deploy dosyaları, CI iş akışı, Inno Setup betikleri. Büyük UI dosyalarında (MainWindow 6.380, SettingsWindow 4.400, ProductEditor 2.900, Dialogs 2.500 satır) ödeme, iptal, restoran, ayar koruması ve hata yönetimi yolları satır satır okundu. Geri kalan kısımlar otomatik desen taramasıyla gözden geçirildi (async void, parse, Process.Start, yetki kontrolleri).
- **Çalıştırılan testler:** `Cloud: npm test` → **52/52 geçti**.
- **Çalıştırılamayan testler:** .NET 10 SDK kurulamadı; bu konteynerde `dot.net` ve `builds.dotnet.microsoft.com` proxy tarafından engelli. Proje zaten `net10.0-windows` hedeflediği için SafetyTests yine de çalışmazdı. Bu yüzden Desktop bulguları **statik analize** dayanıyor. Aşağıda "doğrulandı" dediğim her madde kod yolu satır satır izlenerek teyit edildi. Yalnızca "olası" olarak işaretlenenler test ile doğrulanmalı (bkz. Bölüm 9).

Önem dereceleri:
- **KRİTİK:** Fiskal kaydı bozar ya da doğrudan yetki ele geçirmeye izin verir.
- **YÜKSEK:** Üretimde veri kaybı, yasal uyumsuzluk veya güvenlik açığı yaratır.
- **ORTA:** Yanlış rapor, kullanılabilirlik ya da performans sorunu.
- **DÜŞÜK:** Hijyen ve bakım.

---

## 2. Önceki Raporun (R112) Takibi

| R112 bulgusu | Durum |
|---|---|
| TSE "AKTIV" değilken sessiz imzasız satış | **Düzeltildi**: TseFailSafe ve outage kaydı, fişte TSE-AUSFALL |
| `ProbeAsync` hiç çağrılmıyor | **Düzeltildi**, hatta her işlemden önce çağrılıyor (bkz. I-j, artık performans sorunu) |
| Güncellemede localhost imza muafiyeti | **Düzeltildi**; ancak yeni bir TOCTOU sorunu var (G-3) |
| Dijital fiş sunucusu tüm arayüzlerde | **Kaldırıldı** (R145, bulut). Restoran API'si ise yeniden `ListenAnyIP` kullanıyor (R-a) |
| Cloud global login limiter | **Düzeltildi**; hedefli kilitleme kaldı (C-f) |
| Kassensturz takvim günü | **Düzeltildi** (R117/R139) |
| Varsayılan yönetici erişimi | **Geriledi**: R182'de `admin/admin` ve `1234` yeniden "usable default" (G-1) |

---

## 3. KRİTİK Bulgular

### K-1 · Tartılı ürün birimi kopyalamada kayboluyor → tutar ve fiş tutarsızlığı *(doğrulandı)*

**Yer:**
- `TorPos.Core/MenuVatPolicy.cs:380` `CloneWithAllocations(...)`
- `TorPos.Core/OrderBestellung.cs:91-107` `WithQuantity(...)`
- Her iki yardımcı da `CartLine`'ı elle kopyalıyor ama `Unit` alanını atlıyor. Varsayılan değer `"Stück"` olduğu için `IsWeighted=false` oluyor.

**Nerede tetikleniyor:** `ApplyAllocations` **her satıra** uygulanıyor:
- `CheckoutApplicationService.cs:113` (normal ödeme)
- `MainWindow.axaml.cs:~2632` (park)
- `MainWindow.axaml.cs:~3313` (simülasyon)

**Senaryo:** 19,90 €/kg ürün, %10 kampanya, 0,500 kg.
- Sepet, R174'teki satır bazlı formülle **8,95 €** gösteriyor (R174ReviewTests bunu doğruluyor).
- Anlık görüntü (snapshot) `Unit=Stück` olduğu için `q × UnitPrice` formülüne düşüyor ve **8,96 €** çıkıyor.
- Bu değer ödenen, TSE ile imzalanan ve `sale_items`'a yazılan tutar oluyor.
- Fişte ve `sale_items`'ta birim "Stück" görünüyor. Park edilen siparişler "kg" bilgisi olmadan saklanıyor.

**Neden testler yakalamıyor:** R174 testi yalnızca `SaleEngine` sepetini kontrol ediyor. Snapshot → kayıt → yeniden yükleme zinciri test edilmiyor.

**Öneri:**
- `CartLine`'a kopyalama kurucusu ya da `with` ifadesiyle kullanılabilen bir record yapısı ekleyin. Elle alan kopyalamayı kaldırın.
- Yansıma (reflection) ile "kopyalanmayan alan var mı" kontrolü yapan bir test ekleyin.
- Satır toplamını snapshot anında dondurun (`LineTotalCents` saklansın), sonradan yeniden hesaplanmasın.

### K-2 · Satış yeniden yüklenirken birim ve satır toplamı yeniden hesaplanıyor *(doğrulandı)*

**Yer:**
- `Infrastructure.cs:2395` `LoadSaleAsync`, satır 2513: `COALESCE((SELECT p.unit FROM products p ...),'Stück')`
- Park edilen fişler için aynı desen: `Infrastructure.cs:3687`

`sale_items` tablosunda birim kolonu yok, birim **ürünün bugünkü** biriminden okunuyor. Ayrıca saklanan `line_total_cents` kullanılmıyor, satır toplamı yeniden hesaplanıyor.

**Etki:**
- K-1 ile birlikte: kayıtta "Stück formülü" (8,96), yüklemede "kg formülü" (8,95). Satırların toplamı `sales.total_cents` ile tutmuyor. Bunun üç sonucu var:
  - `FiscalProcessData.KassenbelegText` "inkonsistent" hatası fırlatıyor.
  - Yeniden basım ("Entgelt" kontrolü) reddediliyor.
  - İlgili Z için DSFinV-K kapanışı üretilemiyor.
- Bağımsız bir sorun daha var: bir ürünün birimi sonradan değiştirilirse (Stück → kg), **geçmiş tüm satışların** hesaplanan satırları değişiyor. Bu, GoBD'nin değiştirilemezlik ilkesine aykırı.

**Öneri:**
- `sale_items`, `parked_receipt_items`, `aborted_*`, `order_*`, `training_*` ve `cancelled_*` tablolarına `unit` kolonu ekleyin (yalnızca ekleme yapan bir migration yeterli).
- Yüklemede saklanan `line_total_cents` değerini **kaynak** kabul edin. Yeniden hesaplamayı yalnızca doğrulama için kullanın; tutmazsa hata yerine denetim kaydı yazın.

### K-3 · Fabrika yöneticisi ve PIN 1234 yeniden kullanılabilir varsayılan *(doğrulandı, R182 gerilemesi)*

**Yer:** `AuthenticationService.cs:26-57`, `:111`, `:156`, `:174`, `:290`, `:494-611`

- İlk kurulumda `admin` / `admin` / PIN `1234` şu parametreyle oluşturuluyor: `CreateAdminAsync(..., mustChangePassword: false)`.
- Giriş sırasında fabrika parolası tanınıyor ama **yalnızca denetim kaydı yazılıyor** (`ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED`). Oturum tam yetkiyle açılıyor.
- `ChangeAdminCredentialsAsync` parola için yalnızca en az **4 karakter** istiyor.
- Varsayılan personel `1234` şifreleriyle oluşturuluyor. Her açılışta bu hash'ler 600k PBKDF2 turuyla yeniden kontrol ediliyor: kişi başı 2 türetme, ~12 türetme, birkaç saniye CPU.
- `first-run-admin.cfg` parolayı diskte geri çevrilebilir hex biçiminde tutuyor.
- **Restoran:** `RestaurantOperatorSessionService.LoginAsync` doğrudan `LoginWithPinAsync` kullanıyor. Eşleştirilmiş herhangi bir el terminali `admin` + `1234` ile yönetici yetkili operatör oturumu açabilir.

**Öneri:**
- Fabrika kimlik bilgisiyle yalnızca "parola değiştir" ekranı açılabilsin (`mustChangePassword: true`, R122/G4 davranışına dönün).
- Parola için en az 10 karakter isteyin. PIN'e ek olarak "fabrika PIN'i yasak" kuralı koyun.
- El terminali girişinde `IsAdmin` kullanıcıları ve fabrika PIN'ini reddedin.
- Varsayılan personel kontrolünü tek seferlik bir migration'a taşıyın.

### K-4 · Restoran: önce DB, sonra TSE; hata sonrası masa kalıcı kilit, DSFinV-K referansı yanlış *(doğrulandı)*

**Yer:**
- Masaüstü: `RestaurantTablePlanWindow.cs:~662-700` `AddSelectedProductAsync`, `:886` `CancelSelectedItemAsync`
- El terminali: `RestaurantHandheldService.cs:380-470`
- Kontrol: `RestaurantFiscalOrderService.cs:195` `IsCurrentStateSecuredAsync`

**Sorun 1 — sıralama:**
- `AddItemAsync` commit ediliyor, ardından `SecureAddedItemAsync` (TSE Finish + `restaurant_bestellungen` INSERT) çalışıyor.
- Bu adım hata verirse (TSE hatası, `InvalidOperationException`, çökme) kalem masada kalıyor ama güvenceye alınmıyor.
- Bundan sonra `IsCurrentStateSecuredAsync` her zaman `false` dönüyor. El terminalinde her ekleme ve iptal, masaüstünde iptal ve ödeme reddediliyor: *"Bestellung/TSE-Stand stimmt nicht mit dem Tisch überein"*.
- Durumu onaracak ya da sonradan imzalatacak (nachsignieren) bir yol yok.
- Masaüstündeki ekleme ön kontrol de yapmıyor.

**Sorun 2 — DSFinV-K:**
- `RestaurantBestellungExportLoader` tüm oturumlar için `ParkNumber=0` ve `CustomBonId="RB-{id}"` atıyor.
- `DsfinvkClosingBuilder.WriteOrderRecord` bir iptal için kabul kaydını `ParkNumber==0 && Sequence==1` ile arıyor. Bu, herhangi bir masaya uyuyor.
- Sonuç olarak `REF_BON_ID="BE-0-1"` gibi var olmayan bir fişe referans veriyor, `REF_Z_NR` ise o anki kapanış oluyor.
- `DailyClosingGuard` açık restoran oturumlarını kontrol etmiyor. Bu yüzden bir masa Z'nin öncesine ve sonrasına yayılabiliyor ve "sipariş kapanışı aşamaz" varsayımı çöküyor.
- Restoran kayıtlarında `INHAUS` sabit olarak 1.

**Sorun 3 — KDV:**
- Bestellung kayıtları ürünün temel KDV oranıyla (%7) imzalanıyor.
- Ödeme ise `_imHaus=true` ile %19'a çevriliyor (`MainWindow.axaml.cs:490`).
- Aynı masa için Bestellung ile Beleg arasında KDV farklı çıkıyor.

**Öneri:**
- Kalemi "PENDING_TSE" durumunda ekleyin; TSE başarılı ya da arıza olarak belgelendikten sonra aynı işlemde ACTIVE yapın. Açılışta ya da masa açılırken yarım kalan kayıtları otomatik olarak arıza/Bestellung kaydıyla kapatın.
- Restoran kayıtlarının kendi kimliğini (`RB-…`) REF alanlarında kullanın.
- `DailyClosingGuard`'a açık restoran oturumu kontrolü ekleyin; ya da masaları Z'ye taşıma kuralını yazılı olarak tanımlayın.
- Bestellung'ta, Beleg'de kullanılacak ImHaus oranını kullanın.

---

## 4. YÜKSEK Önemde Bulgular

### Güvenlik

**G-1 · El terminalinden ve masaüstünden yetkisiz kalem iptali.**
- `RestaurantHandheldService.cs:824-853` (`RequireOperatorAsync`) yalnızca `UserPermissions.Sale` istiyor.
- Masaüstünde `RestaurantTablePlanWindow.cs:886` hiçbir yetki kontrolü yapmıyor.
- Normal kasada aynı işlem `ImmediateStorno` yetkisi ve iptal nedeni istiyor (`MainWindow.axaml.cs:1560`).
- Garson, gönderilmiş ve TSE ile güvenceye alınmış kalemleri gerekçe girmeden iptal edebiliyor. Bu, klasik "kayıt dışı satış" senaryosu.
- **Öneri:** Her iki yolda da `ImmediateStorno` yetkisi, neden seçimi ve `ControlledPosActionService` kaydı zorunlu olsun.

**G-2 · Restoran cihaz API'sinin sertleştirilmesi.** (`RestaurantLocalApiHost.cs`)
- `ListenAnyIP` kullanılıyor: kablosuz ağ, VPN ve misafir ağı dahil tüm arayüzlerden erişilebilir. Belirli bir LAN arayüzü seçilmeli ya da Windows Güvenlik Duvarı kuralı yalnızca "Private" profil için açılmalı.
- İşlemler oturum belirteci yerine **her istekte düz PIN** ile de yapılabiliyor (`OperatorPin` alanları). Bu hem PIN'in sürekli ağda dolaşmasına yol açıyor hem de eşleştirilmiş bir cihazın başka garsonların hesaplarını bilerek kilitlemesine izin veriyor. Oturum belirteci zorunlu olmalı.
- `/api/v1/sync` ve `/terminals` KDS dahil her cihaza açık. Cihaz tipine göre yetki kapsamı yok.
- Eşleştirmede `ON CONFLICT(device_id)` başka bir cihazın kimliğini ve belirtecini ezip devre dışı bırakılmış cihazı yeniden etkinleştirebiliyor.
- 6 haneli eşleştirme kodunda hız sınırı IP başına. Kod başına deneme sınırı yok.
- `AddItem` miktarı için üst sınır yok. Adet ürünlerinde kesirli miktar kabul ediliyor. Çok büyük değerlerde `decimal*1000 → long` taşması 500 hatası veriyor.

**G-3 · Güncellemede TOCTOU.**
- `TorUpdateService.cs:233-243`: İmza doğrulaması indirme (staging) anında yapılıyor.
- Program kapandıktan sonra PowerShell `%APPDATA%` altındaki (kullanıcı yazabilir) setup dosyasını `-Verb RunAs` ile **yeniden doğrulamadan** yönetici olarak başlatıyor.
- `powershell.exe` PATH üzerinden çözümleniyor.
- Bölünmüş sürümlerde tek bir `TOR-POS-Pro-Setup` bekleniyor; temizlik de yalnızca bu adı arıyor.
- **Öneri:** Setup dosyasını ProgramData altında yönetici ACL'li bir dizine kopyalayın. Başlatmadan hemen önce Authenticode/parmak izi kontrolü yapan küçük bir imzalı yardımcı kullanın. PowerShell'i `%SystemRoot%\System32\WindowsPowerShell\v1.0\` tam yoluyla çağırın.

**G-4 · Lisans ve deneme süresi kullanıcı dosyalarıyla aşılabiliyor.**
- `CommercialLicenseService`: Devre dışı bırakma yalnızca kullanıcının yazabildiği `commercial-license-deactivations.jsonl` dosyasına kaydediliyor. Dosya silinirse lisans yeniden aktif oluyor. Bitiş kontrolü yerel saate bakıyor; saat geri alınırsa uzuyor.
- `TrialLicenseService`: ProgramData'daki kimlik dosyası silinirse deneme sıfırlanıyor. Önbellek HMAC anahtarı açık `trialId` + sabit bir değerden türetildiği için sahtelenebilir (`:274-353`). `ReadCache` 7 günlük çevrimdışı sınırı uygulamıyor, bu da sınırsız çevrimdışı demo demek.
- Deneme API'si `TrialPolicy.PublicApiBaseUrl` ile üçüncü taraf bir alan adına sabitlenmiş: `tor-pos-trial-api-xifmg0.v2.appdeploy.ai` (`TorPos.Core/TrialLicensing.cs:39`).

**G-5 · Cloud: 2FA yeniden kaydı yeniden kimlik doğrulaması istemiyor.**
- `/api/2fa/setup/start` ve `/confirm`, 2FA açıkken de çağrılabiliyor ve parola ya da mevcut TOTP istemiyor.
- Oturumu çalınan bir hesapta saldırgan 2FA sırrını kendi sırrıyla değiştirip hesap sahibini dışarıda bırakabiliyor.
- Ek olarak: TOTP tekrar kullanım koruması yok (aynı kod ~90 sn boyunca tekrar kullanılabiliyor). E-posta başına giriş limiti başarılı girişleri de sayıyor, bu da hedefli kilitlemeye izin veriyor. Kullanıcı yoksa scrypt çalışmadığı için yanıt süresinden kullanıcı adı tahmin edilebiliyor.

### Fiskal / Donanım

**F-1 · Swissbit TimeAdmin PIN'i otomatik ve sayaçsız tekrar deneniyor (KRİTİK etkili).**
- `SwissbitWormApiBridge.cs:~790-815` (`PrepareForTransaction`) ve `:~905-955` (`UpdateTime`, `UserLogin`).
- TSE saati geçersizse **her işlemde** kayıtlı PIN ile giriş deneniyor ve `retries` çıktısı yok sayılıyor. Kayıtlı PIN yanlışsa (örneğin TSE değiştirildiyse) birkaç satışta TimeAdmin PIN'i bloke oluyor.
- Saat, bilgisayar saatinden (`UtcNow`) makullük kontrolü yapılmadan yazılıyor. Bilgisayar saati yanlışsa bütün imzalar yanlış zamanla atılıyor.
- **Öneri:**
  - İlk başarısız girişte kayıtlı PIN'i askıya alın ve kasiyere uyarı gösterin.
  - `retries` ≤ 1 ise hiç denemeyin.
  - Saat güncellemeden önce NTP ile ya da son TSE `logTime` değeriyle karşılaştırarak makullük kontrolü yapın.

**F-2 · Yazılım güncellemesinden sonra korumasız otomatik Z.**
- `DsfinvkMasterDataService.EnsureSoftwareVersionAsync` (açılışta, `App.axaml.cs` içinde) açık Vorgang varsa `DailyClosingGuard`'ı hiç sormadan Z oluşturuyor. Açık park fişleri, siparişler, restoran masaları ve çözülmemiş ödeme günlüğü kontrol edilmiyor. Kassensturz da yapılmıyor.
- Sürüm geri alındığında (downgrade) da tetikleniyor.
- Hata olursa yalnızca log yazılıyor ve satışlar yeni sürümle, eski sürüm ana verisiyle kaydedilmeye devam ediyor.
- **Öneri:** Guard başarısızsa satışları "önce kapanış gerekli" durumunda engelleyin ve kullanıcıyı Z akışına yönlendirin. Downgrade'i ayrıca ele alın.

**F-3 · Fiskal üretim kapısı iki farklı kurala bağlı.**
- `FiscalComplianceService` ve `SaleRepository`/`CashMovement` `RequireProduction` çağrıları `FiscalRelease.Enabled` kullanıyor. Bu değer **üç Swissbit neslinin hepsi** ve ortak kapı açık olmadıkça `false`.
- `MainWindow` ve `TseFailSafe` ise `EnabledForProvider` kullanıyor.
- Sonuç: sağlayıcı bazlı ya da bulut bazlı onay tasarımı işlevsiz. Bir fiskaly/bulut TSE, tüm Swissbit nesilleri onaylanmadan hiçbir zaman canlıya alınamaz. Uyum metni de "Swissbit" diye sabit kodlanmış.

**F-4 · Seçilebilir ama çalışmayan bulut TSE.**
- `App.axaml.cs`'de `TseProviderKind.Cloud` seçilirse sağlayıcı olarak `CloudTseProvider` kullanılıyor. Bu, her işlemi reddeden bir iskelet (stub); dolayısıyla **her satış arıza kaydına düşüyor**.
- `FiskalySignDeTseClient` ve `FiskaltrustQueueClient` hiçbir yere bağlanmamış.
- Başlangıç kontrolü uyarı metni her durumda "Swissbit SDK" diyor.
- **Öneri:** Onaylanmamış sağlayıcıyı arayüzde seçilemez yapın ya da "Nur Test" etiketi ekleyin.

**F-5 · Satış imzalamada yakalanmayan hata türü.**
- `SaleFiscalSigningService` yalnızca `UnsupportedVatRateException` yakalıyor.
- `KassenbelegText`'in fırlattığı `InvalidOperationException` ("inkonsistent", K-2 ile birlikte gerçekçi) hiçbir TSE ya da arıza kaydı bırakmıyor ve Vorgang kapanmıyor.

**F-6 · Çökme penceresi: TSE imzaladı, DB yazmadı.**
- `TseVorgangService.FinishAsync` sonrası uygulama çökerse açılışta Finish yeniden deneniyor, TSE hata veriyor ve işlem **imzalandığı hâlde arıza olarak** kaydediliyor.
- Swissbit watchdog zaman aşımında işçi süreci öldürdüğünde de aynı durum oluşabiliyor.
- **Öneri:** TSE tarafında işlem numarası ile uzlaştırma yapın (`worm_transaction_listStartedTransactions` / dışa aktarma üzerinden).

### Veri ve Raporlama

**V-1 · Z, X, DATEV ve KDV dağılımı saklanan satır toplamını değil `quantity × unit_price` değerini kullanıyor.**
- İlgili yerler: `BusinessManagementService` (B-a), `DatevKassenbuchAsciiService.cs:~300-330`.
- Tartılı ürün + kampanya ve kısmi iade dilimlerinde KDV bazı ciroyu tutmuyor (K-1/K-2 ailesi).
- DATEV tarafında Leergut ödemesi gibi `cash_portion<=0` olan satışlar atlanıyor. Z'deki nakit bunları içeriyorsa *"DATEV-Kassenbuch-Abgleich fehlgeschlagen"* hatası alınması olası (**test ile doğrulanmalı**).

**V-2 · Yönetim raporları STORNO ve RETURN tutarlarını düşmüyor.**
- `TurnoverSummary` (HEUTE/WOCHE/MONAT), aylık rapor ve `SalesStatistics` yalnızca SALE topluyor, dolayısıyla ciro Z'den yüksek görünüyor.
- `created_at` metin karşılaştırmasıyla filtreleniyor; saat dilimi ve yaz saati geçişlerinde kayıyor.

**V-3 · DSFinV-K dışa aktarma bütün DB kuyruğunu kilitliyor.**
- `DsfinvkExportService.BuildPlanAsync` tüm kapanışları IoQueue içinde, fiş başına ayrı sorguyla (N+1) ve CSV'yi bellekte oluşturarak üretiyor.
- Dışa aktarma süresince kasada ödeme alınamıyor.
- `BuildProgrammingProtocolAsync` ise TSE donanım sorgusunu kuyruk içinde yapıyor.

**V-4 · Tek global IoQueue ve `ux_checkout_one_open`.**
- Tek bir çözülmemiş ödeme işlemi tüm satışları engelliyor.
- Üretim izni yokken manuel "ödendi" uzlaştırması APPROVED durumunda takılıyor.
- `AsyncLocal Inside` bayrağı ateşle-unut (fire-and-forget) görevlere sızıyor; bu, yeniden giriş (re-entrancy) korumasını gevşetiyor.

### Cloud

**C-1 · Ek dosyalı e-posta üretimde 413 alıyor.**
- Caddy'de `request_body max_size 2MB` tanımlı. `/api/v1/devices/mail/send` ise 12 MB'a kadar kabul ediyor, masaüstü de 8 MB'lık ek gönderebiliyor (base64 ile +%33).
- Yaklaşık 1,5 MB üzerindeki her rapor ya da DATEV e-postası uç noktada reddediliyor.

**C-2 · Restoran sürümü güncelleme ve provizyon zincirinin dışında.**
- `update-store.js`, sunucu varsayılanları ve `tools/provision.js` yalnızca `['KIOSK','IMBISS']` biliyor.
- Masaüstünde ise tek bir `TOR-POS-Pro-Setup` adı bekleniyor.

**C-3 · Kimlik doğrulamasız `/updates/*` ve `/trial/*` her istekte exe'yi okuyup SHA-256 hesaplıyor.**
- Bu işlem `readFileSync` ile senkron yapılıyor ve olay döngüsünü bloke ediyor. Birkaç eşzamanlı istek sunucuyu durdurabilir.
- **Öneri:** Hash'i dosyanın `mtime` ve boyutuna göre önbelleğe alın.

**C-4 · Tek bir hatalı olay tüm partiyi 400 ile reddediyor.**
- Masaüstü outbox'ı FIFO çalıştığı ve dead-letter mekanizması olmadığı için bu olay kuyruğun başında **sonsuza kadar** kalıyor ve bulut senkronizasyonu tamamen duruyor (`TorCloudSyncService`).

---

## 5. ORTA Önemde Bulgular

| # | Yer | Bulgu | Öneri |
|---|---|---|---|
| O-1 | `PrintJobJournal.cs` | Her baskı için bir JSON dosyası oluşuyor ve **hiç silinmiyor**. `GetUncertainAsync` bütün dosyaları IoQueue içinde okuyor, tek bir bozuk dosyada exception fırlatıyor. Ana yazıcı mutfak yazıcısının belirsiz işlerini de kendi hesabına sayıyor. JSON'lar tam fiş içeriği taşıyor ve yedeğe giriyor. | PRINTED ve REVIEWED olanları N gün sonra arşivleyin. Belirsiz işler için bir dizin kullanın. Bozuk dosyayı karantinaya alın. |
| O-2 | `SwissbitWatchdogBridge.cs` | Her TSE çağrısında (start/update/finish) tam bir uygulama süreci ve `worm_init` başlıyor. Satış başına 2–3 süreç, saniyeler süren gecikme. | İşçi süreci kalıcı tutun (named pipe) ve yalnızca kilitlenmede öldürün. |
| O-3 | `TseFailSafeService` | Her işlemden önce TSE yeniden yoklanıyor (I-j). | Son başarılı yoklamayı kısa bir süre (örn. 30 sn) önbelleğe alın. |
| O-4 | `FiskaltrustQueueClient` | Varsayılan `HttpClient` 100 sn zaman aşımı kullanıyor, bu da ödemenin donması demek. `ftState` hata bitleri okunmuyor. İşlem numarası `ftReceiptIdentification`'dan sezgisel olarak çıkarılıyor. | Zaman aşımını 5–10 sn yapın; `ftState` kontrolü ekleyin. |
| O-5 | `FiskalySignDeTseClient` | `_progress`, `_pending` ve `_transactionGates` sözlükleri hiç budanmıyor. | Finish sonrası girdileri silin. |
| O-6 | `App` | `Dispatcher.UIThread.UnhandledException` işleyicisi yok. Yaklaşık 60 `async void` UI olayında yerel try/catch bulunmuyor (örn. `OnZArchiveMenuClick`, `OnRestaurantTablesClick`); bir istisna tüm süreci çökertir. | Global UI istisna işleyicisi ve ortak bir `SafeAsync` sarmalayıcısı ekleyin. |
| O-7 | `MainWindow.axaml.cs:3136` | Restoran ödeme seçimi iptal edilince `_restaurantCheckoutDraft` görünmeden açık kalıyor. "C" tuşu bunu temizlemiyor ve masa planı *"Zuerst Kassenbon abschließen"* diye açılmayı reddediyor. | İptal ve "C" akışında taslağı temizleyin. |
| O-8 | `Infrastructure.cs` | Storno ve Retoure yalnızca satışın **takvim gününde** mümkün (`CreatedAt.Date != Now.Date`). Gece yarısını geçen serviste ya da ertesi gün iade yapılamıyor. | Kural Z dönemine bağlanmalı, sonraki günler için ayrı iade süreci olmalı. |
| O-9 | Açılış migration'ları | `'Sonstiges'` kategorisi her açılışta pasifleştiriliyor ve ürünleri taşınıyor. `UPPER('Döner')` SQLite'ta hiç eşleşmiyor (UPPER yalnızca ASCII çeviriyor). | Tek seferlik migration'a taşıyın. |
| O-10 | `DeactivateProductAsync` | Pasifleştirilen ürün başka bir menünün bileşeniyse combo satırları sessizce siliniyor; açık sipariş kilidi atlanıyor. | Engelleyin ya da uyarı gösterin. |
| O-11 | `SchemaMigrationService` 25–34 | `TOR_POS_PRODUCT_EDITION=RESTAURANT` ortam değişkeni yoksa bunlar boş işlem, ama "uygulandı" olarak kaydediliyor. Bu DB'ye bir daha asla restoran tabloları gelmez. İsimleri R183–R192, yayın ise R182. | Koşulu migration'ın içine değil, sonradan çalışan ayrı bir "restoran şema" adımına koyun. |
| O-12 | CSV içe aktarma | Geçersiz KDV sessizce %19 yapılıyor. Her satır kategorinin KDV'sini eziyor. Stok CSV'deki değerle eziliyor (kolon yoksa 0) ve envanter denetim kaydı yazılmıyor. Yalnızca UTF-8 okunuyor. | Satırı reddedin; kategori KDV'sine dokunmayın; stok değişikliğini inventory audit'e yazın; Windows-1252 desteği ekleyin. |
| O-13 | `ReportEmailService` | Her durumda STARTTLS kullanılıyor (465/SSL yok sayılıyor). Kullanıcı adı olmasa da AUTH yeteneği zorunlu tutuluyor. `last_period` kaydı başarısız olursa e-posta tekrar gönderiliyor. | |
| O-14 | `BackupEncryptionService` / `FullBackupService` | Şifreleme varsayılan olarak kapalı. Düz metin dosya önce yazılıp sonra siliniyor (diskte izi kalıyor). Tam yedek `TseExports`'u (saklama yükümlülüğü olan TAR'ları), lisans ve rapor dosyalarını dışarıda bırakıyor. Uygulama içinden geri yükleme yok. Almanca hata mesajında Türkçe metin var (*"Kurtarma kodu falsch…"*). | |
| O-15 | `AppPaths.DataDirectory` | Fiskal veritabanı **Roaming** `%APPDATA%` altında ve Windows kullanıcısına bağlı. Başka bir Windows kullanıcısı boş bir kasa ile başlıyor (fiskal verinin bölünmesi). Dolaşan profillerde DB ve WAL senkronizasyon riski var. Swissbit SDK yolu sabit olarak `TOR-POS-Pro` altında aranıyor. | Veriyi ProgramData altında, kısıtlı ACL ile tutun. |
| O-16 | `Infrastructure.cs` | `system_identity.created_at` SQLite `datetime('now')` (UTC) ile yazılıyor ama yerel saat olarak okunuyor. | |
| O-17 | Kısmi iade | Negatif ara toplamlı (Leergut) orijinaller için `CumulativeReturnProration` exception fırlatıyor. `WriteAborted` `UMS_BRUTTO=max(0)` kullanırken `Bonkopf_USt` negatif olabiliyor. | |
| O-18 | `RestaurantSplitCalculator.ByItems` | Aynı kalem birden fazla kez seçilebiliyor; her bölünmüş hesap bağımsız yuvarlanıyor. | |
| O-19 | `PaymentTerminalProfiles.Find` | Bilinmeyen profil kimliği `AUTO_ZVT` profiline düşüyor (`ProductionReady=true`). | Bilinmeyen profilde hata verin. |

---

## 6. DÜŞÜK Önem / Hijyen

- **KDV %0 için üç ayrı kural:** `FiscalReceiptFields` yalnızca 7 ve 19'u kabul ediyor, `TaxContainer` 0'ı kabul ediyor, `VatKey` 0'ı reddediyor. Şu an kategori kaydı %0'a izin vermediği için etkisiz; ileride tuzak olur.
- **Üç örtüşen TSE sağlayıcı sınıflandırması:** `TseProviderKind`, `TseProviderCatalog`, `CloudTseVendors`. `CloudTseRelease.FiskaltrustValidated` katalog üzerinden hiç okunmuyor.
- **Dört ayrı "start+finish imzalama" uygulaması** var (Sale, Order, `TseKassenbelegSigner`, `TseVorgangService`). Hata düzeltmeleri bunlar arasında kolayca sapıyor.
- `GetExpectedCashCentsAsync` ölü kod ve eski dönem kuralını içeriyor.
- `ProductRepository.GetByIdAsync` tek ürün için bütün ürünleri yüklüyor.
- `OrderBestellungDelta.Group` kültüre bağlı `"{x.Quantity:0.###}"` biçimlendirmesi kullanıyor.
- `SimplePdfWriter` Latin-1 kullandığı için "…" ve Türkçe karakterler `?` çıkıyor. Arayüz TR desteklerken PDF raporlarda Türkçe isimler bozuluyor.
- Z dönemi her iki uçta da kapsayıcı (`>= from AND <= to`); DSFinV-K ise `> from` kullanıyor. Aynı milisaniyede sınır çakışması olabilir.
- `DailyBackupScheduler` 30 sn'de bir tüm ayarları okuyor. `app_sequence` içinde her Z dönemi için bir sıra satırı birikiyor.
- Swissbit SDK yolu dosyadan imza kontrolü yapılmadan yükleniyor. Arama kökleri arasında Masaüstü ve Downloads var (listelemede kullanılıyor; yükleme yalnızca yapılandırılmış yoldan).
- `DiagnosticsWindow` ve `GooglePairingWindow`'da `Process.Start(UseShellExecute=true)`. URL HTTPS olarak doğrulanıyor; sorun yok.

---

## 7. Cloud — Ek Bulgular (Orta/Düşük)

- `receiptDetail()` sorgusu `transaction_type`, `original_receipt_number` ve nakit/kart kolonlarını çekmiyor. Portal modalında Storno ve Retoure "Verkauf" olarak görünüyor (`public/app.js:64`).
- `cloud_events` içinde heartbeat olayları (60 sn'de bir) süresiz saklanıyor. `berlin_day()` WHERE içinde kullanıldığı için tam tablo taraması yapılıyor. `cloud_sales(register_id, occurred_at)` üzerinde indeks yok. Portal sorgularında sayfalama yok.
- SALE ve MIXED satışlarda nakit+kart payının toplamla tutarlılığı kontrol edilmiyor.
- `Origin: null` ya da bozuk `Host` başlığında `new URL` exception fırlatıyor ve 500 dönüyor (400/403 olmalı).
- `serveStatic` yol kontrolü `startsWith(PUBLIC)` ile yapılıyor, `path.sep` eklenmemiş (sertleştirme).
- Mail hız limiti "kontrol et, sonra ekle" biçiminde (yarış durumu). Çökme sonrası `SENDING` durumunda kalan satırlar temizlenmiyor.
- HTTP → HTTPS yönlendirmesi sorgu dizesini (query string) atıyor. `TOR_CLOUD_PUBLIC_URL` tanımlı değilse indirme URL'si `Host` başlığından üretiliyor (host header injection).
- Masaüstü heartbeat'i `tse_status` alanını her zaman "NICHT GEPRÜFT" gönderiyor.
- `login.html` her zaman *"Demo-Build: noch kein Produktions-Accountsystem"* diyor. `index` ve `projektstatus` sayfaları R48'de kalmış. README "v0.8.0 R48" derken `CLOUD_VERSION` 0.13.0-R145.

---

## 8. Test, CI ve Dokümantasyon

- **Test boşlukları:**
  - K-1/K-2 zinciri (snapshot → kayıt → yükleme → yeniden basım) için test yok.
  - Restoran TSE hata yolu (K-4) test edilmiyor.
  - El terminali yetki kapsamı (G-1) test edilmiyor.
  - Yaklaşık 40 test dosyasında ~170 **kaynak metin** kontrolü var (`ReadAllText` ile koddaki bir string'i arama). Bunlar davranışı değil yazımı doğruluyor; yeniden adlandırmada yanlış alarm veriyor, gerçek regresyonu kaçırıyor. Kritik kurallar davranış testlerine taşınmalı.
- **CI:**
  - UI snapshot kontrolü yalnızca KIOSK ve IMBISS için çalışıyor. **RESTAURANT sürümünün pencereleri hiç çizilmiyor.**
  - Hâlâ eski ortak `TOR-POS-Pro-Setup.iss` ile "Kunden Setup" üretiliyor. Bölünmüş ürünlerle birlikte iki ayrı kurulum hattı var; güncelleme kanalı ise bölünmüş ürünleri bilmiyor (C-2).
  - Action'lar SHA ile sabitlenmemiş (`@v7`). Tedarik zinciri için commit SHA'ya sabitleme önerilir.
- **Kurulum:**
  - `{commonappdata}\…` dizinine `users-modify` izni veriliyor. Deneme kimliği gibi dosyalar tüm yerel kullanıcılar tarafından değiştirilebilir (G-4 ile bağlantılı).
  - Setup dosyası imzalanmıyor. `UpdateSignerThumbprint` boş olduğu için uzaktan güncelleme zaten kapalı.
- **Sürüm ve doküman kayması:**
  - Yayımlanmamış R183–R192 migration'ları R182 sürüm kimliğiyle çalışıyor. DSFinV-K `KASSE_SW_VERSION` kod değişikliğini yansıtmıyor.
  - `Desktop/` kökünde 150'den fazla R*-CHANGELOG ve hotfix notu var; yeni gelen biri için gürültü. `Dokumentation/Archiv` altına taşınması önerilir.

---

## 9. Doğrulama Durumu

| Bulgu | Durum |
|---|---|
| K-1, K-2, K-3, K-4, G-1, G-3, F-1, F-2, F-3, F-4, F-5, C-1, C-2, C-4 | Kod yolu uçtan uca izlenerek **doğrulandı** |
| K-1'deki 8,95 € / 8,96 € sayısal örneği | R174 formülü ve `q × UnitPrice` elle hesaplandı; **SafetyTests ile tekrar edilmeli** |
| V-1 DATEV Leergut uyumsuzluğu | **Olası**. Z'deki `CashCents` hesabının ödemeyi (payout) nasıl netlediği test ile doğrulanmalı |
| F-6 çökme penceresi | Kod sırasından çıkarıldı; donanımla yeniden üretilmedi |
| O-2 Swissbit gecikmesi | Mimari çıkarım; ölçüm yapılmadı |

---

## 10. Önerilen Öncelik Sırası

**Hemen (üretim kilidi açılmadan önce zorunlu):**
1. K-1 ve K-2: `CartLine` kopyalamasını düzeltin, `unit` kolonunu ekleyin, saklanan satır toplamını kaynak kabul edin, uçtan uca regresyon testi yazın.
2. K-3: Fabrika kimlik bilgisini zorunlu değiştirmeye bağlayın; el terminalinde yönetici ve fabrika PIN'ini engelleyin.
3. F-1: TimeAdmin PIN'ini tek başarısız denemeden sonra askıya alın; saati makullük kontrolünden geçirin.
4. K-4 ve G-1: Restoran Bestellung sıralamasını, DSFinV-K referanslarını, iptal yetkisini ve Z guard'ını düzeltin.

**Kısa vade (1–2 sürüm):**
5. F-2 (otomatik Z guard), F-3 (tek fiskal kapı), F-4 (onaysız sağlayıcıyı gizle), F-5 ve F-6.
6. G-3 (güncelleme TOCTOU), G-5 (2FA yeniden doğrulama, TOTP tekrar koruması), C-1 (Caddy limiti), C-2 (restoran kanalı), C-4 (dead-letter).
7. V-1 ve V-2 (rapor tutarlılığı), O-6 (global UI istisna işleyicisi), O-1 (baskı günlüğü temizliği).

**Orta vade:**
8. G-2 (restoran API sertleştirme), G-4 (lisans ve deneme dayanıklılığı), V-3 ve V-4 (IoQueue'yu okuma/yazma ve uzun iş kuyruğu olarak ayırma), O-2 (kalıcı Swissbit işçisi).
9. Test paketindeki kaynak metin kontrollerini davranış testlerine dönüştürme; CI'ya RESTAURANT UI kontrolü ekleme.

---

*Bu rapor statik kod incelemesine ve Cloud test paketinin çalıştırılmasına dayanır. Masaüstü testleri bu ortamda çalıştırılamadı (bkz. Bölüm 1); bulguların Windows üzerinde SafetyTests'e yeni regresyon testleri eklenerek doğrulanması önerilir.*
