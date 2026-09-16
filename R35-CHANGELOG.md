# R35 – Bon-Archiv

- Arşiv penceresi boş sonuçta da açılır; tarih, tam fiş numarası ve ödeme filtresi sunar.
- Z kapanışından bağımsız, salt okunur ve parametreli sorgu kullanılır.
- Sorgular mevcut arka plan I/O kuyruğunda; 201 sonuç sınırı ile 200 satır ve taşma uyarısı.
- Tarih filtresi kayıttaki yerel takvim gününü kullanır. Sıralama gerçek zamana göre yapılır.
- Önizleme/PDF ve kayıtlı kopya baskısı korunur. Boş listede ilgili düğmeler devre dışıdır.
- Sorgu sürerken yinelenen arama engellenir; geçersiz tarih/numara açıklaması gösterilir.
- R34 güvenlik engelleri ve test fişi korunur.

Doğrulama günlükleri verification/r35-build.log ve r35-safety-tests.log dosyalarındadır.
Test arşiv kayıtları yalnızca otomatik testin geçici veritabanına yazılır; kullanıcı verisi üretilmez.
