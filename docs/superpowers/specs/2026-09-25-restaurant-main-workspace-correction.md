# TOR Restaurant — Ana çalışma ekranı düzeltmesi

## Amaç

TOR Restaurant sürümünde masa planı ayrı bir modal/yönetim penceresi olarak açılmayacak. Restaurant kullanıcısı programı açtığında gerçek Restaurant çalışma alanında çalışacak; Tischplan, masa siparişi, ürün seçimi ve ödeme aynı ana akışın parçaları olacak.

Bu düzeltme `feature/restaurant-third-exe` branch'indeki üçüncü test installer üzerinde yapılır. Mevcut fiscal/TSE, ödeme, split, kitchen dispatch, audit, yetki ve veri modeli davranışları korunur.

## Mevcut hata

Şu an `MainWindow.OpenRestaurantTablePlanAsync()`, `RestaurantTablePlanWindow` örneğini `ShowDialog(...)` ile eski POS `MainWindow` üzerine açıyor.

Bunun sonucu:
- Tischplan, Restaurant ana ekranı yerine ayrı bir modal pencere gibi davranıyor.
- Pencere kapatıldığında eski genel POS ekranı görünür oluyor.
- Masa seçimi ve ürün ekleme akışı Restaurant servis akışından kopuk hissediliyor.
- Kullanıcının beklediği tek ekranlı Tischplan / Theke / Warengruppe / Artikel / sipariş çalışma alanı oluşmuyor.

## Hedef kullanıcı akışı

### Program açılışı

Restaurant edition açıldığında:
- recovery/fiscal güvenlik kontrolleri tamamlanır;
- yarım kalmış kart/ödeme/sepet varsa önce mevcut korumalı akış çözülür;
- normal durumda Restaurant ana çalışma alanı doğrudan görünür;
- eski kiosk/einzelhandel tarzı genel kasa ekranı Restaurant kullanıcısının arka planında görünür bir fallback olarak kalmaz.

### Üst navigasyon

Restaurant ana çalışma alanında operatörün sürekli erişebileceği iki ana satış modu vardır:

- **TISCHPLAN**
- **THEKE**

KDS, Handheld, Reservierungen ve Stammdaten mevcut entitlement/yetki kurallarına göre erişilebilir kalabilir; ancak günlük satışın ana iki modu Tischplan ve Theke'dir.

### Tischplan modu

- Aktif alanlar (örn. Innenbereich, Terrasse, Außenbereich) gösterilir.
- Aktif masalar kart olarak gösterilir.
- Masa durumu renk/etiket ile anlaşılır: frei / belegt.
- Masaya tek dokunuş yeterlidir.
  - boş masa: masa oturumu açılır ve doğrudan sipariş çalışma alanına geçilir;
  - dolu masa: mevcut masa oturumu yüklenir.
- Ayrı bir zorunlu “TISCH ÖFFNEN” adımı yoktur.
- Açık masa bilgisi çalışma alanında her zaman görünürdür.

### Sipariş çalışma alanı

Masa seçildiğinde aynı Restaurant ana ekranında:
- Warengruppen görünür;
- Warengruppe seçilince Artikel görünür;
- Artikel seçilince gerekiyorsa Bestelloptionen / ürün seçenekleri uygulanabilir;
- ürün doğrudan açık masa siparişine eklenir;
- açık sipariş/position listesi sağ tarafta sürekli görünür;
- miktar değişikliği ve mevcut güvenli storno/iptal kuralları korunur.

Masa planı ve sipariş alanı iki ayrı uygulama/pencere gibi hissedilmemelidir.

### Alt sabit işlemler

Sipariş ekranının altındaki işlem alanı ekran boyutundan bağımsız olarak erişilebilir kalır. Temel eylemler:

- **BESTELLUNG SENDEN**
- **ZWISCHENRECHNUNG**
- **UMBUCHEN**
- **TEILEN**
- **BEZAHLEN**

Gerekli durumlarda Tischdetails/Storno gibi ikincil eylemler ayrı bir açılır alan veya bağlama duyarlı kontrol olarak kalabilir.

### THEKE modu

THEKE:
- aynı Restaurant ana çalışma alanında masasız direkt satış modudur;
- Warengruppe / Artikel seçimi aynı bileşenleri kullanır;
- masa oturumu oluşturmaz;
- ödeme mevcut güvenli POS checkout/fiscal akışına gider;
- eski genel POS ekranına geçiş olarak uygulanmaz.

