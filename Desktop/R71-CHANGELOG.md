# TOR POS R71 – ANGEBOT / PROMOTION CAMPAIGNS

## Amaç
Döner/Imbiss/Kiosk işletmelerinin tarih aralıklı kampanyaları ürün fiyatını kalıcı
olarak değiştirmeden hızlı ve denetlenebilir biçimde kullanması.

## Kampanya yönetimi
WAREN → ARTIKEL → ANGEBOTE / AKTIONEN menüsü eklendi. Ayrıca Stammdaten içinde
WARENGRUPPE ve ARTIKEL bölümlerine doğrudan ANGEBOT butonu eklendi.

Hızlı oranlar:
- 10 %
- 15 %
- 20 %
- 25 %
- 30 %

Kapsam:
- ALLE ARTIKEL
- AKTUELLE WARENGRUPPE
- AKTUELLER ARTIKEL

Başlangıç ve bitiş tarihi dahil olacak şekilde DatePicker ile belirlenir. Tarih gelince
otomatik uygulanır, bitiş tarihinden sonra otomatik olarak artık seçilmez.

## Fiyat kuralı
Normal ürün fiyatı değiştirilmez. Satış anında en uygun aktif kampanya seçilir.
Öncelik: en yüksek yüzde; eşit yüzde durumunda ARTIKEL > WARENGRUPPE > ALLE ARTIKEL.

Pfand kesinlikle Angebot indirimine girmez. Örnek: ürün 10,00 EUR + 0,25 EUR Pfand ve
%20 Angebot ise list price 10,25 EUR, Angebot indirimi 2,00 EUR, satış fiyatı 8,25 EUR.

## Immutable satış snapshot'ı
Cart/Checkout/ParkedReceipt/SaleItem üzerinde şu bilgiler snapshot olarak tutulur:
- Listenpreis
- tatsächlicher Verkaufspreis
- promotion_id
- promotion_name
- promotion_percent
- promotion_discount
- promotion_start_date
- promotion_end_date

Kampanya daha sonra deaktiviert olsa bile eski Parkbon/Bon/Rapor değişmez.

## Bon
Promosyonlu ürün satırında Listenpreis → Angebotspreis görünür. Ayrıca:
- ANGEBOT adı
- yüzde
- geçerlilik başlangıç/bitiş tarihi
- kampanya indirim tutarı
- Pfand varsa 'nicht rabattiert' bilgisi
yazdırılır. Manuel Rabatt ayrı satırdır.

## Z- / X-Bericht
Yeni finansal ayrım:
- Listenwert vor Angebot/Rabatt
- Angebote / Aktionen
- Manuelle Rabatte
- Umsatz nach Rabatt (brutto)
- Belegstorno / Gegenbuchung
- Retouren
- Umsatz nach Storno/Retouren
- Zahlarten Bar/Karte
- Umsatzsteuer nach Rabatt: Brutto / Netto / Steuer
- Sofort-Storno vor Zahlung ayrıca bilgi kalemi

Her aktif satış kampanyası ayrıca ad, yüzde, tarih, Bon sayısı, Listenwert, Rabatt ve
Artikelumsatz ile gösterilir.

Not: R71 yalnız raporlama/veri modelinde STORNO/RETURN ayrımını hazırlar. Gerçek
BON-STORNO/RETOURE Gegenbuchung workflow'u henüz production olarak açılmamıştır.

## Monatsübersicht / Export / Cloud
Aylık rapor aynı Angebot/Rabatt ayrımını içerir. Buchungsdaten CSV ve local-first Cloud
sale event'i de immutable promotion snapshot alanlarını taşır.

## Rabatt stacking
Varsayılan: Angebot + manueller Rabatt otomatik birleşmez. Ayarlarda açıkça izin
verilmedikçe aktif Angebot olan sepette manuel Rabatt bloke edilir.

## Audit
Kampanya oluşturma `PROMOTION_CREATED`, deaktivieren `PROMOTION_DISABLED` olarak
immutable audit_log'a aynı DB transaction içinde yazılır. Kampanya silinemez. Name,
yüzde, tarih ve kapsam sonradan inplace değiştirilemez; yanlışsa deaktive edilip yenisi
oluşturulur.

## Schema
Schema V3. R70 V2 → R71 V3 migration öncesi mevcut mekanizma verified PRE-MIGRATION
backup alır.

## Diagnose
SYSTEMSTATUS / DIAGNOSE ekranına ANGEBOTE durumu eklendi: aktif / planlı / geçmiş /
toplam kampanya sayısı.

## Test
13 yeni Safety Check. Windows hedefi: ALL 283 CHECKS PASSED.

## Windows kabul testi
1. 7-SICHERHEITSTESTS.bat → ALL 283 CHECKS PASSED
2. R70 DB ile aç → DATENBANK-SCHEMA V3 AKTUELL
3. Stammdaten → Warengruppe → ANGEBOT → %20, bugün–+3 gün
4. Ürün ekle → sepet Listenpreis → Angebotspreis göstermeli
5. Pfand'lı üründe Pfand tutarı değişmemeli
6. Bon testinde Angebot adı/yüzde/tarih görünmeli
7. RABATT tıkla → stacking kapalıysa aktif Angebot nedeniyle bloke olmalı
8. X-Bericht/Z-Bericht → Angebot ve manueller Rabatt ayrı satırlar
9. Angebot deaktive et → yeni sepete artık uygulanmamalı; eski Parkbon snapshot'ı değişmemeli
