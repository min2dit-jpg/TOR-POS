# TOR POS R69 – Versioned Database Migrations

## Neden?
R68.1'e kadar veritabanı şeması ağırlıklı olarak `EnsureColumnAsync` ve
`CREATE TABLE IF NOT EXISTS` ile additive olarak güncelleniyordu. Bu yaklaşım
erken geliştirme için güvenliydi, ancak çok sayıda müşteri kurulumu olduğunda
hangi veritabanının hangi şema sürümünde olduğunu açıkça takip etmek zorlaşır.

## R69 mimarisi
Yeni tablolar:
- `schema_version`
- `schema_migrations`

Hedef şema:
- Version 1 = `R69_VERSIONED_SCHEMA_BASELINE`

R69'dan SONRA eklenecek yeni şema değişiklikleri `SqliteDatabase.InitializeAsync`
içine dağınık şekilde eklenmemeli. Sıralı migration olarak
`SchemaMigrationService` içine eklenmeli:
- 2
- 3
- 4
...

## Legacy uyumluluk
Mevcut çalışan müşteri DB'lerini riskli bir big-bang refactor ile yeniden
oluşturmadık. Eski `InitializeAsync` davranışı "legacy compatibility bootstrap"
olarak korunuyor.

Akış:
1. DB'nin mevcut şema sürümünü sadece READ-ONLY kontrol et.
2. Mevcut ve eski/unversioned DB ise PRE-MIGRATION BACKUP al.
3. Backup `PRAGMA quick_check` ile doğrulanmadan devam ETME.
4. Legacy compatibility bootstrap çalıştır.
5. `schema_version/schema_migrations` oluştur.
6. Eksik migration'ları sırayla transaction içinde uygula.
7. Her başarılı migration history'ye kaydedilir.
8. Son sürüm hedefle aynı değilse startup fail olur.

## Fail-closed davranışı
- Backup alınamıyorsa migration başlamaz.
- DB şeması uygulamadan daha yeniyse downgrade reddedilir.
- Migration transaction hata verirse version ilerletilmez.
- Aynı güncelleme ikinci kez başlatılırsa migration tekrar uygulanmaz ve
  gereksiz yeni pre-migration backup üretilmez.

## Backup
Varsayılan migration backup dizini:
`TOR POS Backups/Migrationen`

Dosya:
`TOR-POS-PRE-MIGRATION-YYYYMMDD-HHMMSS-...db`

Migration backup'ları otomatik "çöp temizliği" ile silinmez.

## Diagnose
SYSTEMSTATUS / DIAGNOSE ekranına:
`DATENBANK-SCHEMA`
eklendi.

Normal durumda:
`V1 AKTUELL`

Ayrıca history sayısı ve son migration zamanı gösterilir.

## Test
8 yeni Safety Check:
1. fresh DB -> V1, gereksiz migration backup yok
2. schema_version/history persist
3. legacy DB -> pre-migration backup oluşur
4. backup quick_check = ok
5. backup eski müşteri datasını korur
6. ikinci startup idempotent, yeni backup yok
7. newer schema/downgrade ve backup-failure fail-closed senaryoları

Windows hedefi:
ALL 262 CHECKS PASSED

## Windows kabul testi
1. Mevcut R68.1 database ile R69'u başlat.
2. Program açılmalı.
3. `Backups\Migrationen` altında PRE-MIGRATION backup oluşmalı.
4. KASSE -> Systemstatus / Diagnose:
   `DATENBANK-SCHEMA · V1 AKTUELL`
5. Programı kapat/aç:
   ikinci bir migration backup oluşmamalı.
6. Eski ürünler, satışlar, ayarlar ve kullanıcılar aynen kalmalı.
7. `7-SICHERHEITSTESTS.bat` -> ALL 262 CHECKS PASSED.
