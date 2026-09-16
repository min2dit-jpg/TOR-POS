# TOR POS v0.7.33 – Bedienkomfort Plan

## Hedef
Müşteri/kasiyer programı eğitim almadan kullanabilmeli. Günlük satış akışı hızlı, teknik/fiskal güç arka planda olmalı.

## Rakiplerden alınacak dersler
- POSprom: olgun Office/Einstellungen mantığı; fakat görünür karmaşıklık azaltılacak.
- LaCash: barcode + dokunmatik artikel tuşu + merkezi giriş + seçili satır işlemleri güçlü; fonksiyonlar konfigürasyona göre gösteriliyor.
- KasseSpeedy: Android/touch yaklaşımı; büyük hedefler ve kısa satış akışı referans alınacak.
- BlitzHandel / Blitzkasse: Handel ve Restaurant kullanım ayrımı; TOR'da KIOSK/IMBISS profilleri daha da netleştirilecek.

## Tasarım kuralları
1. Normal satış: ürün -> Bezahlen -> Bar/Karte.
2. Barkod satış: okut -> okut -> Bezahlen -> ödeme.
3. Sık kullanılan işlem ana ekranda 1 dokunuş.
4. Seyrek kullanılan yönetim işlemi en fazla 3 dokunuş.
5. Kasiyer teknik terim görmez; TSE/ZVT/DSFinV-K/port/log ayrıntısı Admin/Techniker alanında.
6. Hata mesajı iki katmanlı: müşteriye kısa çözüm, teknisyene ayrıntı.
7. KIOSK ve IMBISS aynı program çekirdeğini kullanır ama gereksiz fonksiyonları birbirine göstermez.

## Uygulama sırası

### Aşama 1 – Hauptkasse sadeleştirme [BAŞLADI]
- [x] Hızlı şeritte yalnız Rabatt / Geparkte Bons / Bon Ein-Aus.
- [x] Bon-Historie / Einlage-Entnahme / Z-Bericht / Bon-Storno -> KASSE menüsü.
- [ ] Ödeme alanını tek odak noktası yap: BAR ve KARTE en büyük iki aksiyon.
- [ ] Seçili sepet satırında bağlama duyarlı +1 / -1 / Storno.
- [ ] Boş sepette gereksiz işlem düğmelerini pasif/gizli tut.
- [ ] KIOSK: scanner-first; IMBISS: touch-first varsayılanı.

### Aşama 2 – Einstellungen sadeleştirme
Müşterinin gördüğü ana gruplar:
- Kasse & Bedienung
- Firma & Bon
- Artikel & Steuern
- Zahlung
- Geräte
- Personal
- Datensicherung
- Erweitert / Techniker

Erweitert / Techniker altında:
- TSE-Aktivierung
- Recht & Fiskal
- ZVT teknik parametreleri
- Scanner gelişmiş ayarlar
- Lizenzierung
- System / Pfade / Performance / Logs

### Aşama 3 – İlk kurulum sihirbazı
Firma -> Betriebsart -> Drucker -> TSE -> Kartenterminal -> Test -> Fertig.
Kurulum tamamlandıktan sonra müşteri normalde teknik ayarlara girmek zorunda kalmamalı.

### Aşama 4 – Satış UX
- Hızlı ürün arama ve EAN fallback.
- Bilinmeyen barkod: tek pencerede Artikel anlegen / Freier Artikel / Abbrechen.
- Miktar girişi LaCash'teki güçlü yaklaşım gibi hızlı; fakat komut sözcüğü gerektirmeden görsel.
- Rabatt: yüzde / EUR / yeni fiyat; yetkiye bağlı.
- Parken: tek dokunuş; geri çağırma büyük kartlarla.
- Son bon/kopya: fiskal olarak yeni satış oluşturmadan.

### Aşama 5 – KIOSK / IMBISS ayrımı
KIOSK: Pfand, EAN, çok ürün, stok, hızlı arama.
IMBISS: Extras, Varianten, Menü/Combo, Im Haus/Außer Haus, masa/park akışı (gerekiyorsa).

### Aşama 6 – Stabilitäts-Offensive
- Crash recovery / açık sepet kurtarma.
- Printer/TSE/ZVT timeout izolasyonu.
- SQLite integrity + backup restore testi.
- 10k ürün / 500k satış benchmark.
- Scanner burst test.
- ZVT ambiguous result recovery.
- TSE çıkarma/geri takma recovery.
- Tekrarlı tıklama/double-submit koruması.

### Aşama 7 – Almanya üretim hazırlığı
- Gerçek Swissbit Hardware TSE kabul testi.
- DSFinV-K 2.4 end-to-end doğrulama.
- TSE TAR export doğrulama.
- Storno/Rückgabe fiskal yaşam döngüsü.
- Z-Abschluss gerçek üretim testi.
- Kassen-Nachschau test paketi.

## v0.7.33 çıkış kriteri
- Kasiyer ana ekranı gözle görülür biçimde sade.
- Normal satışta menü açma gerekmiyor.
- Admin fonksiyonları kaybolmadan ikinci seviyeye taşınmış.
- KIOSK ve IMBISS günlük ekranları kendi işine odaklı.
- Windows build + temiz kurulum + update kurulumu geçiyor.

## Umgesetzt – Einstellungen Phase 1
- Navigation von 15 Einträgen auf 8 verständliche Bereiche reduziert.
- Kartenzahlung und Terminal zusammengeführt.
- Geräte und Scanner zusammengeführt.
- Firma und Bon zusammengeführt.
- TSE, Fiskal, Lizenz und System unter **Erweitert / Techniker** gebündelt.
- Alte interne Seitennamen bleiben über Alias-Auflösung kompatibel, damit vorhandene Menüaufrufe weiterhin die richtige Gruppe öffnen.
- Artikelpflege bleibt bewusst unter **STAMMDATEN** statt doppelt in Einstellungen zu erscheinen.
