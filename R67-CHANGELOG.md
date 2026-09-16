# TOR POS R67 – Performance & Diagnose

## Amaç
Sorunları tahmin etmek yerine kasanın hangi adımda kaç milisaniye beklediğini
ölçmek ve cihaz durumlarını tek ekranda göstermek.

## Yeni SYSTEMSTATUS / DIAGNOSE ekranı
KASSE menüsünden Admin ile açılır:
- BONDRUCKER
- KARTENTERMINAL
- TSE
- DATENSPEICHER
- DATENSICHERUNG

Cihaz testleri paralel çalışır. Dış cihaz çağrıları sınırlı bekleme süresine
sahiptir; Diagnose ekranı yüzünden kasa 10–15 saniye donmamalıdır.

## Performance ölçümleri
Checkout:
- checkout.recovery_flush
- checkout.printer_preflight
- checkout.fiscal_preflight
- checkout.journal_begin
- terminal.payment_roundtrip
- checkout.database_commit
- checkout.aftercare
- printer.receipt_spool
- printer.kitchen_spool

Startup:
- startup.settings
- startup.cart_recovery
- startup.fiscal_status
- startup.parked_count
- startup.stock_warning

Mevcut ölçümler de korunur:
- catalog.reload
- barcode.lookup
vb.

Her metric için:
- Son süre
- Ortalama
- Maksimum
- Ölçüm sayısı
- 500 ms ve üzeri yavaş ölçüm sayısı

Ayrıca son 30 ölçüm zaman çizelgesi bulunur.

## Güvenlik
- Diagnose sırasında ödeme başlatılmaz.
- Printer timeout = 2 s.
- Terminal timeout = 3 s.
- TSE timeout = 3 s.
- TSE kurulu değilse TESTBETRIEB'de hata gibi gösterilmez; AUS/NICHT EINGERICHTET olur.
- Performance ölçümü herhangi bir satış verisini değiştirmez.
- Reset sadece RAM'deki ölçüm istatistiklerini temizler; Audit/Sale/TSE verilerine dokunmaz.

## Test
4 yeni deterministik Safety Check eklendi:
1. count / average / max / last
2. slow counter
3. recent timeline
4. reset

Windows hedefi:
ALL 250 CHECKS PASSED

## Windows kabul testi
1. Programı aç.
2. KASSE -> Systemstatus / Diagnose.
3. Yazıcı kapalıyken ALLE GERÄTE PRÜFEN:
   yaklaşık 2 saniyede printer sonucu dönmeli, pencere donmamalı.
4. BAR testi yap.
5. Diagnose ekranını tekrar aç.
6. checkout.printer_preflight ve ilgili sürelerin görünmesini kontrol et.
7. 7-SICHERHEITSTESTS.bat -> ALL 250 CHECKS PASSED.
8. 1-SETUP-ERSTELLEN.bat -> gerçek Windows build.
