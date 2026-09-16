# R38 – TOR POS Cloud Foundation

Basis: R37 SumUp 1€ Device Test. SumUp ödeme/test koduna dokunulmadı.

## Neu
- Einstellungen → Geräte altında `TOR POS Cloud · Verbindung` bölümü.
- Cloud Server-URL ve cihaz kodu ayarlanabilir.
- Gerätetoken yalnız bağlantı testi sırasında bellekte tutulur ve test sonunda temizlenir; R38 içinde kalıcı kaydedilmez.
- `TOR CLOUD VERBINDUNG TESTEN` çağrısı yalnız `/api/v1/devices/ping` endpoint'ine gider.
- Live URL için HTTPS zorunlu; HTTP yalnız localhost / 127.0.0.1 / ::1 geliştirme testi için kabul edilir.
- Bağlantı testi hiçbir satış, Bon, TSE, DSFinV-K, stok veya müşteri verisi göndermez.

## Architekturentscheidung
TOR POS Cloud `local-first` çalışacaktır. Cloud erişilemez olduğunda kasa satış işlemini sürdürmelidir.
Sonraki aşamada durable `cloud_outbox` eklenecek; satış tamamlandıktan sonra cloud event ayrı arka plan kuyruğundan gönderilecektir.

## Nicht geändert
- SumUp / ZVT ödeme akışı
- checkout safety / payment guard
- satış commit
- receipt numbering
- TSE / DSFinV-K
- yazıcı kuyruğu
- Bon-Historie

## Test
Bu ortamda .NET SDK bulunmadığından Windows/Avalonia build yapılmadı. Kaynak için statik kontroller yapıldı.
