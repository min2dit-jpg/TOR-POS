# TOR POS R70 – Audit Integrity & Controlled POS Actions

## Amaç
SOFORT STORNO, RABATT ve C / Verkauf abbrechen gibi hassas kasa işlemlerinin
izlenebilir, zorunlu nedenli ve uygulama içinden değiştirilemez şekilde
protokollenmesi.

## Schema V2
Yeni tablo:
`pos_action_log`

Önemli alanlar:
- created_at
- action_id
- phase
- actor
- register_id
- operation_id
- action_type
- reason
- entity_type / entity_id
- before_total_cents
- after_total_cents
- amount_cents
- details
- prev_hash
- entry_hash

UPDATE ve DELETE SQLite trigger ile bloke edilir.

## Hash zinciri
Her satır SHA-256 ile bir önceki satırın hash'ine bağlanır.

Amaç:
Birisi raw SQLite erişimi ile trigger'ı kaldırıp eski bir controlled-action
satırını değiştirirse Diagnose bütünlük kontrolünün bunu fark edebilmesi.

Bu yapı `tamper-evident`tir. Henüz harici/private-key imzalı günlük checkpoint
olmadığı için kriptografik anlamda tam `tamper-proof` iddiası yapılmaz.

## Audit-first / fail-closed
Hassas işlem sırası:
1. Yetki kontrolü
2. Zorunlu neden
3. AUTHORIZED kaydı durable DB'ye yazılır
4. Kasa işlemi uygulanır
5. APPLIED kaydı yazılır

AUTHORIZED audit yazılamazsa:
İŞLEM UYGULANMAZ.

Uygulama sırasında hata olursa:
FAILED satırı yazılmaya çalışılır ve teknik hata görünür hale gelir.

Yetkisiz denemeler:
DENIED olarak loglanır.

## SOFORT STORNO
Zorunlu olarak:
- ürün
- miktar
- birim fiyat
- satır toplamı
- MwSt.
- mevcut indirim
- before/after total
- operator
- kassennummer
- operation id
- neden

protokollenir.

## RABATT
Artık neden zorunludur.
Eski indirim, yeni indirim ve satış toplamının before/after değeri loglanır.

## C / Verkauf abbrechen
Sadece sayısal giriş temizleniyorsa audit gerekmez.
Gerçek dolu sepet iptal ediliyorsa neden zorunludur.

Geri çağrılmış Parkbon iptalinde:
Önce durable ParkedReceipt DB'de cancel edilir,
sonra RAM sepeti temizlenir.
DB cancel başarısızsa ekrandaki sepet korunur.

## Einstellungen
Kasse · Technische Feinabstimmung altında:
- Stornogründe
- Rabattgründe
- Abbruchgründe

ayrı ayrı düzenlenebilir.

## Diagnose
SYSTEMSTATUS / DIAGNOSE:
`AUDIT-INTEGRITÄT · OK`

Kontrol:
- hash zinciri
- satır sayısı
- bozulmuş entry tespiti

## Schema Migration
R69 V1 -> R70 V2 geçişinde mevcut müşteri DB'si için migration öncesi
otomatik verified PRE-MIGRATION backup alınır.

## Test
8 yeni Safety Check:
1. schema V2
2. authorized+applied hash chain valid
3. previous hash linking
4. normal audit_log mirror
5. UPDATE blocked
6. DELETE blocked
7. duplicate phase blocked
8. raw DB tamper detection

Windows hedefi:
ALL 270 CHECKS PASSED

## Windows kabul testi
1. 7-SICHERHEITSTESTS.bat -> ALL 270 CHECKS PASSED
2. R69.1 DB ile R70 aç:
   Diagnose -> DATENBANK-SCHEMA V2 AKTUELL
3. Diagnose -> AUDIT-INTEGRITÄT OK
4. Sepete ürün ekle -> SOFORT STORNO:
   neden penceresi açılmalı.
5. Neden seçmeden Storno uygulanmamalı.
6. RABATT -> tutar -> neden seçimi.
7. C -> dolu sepet -> Abbruchgrund seçimi.
8. Sonra Diagnose:
   AUDIT-INTEGRITÄT OK ve entry sayısı artmış olmalı.
