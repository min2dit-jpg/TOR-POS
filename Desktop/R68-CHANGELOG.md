# TOR POS R68 – Fast Checkout + DI Foundation

## 1. Schnellkassieren / F5
Yeni bir KASSIEREN hızlı akışı eklendi.

Ayarlar → Zahlarten → Schnellkassieren:
- Standard-Zahlart = AUS / BAR / KARTE
- `AUS`: F5 ödeme türü seçim penceresini açar.
- `BAR`: F5 doğrudan BAR akışını başlatır.
- `KARTE`: F5 doğrudan kart terminali akışını başlatır.
- Yalnız tek ödeme türü aktifse gereksiz seçim penceresi otomatik atlanır.

Opsiyon:
`Bei F5 + BAR sofort als PASSEND kassieren`
Açıksa F5 + BAR için Gegeben/Rückgeld penceresi atlanır ve ödeme
`Tendered = Total`, `Change = 0` olarak kaydedilir.

ÖNEMLİ:
- F1 BAR her zaman eski normal Gegeben/Rückgeld akışıdır.
- F2 KARTE her zaman doğrudan kart akışıdır.
- Schnellkassieren hiçbir zaman payment method'u belirsiz bırakmaz.
- CheckoutSnapshot her zaman PaymentMethod.Cash veya PaymentMethod.Card taşır.
- Mevcut payment-in-progress / double-submit lock aynen korunur.

## 2. DI Foundation
`Microsoft.Extensions.DependencyInjection 10.0.0` eklendi.

Yeni:
- `IAppWindowFactory`
- `AppWindowFactory`
- merkezi `ServiceCollection`
- `BuildServiceProvider(ValidateOnBuild=true, ValidateScopes=true)`

Shared servisler bir defa container'a kaydediliyor.
MainWindow, SettingsWindow ve DiagnosticsWindow oluşturma işlemleri merkezi
factory üzerinden yapılıyor.

Bu R68'de bilinçli olarak "big-bang DI refactor" yapılmadı:
- SQLite/auth/cloud/backup gibi async initialization sırası App.axaml.cs içinde
  açık ve kontrollü kalıyor.
- Runtime kullanıcı (`AuthenticatedUser`) factory'ye açıkça veriliyor.
- Sonraki sürümlerde diğer Window'lar kademeli olarak factory/DI'ya taşınabilir.

## 3. Küçük temizlik
MainWindow.axaml içinde daha önce kalan duplicate
`x:Name="BackToCategoriesButton"` satırı temizlendi.

## Test
4 yeni Safety Check:
1. Quick checkout AUS + iki tender -> explicit choice
2. Configured BAR -> Cash resolve
3. Exact cash sadece F5 Schnellkassieren için
4. Tek aktif tender -> gereksiz choice yok

Windows hedefi:
ALL 254 CHECKS PASSED

## Windows kabul testi
1. 7-SICHERHEITSTESTS.bat -> ALL 254 CHECKS PASSED
2. Einstellungen → Zahlarten:
   - Standard-Zahlart = BAR
   - `Bei F5 + BAR sofort als PASSEND kassieren` = EIN
3. Ürün ekle → F5:
   - CashPaymentWindow açılmadan BAR PASSEND akışı başlamalı.
4. Aynı ürün testi → F1:
   - Normal Gegeben/Rückgeld penceresi MUTLAKA açılmalı.
5. Standard-Zahlart = KARTE → F5:
   - kart akışına gitmeli.
6. Standard-Zahlart = AUS ve BAR+KARTE aktif → F5:
   - Zahlart wählen penceresi açılmalı.
7. Çift F5 / hızlı çift dokunma ikinci checkout başlatmamalı.
