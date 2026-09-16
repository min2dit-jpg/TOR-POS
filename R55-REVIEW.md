# R55 — uygulanan düzeltmeler ve sınırlar

Temel paket R54'tür. Sayısal sürüm artık uygulama, FileVersion ve Inno Setup'ta
`0.7.33.550`; bilgi sürümünde ayrıca R55 etiketi bulunur. Önceki `.540` dosya
sürümünden geriye düşülmedi. `stockUpdated` ölü dalı kaldırıldı.

## Sipariş ve baskı işlemi

`ParkedReceiptRepository` kabul/değişiklik/iptal işlemlerinde sipariş, audit ve
`order_print_outbox` kayıtlarını aynı SQLite transaction içinde yazar. Yazıcı
ayarından dolayı baskı içeriği hazırlanamazsa sipariş de kaydedilmez; operatör
ayarları düzeltip tekrar deneyebilir. Baskı kapalıysa baskı kaydı oluşturulmaz.

Outbox ürün ve menü bileşenlerinin o andaki ad/miktarlarını ve seçili yazıcıyı
saklar. Sonraki katalog değişikliği bu payload'ı değiştirmez. Bu, mutfak baskısı
snapshot'ıdır; satış/stok modelinin tamamının snapshot'a taşındığı anlamına gelmez.

`OrderPrintDispatcher`, sabit kimlikle mevcut `PrintJobJournal` sistemine aktarır.
Günlükte aynı kimlik varsa göndermez. Günlük yazılmış, fakat outbox onayı henüz
yazılmamış bir kapanma bu şekilde ikinci baskı başlatmaz. Günlükteki belirsiz
kayıtlar mevcut yönetici kontrolüne gider. SPOOL_ACCEPTED yalnız sürücünün
çağrıdan döndüğünü ifade eder; kâğıt çıktısını kanıtlamaz. HANDED_OVER yalnız
sorumluluğun yazdırma günlüğüne aktarıldığını ifade eder.

Gönderilmemiş NOT_SUBMITTED işleri artık kontrol ekranında kaybolmaz.
Aktarılamamış outbox işleri bekler, tekrar denenir; yazıcı hatası günlüğe yazılır.
Tek uygulama örneği kuralı korunur. Hazır kabul edilmiş siparişlerin baskısı
başlangıçta, kullanıcı giriş yapmadan da devam edebilir.

## Güncelleme yayını

`PUBLISH-UPDATE.ps1` önce geçici kopyayı hash'ler, Authenticode imzasını ve beklenen
sertifikayı doğrular, ardından hash'i yeniden karşılaştırır. `update-store.js`
aktarılan kopyayı tekrar hash'ler ve değişmez hash adlı EXE oluşturur. Manifest
geçici dosya + aynı klasörde rename ile son adımda değiştirilir. Eski EXE'ler
üzerine yazılmaz. Yayınlama/devre dışı bırakma aynı kilidi kullanır.

Node modülü dahili yayınlama adımıdır; tek başına Authenticode doğrulayıcı değildir.
Windows giriş noktası PowerShell betiğidir. Kilit dosyası çökmede kalırsa çalışan
bir yayın olmadığı doğrulandıktan sonra kaldırılmalıdır. Aktif manifest değişince
eski URL talebi mevcut sunucuda 404 alabilir; yeniden güncelleme kontrolü gerekir.
Üretim imza sertifikası bu pakette kurulmuş değildir; Desktop'ın sabit sertifika
kontrolü ve kapalı uzaktan kurulum sınırı kaldırılmadı. Yönetici yetkisi zorla
istenmez; hedef yayın klasörüne yazma yetkisi gerekir.

## Veri + görsel yedeği ve kontrol

`FullBackupService` SQLite Backup API ile veritabanı kopyası alır, uygulamanın
ProductImages, ReceiptAssets ve PrintJobs klasörlerini ekler. Dosya hash'lerini
manifestte saklar. Kontrol, yalnız yeni bir klasöre çıkarır; mevcut hedef üzerine
yazmaz. Dosya hash'leri, SQLite integrity_check ve foreign_key_check çalışır.
Bozuk içerik testi paketin reddedildiğini doğrular. Dosya işleri UI dışında yürür.

Bu özellik bir disk imajı veya tamamlanmış canlı geri yükleme sihirbazı değildir.
Canlı açık sepet dosyaları ve harici klasörler kapsam dışıdır. DB ile bütün dosya
sisteminin aynı anda snapshot'ı alınmaz. En temiz taşınma yedeği, açık işlemler ve
baskılar kontrol edilmişken alınmalıdır. Farklı Windows hesabında şifreli erişim
bilgileri yeniden kurulmalı; mutlak resim yolları gerekirse uyarlanmalıdır.

## Testler

- Desktop: 195 kontrol; gerçek SQLite dosyaları, outbox aktarımında kesinti
  benzetimi, yanlış baskı ayarının/audit hatasının tam rollback'i, değişiklik/iptal
  baskı kayıtları, yeniden başlatmada tekrar göndermeme, yedek geri açma ve bozulma.
- Cloud: 19 test; önceki 16 test + 3 yayın deposu testi. Hash hatasında eski yayının
  korunması, değişmez dosyalar, kilit ve devre dışı bırakma.
- .NET 10 build: 0 hata, 8 mevcut uyarı.
- Windows Authenticode/UAC ve gerçek donanım kabulü yapılmadı. Node testleri
  imza doğrulamasının Windows'ta çalıştırıldığı anlamına gelmez.

## Sonraki işler

1. Boş Windows bilgisayarında gerçek geri yükleme ve cihaz bağlantısı provası.
2. Park kabulünün sepet kurtarma kimliğiyle tam idempotent yapılması: commit
   sonrası UI temizlenmeden kapanma hâlinde aynı kabulü bulma.
3. Menü bileşenlerinin satış/stok snapshot'ı ve vergi/indirim dağılımı testleri.
4. Sipariş hazırlık durumlarının Cloud outbox olaylarına bağlanması.
5. Büyük veri yük testi, outbox/baskı günlüğü arşivleme ve saklama politikası.

TSE/DSFinV-K üretim engeli ve SumUp kaynakları korunur.
