# R54: inceleme ve temel geliştirme sırası

Temel: teslim edilmiş R53. Bu inceleme özellikle sipariş, menü/stok, dil,
görsel geçişi, yazdırma ve yedekleme yollarını kapsar. Bütün programın her
akışının hatasız olduğuna dair bir onay değildir. Windows donanım kabulü yapılmadı.

## R54'te düzeltilen somut noktalar

| Bulgu | Değişiklik / kanıt |
|---|---|
| Sipariş hazırlığı ayrı tutulmuyordu. | `OrderWorkflowService`: kalıcı durum, ayrı ödeme bilgisi, eğitim ayrımı, sürüm kontrolü ve işlem içi audit kaydı. Durum değişikliği satış/stock tablolarına yazmaz. |
| Eğitim siparişi tahsilatta iptal edilmiş gibi kapanıyordu. | Yeni test işlemleri `SIMULATED` ve açık test ödeme etiketiyle saklanır. Eski iptal kayıtlarından tahsilat sonucu türetilmez. |
| Eski seçimle yeni sipariş bilgisi ezilebilirdi. | `workflow_version` karşılaştırması; sepet içeriği değişirse sürüm yükselir ve hazırlık başa döner. |
| Menünün içinde başka menü saklanabiliyor, stok yalnız bir seviye işleniyordu. | Kaydetmede aktif tekil bileşen kontrolü ve ters yönde iç içe menü oluşturma engeli. Eski hatalı kayıtlar otomatik silinmez; düzenlenmelidir. |
| Açık siparişin menü reçetesi değiştirilebiliyordu. | Açık sipariş kullandığı sürece reçete değişikliği engellenir; aynı reçetenin yeniden kaydı mümkündür. |
| Uzun mutfak fişi tek sayfada kesilebiliyordu. | Karakter/satır ilerlemesiyle sayfalama, her sayfada sipariş kimliği, devam uyarısı; baskı ilerleyemiyorsa açık hata. Fiziksel sonuç henüz doğrulanmadı. |
| Mutfak değişiklik fişi yeni sipariş sanılabilirdi. | Tam güncel siparişin önceki fişi değiştirdiği açıkça yazılır. STORNO/HINWEIS ürünlerden önce basılır. Fark satırlarını hesaplayan ayrı delta fişi henüz yoktur. |
| Yedek adları saniye hassasiyetindeydi. | Milisaniye + benzersiz kimlik; aynı anda iki yedek testi. |
| Eski dil ayarı ve hazır görseller korunuyordu. | Dil sözlükleri/seçenek kaldırıldı; geçmiş TR/EN ayarı silinir. Ayrılmış `tor-imbiss-` görsel referansları kaldırılır, müşteri fotoğrafları korunur. |

## Sıradaki mimari işler

1. **Sipariş + mutfak baskısı için veritabanında kalıcı gönderim kaydı.**
   Şu anda `OnParkClick` önce siparişi saklıyor, sonra ayrı yazdırma kuyruğunu
   çağırıyor. Bu iki adım arasındaki kapanma, mutfağa gönderilmemiş sipariş bırakabilir.
   Sipariş değişikliği ve gönderilecek baskı içeriği aynı transaction içinde
   yazılmalı; işçi bu kaydı mevcut yazdırma altyapısına aktarmalı.
   Gerçek basılıp basılmadığı belirsiz işlerde otomatik tekrar baskı yapılmamalı.

2. **Sipariş anının menü bileşenlerini saklama.**
   `BuildKitchenLines` katalogdan, satışın stok adımı `product_combo_items` tablosundan
   güncel reçete okuyor. R54 açık sipariş reçetesini kilitleyerek riski azaltır.
   Kalıcı çözüm: sipariş/satış satırında bileşen kimliği, miktar ve adın snapshot'ı;
   tekrar baskı ve stok aynı snapshot'ı kullanmalı. Menüde farklı vergi oranları ve
   indirim dağılımı da bununla birlikte ayrı hesaplama testlerine bağlanmalı.

3. **Checkout akışını MainWindow'dan hizmet katmanına taşıma.**
   Sepet kilidi, ödeme journal'ı ve üretim engeli korunmalı. Önce mevcut davranışı
   uçtan uca hata senaryolarıyla sabitle, sonra adım adım taşı. UI yalnız komut ve
   sonucu göstermeli. Yeni OrderWorkflowService bu ayrım için küçük bir başlangıçtır.

4. **Tam yedek ve geri yükleme provası.**
   Mevcut SQLite Backup API tutarlı veritabanı kopyası üretir. Ancak müşteri
   resimleri, logo ve dış dosya günlükleri veritabanı dosyasının içinde değildir.
   Tam paket + dosya bütünlüğü + boş Windows bilgisayarına geri yükleme testi gerekir.
   Windows hesabına bağlı şifrelenmiş anahtarlar başka hesaba kopyalanmış olmakla
   çalışır kabul edilmemeli; yeniden eşleştirme açıkça tasarlanmalı.

5. **Cloud'a sipariş durumu ve sayfalama.**
   R54 yeni sipariş hazırlık bilgisini henüz göndermiyor. Durum olayı, sipariş kimliği
   ve sürümüyle mevcut outbox'a bağlanmalı; tekrar ve sıra dışı teslim test edilmeli.
   Yerel sipariş listesi 500 kayıtla sınırlı. Büyüme öncesi veritabanından arama ve
   sayfalama, 10.000 ürün / 500.000 satış yük testi gerekir.

6. **Donanım ve hata enjeksiyonu kabulü.**
   Windows + scanner + Bon/A4 + mutfak yazıcısı; sürücü takılması, elektrik kesilmesi,
   disk dolması, bozuk kurtarma dosyası, bağlantı kesilmesi, uygulama yeniden açılışı.
   Yazılım testleri bu saha testlerinin yerine geçmez. TSE, DSFinV-K ve gerçek
   terminal tahsilatı için mevcut üretim engeli kaldırılmadı.

## Doğrulama

- .NET 10 derlemesi: 0 hata (tam derlemede 8 mevcut uyarı).
- Desktop: 182 kontrol geçti; yeni R54 testleri gerçek SQLite dosyalarıyla çalıştı.
- Cloud: 16/16 test geçti; Cloud kaynakları değiştirilmedi.
- SumUp iki bağlantı kaynağı R53 ile SHA-256 karşılaştırmasında aynı.
- Ayrıntılı loglar ve değişmeyen kaynak hash'leri `verification/R54` altında.

Öncelik: yeni büyük özellikten önce ilk iki madde ve geri yükleme provası.