## Kapatma / navigasyon davranışı

Restaurant ana çalışma alanında normal operatör akışında “SCHLIESSEN” ile alttaki eski POS ekranına düşülmez.

Programdan çıkış ayrı uygulama çıkış davranışıdır. Menü/ayar pencereleri kapatıldığında kullanıcı Restaurant ana çalışma alanına döner.

## Stammdaten

Mevcut Restaurant Stammdaten kapsamı korunur:
- Artikel
- Zutaten
- Warengruppen
- Bestelloptionen
- Tische & Bereiche
- Mitarbeiter
- Drucker/Küche
- Firmendaten

Mevcut permission kontrolleri korunur.

## Korunacak mevcut davranışlar

Aşağıdaki servis ve güvenlik davranışları yeniden yazılmamalı; mevcut implementasyon yeniden kullanılmalıdır:
- RestaurantRepository masa/session state
- RestaurantFiscalOrderService
- TSE güvenlik kilitleri
- split checkout
- table takeover / move / merge
- kitchen outbox / dispatcher
- Zwischenrechnung read-only kuralı
- immediate storno permission + audit
- final checkout / receipt / payment safeguards
- edition/entitlement kontrolleri

## Mimari yaklaşım

Amaç yeni bir ikinci satış motoru yazmak değildir.

Restaurant edition için mevcut `MainWindow` içinde bir Restaurant workspace/state oluşturulacak veya mevcut cashier workspace Restaurant bağlamına uyarlanacaktır. `RestaurantTablePlanWindow` içindeki işlevsel masa/session davranışları yeniden kullanılabilir bileşen/metotlara ayrılır; ancak günlük satış akışının kökü `ShowDialog` ile açılan modal pencere olmayacaktır.

Geçici olarak iki farklı Restaurant satış ekranı aynı anda korunmayacaktır. Tek bir ana Restaurant çalışma akışı olacaktır.

## Ekran boyutları

1366×768 ana hedef boyuttur. 1024×640 desteklenir.

- Üst navigasyon görünür kalmalı.
- Açık sipariş listesi kullanılabilir olmalı.
- Alt temel işlem düğmeleri kaybolmamalı.
- İçerik gerektiğinde kendi alanında scroll olabilir.
- Sabit eylemler ana scroll alanının içine gömülmemeli.

## Test gereksinimleri

Yeni regresyon testleri en az şu davranışları doğrulamalıdır:

1. Restaurant edition startup ana Restaurant workspace'i açar; eski POS ekranını modal arka plan olarak bırakmaz.
2. Tischplan modal `ShowDialog` günlük ana satış akışının kökü değildir.
3. Boş masaya tek seçim masa oturumu açar ve sipariş çalışma durumuna geçirir.
4. Dolu masa seçiminde mevcut oturum yüklenir.
5. THEKE masasız direkt satış bağlamına geçer.
6. Tischplan ↔ THEKE geçişi açık/korumalı işlem varken güvenlik kurallarını ihlal etmez.
7. Restaurant masa siparişi ürün ekleme mevcut fiscal/kitchen davranışlarını korur.
8. Zwischenrechnung hâlâ read-only kalır.
9. BEZAHLEN mevcut checkout/fiscal güvenlik akışını kullanır.
10. 1366×768 ve 1024×640 headless UI kontrolünde temel navigasyon ve alt aksiyonlar görünürdür.
11. Restaurant edition'da eski genel POS ekranına “SCHLIESSEN” ile düşme regresyonu yoktur.
12. Einzelhandel ve Gastro edition'ları bu düzeltmeden etkilenmez.

## Teslim kriteri

Yeni test installer ancak:
- ilgili yeni testler,
- mevcut Restaurant testleri,
- Windows Release build,
- üç edition build,
- headless layout kontrolleri,
- fiscal safety/regression testleri

yeşil olduktan sonra üretilecek.

Yeni artifact adı öncekiyle karışmaması için dördüncü Restaurant test sürümünü açıkça belirtecek şekilde tanımlanmalıdır.

## Kapsam dışı

Bu düzeltmede:
- yeni TSE sağlayıcısı eklenmez;
- yeni ödeme terminali entegrasyonu eklenmez;
- rezervasyon/KDS/handheld fonksiyonları yeniden tasarlanmaz;
- inventory/cost/allergen özelliği eklenmez;
- main branch'e merge yapılmaz.
