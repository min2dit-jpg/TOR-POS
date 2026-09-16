# TOR POS Performance Contract

## Hedefler
- Barcode cache lookup: < 10 ms hedef
- Barcode -> ürün/sepete toplam: < 100 ms hedef
- Ürün tuşu -> sepete: anlık hissedilmeli
- BAR/KARTE local commit: < 150 ms hedef
- 10.000 aktif ürün: satış akıcı kalmalı
- 500.000 eski satış: barcode/sepet performansını bozmamalı

## Değiştirilemez kurallar
1. Satış hot-path içinde internet çağrısı yok.
2. Barcode araması DB sorgusu ile yapılmaz; RAM dictionary kullanılır.
3. Sepet ödeme anına kadar RAM'de tutulur.
4. SQLite WAL kullanılır.
5. Satış transaction kısa tutulur.
6. Printer/TSE/ZVT ileride UI thread'i bloke edemez.
7. Backup/rapor/update background worker'da çalışır.
8. Ürün resmi aynı oturumda tekrar tekrar decode edilmez.
9. Network kesilmesi satışa engel olamaz.
10. TSE/ZVT için timeout ve cancellation zorunlu olacak.

PerformanceCounters:
- catalog.reload
- barcode.lookup
- sale.commit.ui
