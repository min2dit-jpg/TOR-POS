# TOR POS R52 – IMBISS Artikel / Warengruppe Düzeni

Bu sürüm, gönderdiğin ekran görüntülerine göre IMBISS ürün yönetimi akışını sadeleştirmek için hazırlandı.

## Yapılan ana değişiklikler

### 1) Artikel sayfası sadeleştirildi
- **Artikel** sekmesinde artık solda **Warengruppen listesi**, ortada yalnızca seçilen warengruppe içindeki **artikeller**, sağda ise **Artikel ekleme / düzenleme alanı** var.
- Böylece kullanıcı önce warengruppe seçiyor, sonra sadece o grubun ürünlerini görüyor.
- Sağ panel aynı ekranda kaldığı için yeni artikel eklemek için başka yere gitmek gerekmiyor.

### 2) Artikel listesi artık karışık değil
- Artikel listesi artık tüm ürünleri aynı anda karışık göstermiyor.
- Sol tarafta hangi warengruppe seçildiyse, orta listede sadece o warengruppe’ye ait ürünler geliyor.
- Her artikel satırında kendi **Warengruppe**, **Artikel-Nr.**, **Bestand** ve **Preis** birlikte gösteriliyor.

### 3) Artikel seçince aynı grubun ürünleri görünür
- Örneğin solda **Döner** warengruppe seçildiğinde sadece Döner grubundaki ürünler listelenir.
- Orta listeden bir döner ürünü seçildiğinde sağ taraftaki Artikel düzenleme alanı açık kalır.

### 4) IMBISS ekranında KIOSK kalıntıları temizlendi
- IMBISS seçildiğinde eski karışık KIOSK kategorilerinin (ör. Haushaltswaren, Lebensmittel, Tabak, Zigaretten, Schnellwahl, Snacks) tekrar görünmesi engellendi.
- Bu veriler silinmiyor; sadece kendi edition alanında kalıyor.

### 5) Ürün görselleri yenilendi
- IMBISS starter ürün görselleri daha düzenli ve daha gerçekçi görünecek şekilde yenilendi.
- Döner, burger, fingerfood, pizza ve içecek görselleri yeniden düzenlendi.

## Not
- Bu sürüm kaynak düzenleme seviyesinde hazırlandı.
- Kullanıcıya ait mevcut ürün, stok, barkod ve manuel yüklenen kendi görselleri korunur.
