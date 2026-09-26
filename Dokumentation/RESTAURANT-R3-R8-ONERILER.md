# TOR Restaurant · R-3–R-9 inceleme ve öneriler

**Tarih:** 25.09.2026 · **Temel:** `main` @ 9043c13 · **Durum:** yalnızca öneri, kod değişikliği yok.

> **DEV5 notu (26.09.2026):** Bu belge PR #99'dan yalnız belge olarak alındı; o tarihteki
> durumu anlatır. R-6, R-9.1, R-4(B), R-5.1, satır snapshot'ları ve KDS indeksi
> `feature/restaurant-third-exe` üzerinde uygulanmıştır. Restaurant şema değişiklikleri
> DEV5'te migration 43–48'dir (42 = main'in `C4_CLOUD_OUTBOX_REJECTED`).

## Sahiplik sınırı

R-1 (birleştirme ve iptalde "önce DB, sonra TSE") ve R-2 (yetim ödeme rezervasyonu) bu belgenin
**kapsamı dışında**. Aşağıdaki dosyalar ürün sahibinde. Bu belge bu dosyalar için yalnızca
**ne yapılması gerektiğini** tarif ediyor, onları değiştirmiyor:

- `RestaurantFiscalOrderService.cs`, `RestaurantRepository.cs`
- `RestaurantWorkspaceControl.cs`, `RestaurantTablePlanWindow.cs`
- restoranla ilgili `MainWindow*` dosyaları
- `RestaurantThirdExeTests.cs`, `TorPos.UiSnapshot/Program.cs`
- restoranın fiskal ve ödeme testleri

Her önerinin altında hangi dosyalara dokunacağı ve bunların sahipli olup olmadığı yazıyor.
**Sahipsiz** işaretli işler yeni dosyalarla, bağımsız olarak yapılabilir.

---

## R-3 · Varyant, menü ve tartılı ürün restoranda satılamıyor

**Bugünkü durum (doğrulandı)**
- El terminali ürün listesi `IsWeighted`, `IsCombo` ve `Variants.Count > 0` olan ürünleri
  gizliyor; sipariş anında da reddediyor (`RestaurantHandheldService.cs` ~95 ve ~283).
- `BuildCheckoutDraftAsync`, varyantlı kalemi ödemede reddediyor ("erst nach vollständigem
  Snapshot-Support").
- `restaurant_session_items` tablosunda yalnızca `variant_name` var. `unit`,
  `vat_allocations_json` ve `menu_components_json` yok.

**Hazır olan temel:** Gastro/Imbiss tarafı aynı sorunu zaten çözmüş.
- R151 ve R153 migration'ları `parked_receipt_items`, `order_bestellung_items`,
  `sale_items` ve diğer tablolara `vat_allocations_json` ile `menu_components_json`
  ekledi.
- `CartLine` bu alanları taşıyor: `VariantName`, `Unit`, `VatAllocations`, `MenuComponents`,
  `ListUnitPriceCents`, promosyon alanları.
- Restoran bunu yeniden icat etmemeli; aynı snapshot sözleşmesini kullanmalı.

**Öneri (sırayla)**
1. **Migration (yeni sürüm):**
   - `restaurant_session_items` tablosuna şu sütunlar eklensin: `unit`, `list_unit_price_cents`,
     `vat_allocations_json`, `menu_components_json` ve isteğe bağlı `modifiers_json`
     ("ohne Zwiebel" gibi fiyatsız notlar).
   - Aynı sütunlar `restaurant_bestellung_items` tablosuna da eklensin.
   - Varsayılanlar bugünkü davranışı korusun: `'Stück'`, `''`.
2. **Tek bir dönüştürücü:** `RestaurantSessionItem ↔ CartLine` için tek bir kaynak fonksiyon
   yazılsın ve bütün alanları kopyalasın.
   - K-1/K-2 dersi: `Unit` ve allocation alanları kopyalama yardımcılarında kayboluyordu.
   - Bu fonksiyonun, alan listesini yansıtma (reflection) ile karşılaştıran bir testi olsun.
     Böylece `CartLine`'a eklenen yeni bir alan unutulamaz.
3. **Fiskal eşleşme anahtarı** *(sahipli: `RestaurantFiscalOrderService.cs`)*:
   - `IsCurrentStateSecuredAsync` bugün şu anahtarla grupluyor:
     `(product_id, product_name, unit_price_cents, vat_rate, pfand_cents)`.
   - Varyant ve menüler gelince aynı fiyattaki iki farklı varyant veya menü seçimi
     birbirine karışır.
   - Anahtar `variant_name`'i ve bileşenlerin kanonik bir hash'ini de içermeli
     (`menu_components_json` + `vat_allocations_json`).
4. **Tartılı ürün:** Restoranda nadir, ama açılacaksa `quantity_milli` zaten var.
   - `Unit = "kg"` saklanmalı.
   - Kısmi ödemede `AllocateCents` ile `CartLine.LineTotalCents` aynı yuvarlamayı
     kullanmalı; bugün uyuşmazsa ödeme exception'a düşüyor.
   - Ayrı testle açılmalı.
5. **Kapı:** Ürün listesindeki filtre ancak 1–3 ve testler bittikten sonra kaldırılsın.
   Öncesinde "gizli" kalsın; bugünkü fail-closed davranış doğru.

**Dokunacağı dosyalar:**
- Sahipli: migration ve repository (`RestaurantRepository.cs`), `RestaurantFiscalOrderService.cs`
- Sahipsiz: `RestaurantHandheldService.cs` (filtre), yeni dönüştürücü dosyası ve testi

---

## R-4 · "Kişiye göre bölme" arayüzde yok

**Bugünkü durum:** `RestaurantSplitCalculator.EqualShares` var ama hiçbir yerden çağrılmıyor
(doğrulandı). Plan (`TOR-RESTAURANT-PRODUKTPLAN.md` §2) bunu temel pakete koyuyor.

**Fiskal zorluk:** Her ödeme bir fiş ve fişin kalemleri olmalı. "Toplamın 1/3'ü" kalem değil.

**Seçenekler**

| | A · Kalem × pay | B · Sıralı kalem seçimi |
|---|---|---|
| Nasıl | Her kalem kişi sayısına oransal bölünür (`quantity_milli / n`). Her kişinin fişinde tüm kalemler kesirli miktarla yer alır. | Kasiyer "3 kişi" der. Sistem kalemleri tutarı en eşit olacak şekilde üç gruba dağıtır. Her grup normal bir kalem bölmesi olarak ödenir. |
| Fiş | "0,333 × Pizza" gibi kesirli miktarlar | Tam miktarlar, okunaklı |
| Kuruş | Bugünkü modelle **tutmuyor** (bkz. R-9) | Tam kalemlerde tutuyor |
| Mevcut kod | `AllocateCents` + kısmi `quantity_milli` var, ama kalan kuruşu taşıyamıyor | Yeni atama algoritması |

**Öneri: önce R-9, sonra A.**
- A fiskal olarak en temiz ve kullanıcıya en tanıdık model. Ama bugünkü kısmi ödeme her
  dilimi `miktar × birim fiyat` ile ayrı ayrı yuvarlıyor, bu yüzden son kişi kalan kuruşu
  **almıyor**.
- R-9 çözülmeden A açılırsa masa toplamından sapan tahsilatlar olur.
- R-9'dan sonra: bölme penceresine "N kişiye eşit böl" hızlı seçimi eklenir. Seçim, mevcut
  `RestaurantSplitSelection` listesine çevrilir. Son pay kalan miktarı ve kalan kuruşu alır.
- **Test:** 3 kişi, 1 × 9,99 € ve 2 × 4,50 €; ödemelerin toplamı masa toplamına kuruşu
  kuruşuna eşit, son ödemeden sonra masa CLOSED.
- Kısa vadeli ara çözüm: B yalnızca tam kalemleri dağıtır, R-9'dan etkilenmez. Tek bir
  kalemi (pizza) bölmez, ama hemen yapılabilir.

**Dokunacağı dosyalar:**
- Sahipsiz: `RestaurantSplitCheckoutWindow.cs`, `RestaurantDomain.cs` (hesaplayıcı ve testi)
- Ödeme akışının kendisi (sahipli) değişmez

---

## R-5 · Kellnerabrechnung ve Außer-Haus

**Bugünkü durum**
- Garson hesabı (vardiya sonu garson başına nakit, kart ve açık masalar) yok.
- Masa oturumunda `assigned_waiter` var; satışta `operator_name` var.
- Restoran ödemesi her zaman "im Haus" olarak kaydediliyor
  (`CopyLines(restaurant.Lines, true)`, MainWindow, sahipli).
- Bahşiş (Trinkgeld) modeli hiç yok.

**Öneri**
1. **Kellnerabrechnung, salt okunur rapor** (yeni dosyalar, sahipsiz):
   - `RestaurantWaiterSettlementService`: dönem (Z dönemi) içinde garson başına
     - kasalanan satışlar (nakit, kart, karma; storno ve iade düşülmüş, V-2 kuralıyla aynı)
     - açık masalar ve toplamları
     - yapılan iptaller (denetim kaydından)
   - Kaynak: `sales.operator_name` ve `restaurant_sessions.assigned_waiter`.
   - Masayı başka garson kasaladıysa hangisine yazılacağı iş kararı. Öneri: kasalayan.
   - Yazdırılabilir özet ve bir yönetici penceresi.
   - Fiskal veriye yazmaz.
2. **Bahşiş:** Almanya'da kartla bahşiş KDV dışı ve fiş tutarından ayrı gösterilmeli.
   Fiskal etkisi olduğu için ayrı bir tasarım gerektirir. İlk sürümde "yalnızca nakit bahşiş,
   sistem dışı" demek dürüst bir başlangıç.
3. **Außer-Haus:**
   - 2026'dan itibaren yemekte KDV im Haus/außer Haus aynı (`ImHausVat`), içecek her zaman %19.
     Bu yüzden vergi farkı pratikte küçük.
   - Ama DSFinV-K'daki `inHaus` alanı ve mutfak fişi (paket) için ayrım yine gerekli.
   - Öneri: masa oturumuna `service_mode` (`IM_HAUS` / `AUSSER_HAUS`) eklensin ve
     "Abholung" sanal masa alanı olarak tanımlansın.
   - Checkout'ta `CopyLines(lines, imHaus)` buna göre çağrılsın *(sahipli: MainWindow)*.

**Dokunacağı dosyalar:**
- 1 sahipsiz (yeni servis, pencere, test)
- 3'ün şema kısmı sahipli (repository), checkout kısmı sahipli (MainWindow)

---

## R-6 · Aynı kalemin iki kez seçilmesi (O-18'in kalanı)

**Bugünkü durum (doğrulandı):**
- `BuildCheckoutDraftAsync`, `selections.Single(x => x.SessionItemId == item.Id)` kullanıyor.
  Aynı kalem iki kez seçilirse .NET'in genel "Sequence contains more than one matching
  element" hatası çıkıyor. Kasiyer anlamsız bir mesaj görüyor.
- `RestaurantSplitCalculator.ByItems` ise çift seçimi kabul ediyor.

**Öneri:** `ByItems` başında çift seçimi açıkça reddetsin: "Position mehrfach ausgewählt".
Testi yazılsın.

**Dokunacağı dosyalar:** Sahipsiz: `RestaurantDomain.cs` ve testi. Repository değişmez.

---

## R-7 · Kısmen ödenmiş kaynak masa birleştirmede "CANCELLED" oluyor

**Bugünkü durum:** `MergeSessionsAsync` kaynak oturumu her zaman `CANCELLED` yapıyor. Kaynakta
daha önce kısmi ödeme (PAID kalem) varsa, raporda o masa "iptal" gibi görünüyor.

**Öneri:** PAID kalemi olan kaynak `CLOSED` olsun, yalnızca hiç ödeme görmemiş kaynak
`CANCELLED` olsun.

**Dokunacağı dosyalar:** Sahipli (`RestaurantRepository.cs`). R-1 çalışmasıyla birlikte
yapılması en doğrusu.

---

## R-8 · Planın yük hedefleri için tekrarlanabilir test yok

**Plan (§11):** Aynı anda 20 cihaz, 100 masa, KDS ve senkron çalışırken kasa hızlı kalmalı.
Bu ölçülmeden canlıya çıkış yok.

**Bugünkü durum:** `tools/TorPos.DbStress` var, ama perakende satış hacmini ölçüyor
(10.000 ürün, 500.000 satış). Restoran eşzamanlılığını ölçmüyor ve CI'da çalışmıyor.

**Öneri (sahipsiz, yeni proje `tools/TorPos.RestaurantStress`)**
- Geçici bir DB ile yerel restoran API'sini (`RestaurantLocalApiHost`) gerçek HTTP üzerinden
  sürsün:
  - 20 sanal el terminali (eşleştirme, operatör oturumu, ekle/iptal komutları, gerçekçi hız)
  - 100 masa, 2 mutfak yazıcısı (sahte lane), KDS okuma döngüsü
  - Aynı anda "kasa" döngüsü: masa açma ve ödeme (simülasyon TSE)
- Ölçümler:
  - Kasa checkout gecikmesi p50/p95/p99
  - `IoQueue` bekleme süresi
  - Komut idempotency: tekrar gönderilen komutlar çift kalem veya çift TSE üretmemeli
  - Hata ve 429 oranı
- Çıktı: `verification/RESTAURANT-STRESS-<tarih>.md`, eşiklerle.
  Öneri eşikler: checkout p95 < 1 s, p99 < 2 s, çift kayıt 0.
- CI'da her PR'da değil, elle ya da gece çalışsın.
- Plus sürümü canlıya çıkmadan önce raporu kaydedilmiş olmalı.

### R-8 · İlk ölçüm sonuçları (25.09.2026, Linux VM, 4 CPU)

Araç: `Desktop/tools/TorPos.RestaurantStress`, raporlar `Desktop/verification/RESTAURANT-STRESS/`.
Servis ve veritabanı katmanı ölçüldü; HTTP/TLS ve TSE dahil değil.
Koşul: 20 cihaz, 100 masa, 60 sn, yaklaşık 5.300 kalem.

| Ölçüm | Sonuç |
|---|---|
| Kalem ekleme | p99 4,4 ms |
| Kasa ödeme hazırlığı | p99 ~65 ms |
| Kasanın veritabanı kuyruğunda beklemesi (IoQueue) | p99 < 1 ms |
| Çift kayıt (tekrarlanan komutlarda kalem veya mutfak işi) | 0 |

**Bulgu: KDS panosu yük altında ağırlaşıyor.** p95 2,8–3,0 sn, p99 4,2 sn.
- `RestaurantKitchenOutbox.BoardAsync`, açık her kalem için `restaurant_kitchen_jobs`
  tablosunu `session_item_id` ile iki kez alt sorguda arıyor. Bu sütunda indeks yok;
  mevcut indeksler `(state, created_at)` ve `(session_id, created_at)`.
- Yalnızca test veritabanında
  `CREATE INDEX … ON restaurant_kitchen_jobs(session_item_id, action, created_at)`
  ile panonun p95'i **39 ms'ye** iniyor.
- Pano ayrıca `FERTIG` durumdaki kalemleri de listelemeye devam ediyor.

**Öneri (şema dosyası ortak ve sende):**
- Bu indeksi bir sonraki restoran migration'ına ekle.
- Panoda `FERTIG` kalemleri bir süre sonra (örn. 10 dk) gizle.
- Kasa bundan etkilenmiyor (okuma bağlantısı), ama mutfak ekranı akşam yoğunluğunda
  saniyelerce donar.

---

## R-9 · Kesirli kısmi ödemede kuruş kaçağı (yeni bulgu)

**Bugünkü durum (kodla doğrulandı, testle doğrulanmalı)**
- Kısmi ödemede her dilim ayrı yuvarlanıyor:
  - `RestaurantSplitCalculator.AllocateCents`: `round(satır toplamı × seçilen / tüm)`
  - kasa satırı: `round(miktar × birim fiyat)`, `CartLine.LineTotalCentsFor`
- Ödenmeyen kalan tekrar `kalan miktar × birim fiyat` olarak fiyatlanıyor. Kalan kuruşu
  hatırlayan bir alan yok.
- Bölme penceresindeki sayı kutusu tam sayılarla ilerliyor (`Increment = 1`), ama "0,5"
  elle yazılabiliyor ve 500 milli-adet olarak kabul ediliyor
  (`RestaurantSplitCheckoutWindow.cs` ~188 ve ~270).

**Örnekler**
- 1 × 9,99 € iki yarım: 0,5 × 999 = 499,5 → 500 ve 500 → **10,00 € tahsil**.
- Üçe: 333/333/334 milli → 333 + 333 + 334 = **10,00 €**.
- Tersine yuvarlanan fiyatlarda eksik tahsilat da olabilir.

**Etkisi:** Fiş ve TSE tutarları kendi içinde tutarlı; her fiş kendi kalemleriyle doğru.
Ama masanın siparişi (TSE-Bestellung) ile ödenen fişlerin toplamı birkaç kuruş farklı
oluyor. Misafire fazla tahsilat müşteri şikayeti, DSFinV-K'da da Bestellung ile Beleg
arasında açıklanamayan fark demek.

**Öneri**
1. **Hemen, sahipsiz:**
   - Tam miktarlı kalemlerde (`quantity_milli % 1000 == 0`) bölme penceresi yalnızca tam
     sayı kabul etsin: `FormatString="0"`, `ParsingNumberStyle` integer.
   - `RestaurantSplitCalculator.ByItems` 1000'in katı olmayan seçimi, kalem tam miktarlıysa
     reddetsin.
   - Bu, bugünkü kaçağı kapatır.
2. **Kalıcı çözüm, sahipli (repository ve payment store):**
   - Kalemde ödenen kuruş tutulsun (`paid_cents`).
   - Kalan kalemin fiyatı "orijinal satır toplamı − ödenen" olsun; son dilim tam kalanı
     alır.
   - Kasa satırı bunu kalem tutarı olarak taşımalı. Bu, `CartLine`'da persisted line total
     kullanımını gerektiriyor; K-2'de fiş yeniden yüklemede aynı prensip uygulandı:
     "saklanan satır toplamı kaynaktır".
   - R-4 A buna bağlı.
3. **Test:** 9,99 € kalem 2 ve 3 parçaya, 0,333 × n kombinasyonları; tahsilat toplamı =
   satır toplamı; son ödemeden sonra masa CLOSED.

**Dokunacağı dosyalar:**
- 1 sahipsiz (`RestaurantDomain.cs`, `RestaurantSplitCheckoutWindow.cs`)
- 2 sahipli (`RestaurantRepository.cs`, `RestaurantPaymentStore`, checkout yolu)

---

## Öneri sırası

1. **R-9.1 ve R-6:** Küçük, sahipsiz, hemen yapılabilir. Para kaçağını kapatır.
2. **R-4:** Önce B (tam kalemler, sahipsiz). R-9.2'den sonra A.
3. **R-5.1 (Kellnerabrechnung, salt okunur):** Sahipsiz.
4. **R-8:** Sahipsiz. Plus sürümü canlıya çıkmadan önce şart.
5. **R-3:** Sahipli dosyalara dokunuyor. R-1'den sonra, aynı elden yapılması doğru.
6. **R-9.2, R-7 ve R-5.3:** Sahipli. R-1/R-3 ile birlikte.
