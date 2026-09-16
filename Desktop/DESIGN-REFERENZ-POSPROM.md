# TOR POS Pro v0.4 – POSprom ayar yapısından alınan dersler

İncelenen kaynaklar:
- Anleitung_POSprom_Suite.pdf
- Anleitung_POSprom_HandelPlus_TSE.pdf

Amaç POSprom'u kopyalamak değil; özellikle "Einstellungen / Office" bölümündeki olgun ürün mantığını
TOR POS'a daha modern ve Kiosk/Imbiss odaklı biçimde taşımaktır.

## TOR POS'a aktarılan yapı

### 1. Allgemein
- Kassennummer
- Kassenname
- Kiosk / Imbiss işletme modu
- Başlangıç ekranı
- Ekran/Touch ölçeği
- Theme
- Währung
- Startgeld

### 2. Firmendaten
- Firma
- Inhaber
- Anschrift
- Telefon / E-Mail
- Steuernummer
- USt-IdNr.
- Bon / Bericht üzerinde kullanma seçenekleri

### 3. Funktionen
- Einmannbetrieb
- Login zorunluluğu
- Freie Preiseingabe
- Pfand / Leergut
- Bediener auf Bon
- Varianten auf Bon
- Kassenlade
- Mindestbestand uyarısı
- Z-Bericht otomatik yazdırma
- Stornogründe

### 4. Zahlarten
- Bar
- Karte
- Auf Rechnung
- Ödeme tuşu isimleri
- ZVT için ayrı bağlanma katmanı

### 5. Steuern
- Standard ve indirimli KDV
- Yeni üründe varsayılan KDV
- DATEV Konto / Gegenkonto / Kennzeichen için hazırlık

### 6. Bon & Rechnung
- Kopfzeile
- Fußzeile
- Ausrichtung
- Zeichenbreite
- Auto-Bondruck
- Letzter Bon

### 7. Geräte-Manager
- Bondrucker
- Kassenschublade
- Kundenanzeige
- A4-Drucker
- Donanımın satış ekranından izole edilmesi

### 8. Scanner
- HID / USB önceliği
- ENTER-Suffix
- alternatif Scanner-Wartezeit
- bilinmeyen barcode uyarısı
- barkod araması RAM cache üzerinden

### 9. Datensicherung
- Program kapanışında backup
- ayrı backup klasörü
- Verzeichnis testen
- manuel backup oluşturma

### 10. Personal & Rechte
- Einstellungen
- Storno
- Z-Abschluss
- Rabatt
- maksimum Rabatt limiti
- kullanıcıları ileride silmek yerine pasif etme

### 11. System
- veri klasörü
- DB yolu
- resim klasörü
- backup yolu
- performans sayaçları
- açık TSE test durumu

## TOR POS farkı

POSprom'daki eski Windows form görünümü alınmadı.
TOR POS'ta sol navigasyonlu, modern Office/Einstellungen düzeni kullanıldı.
Satış hot-path'i internetten ve ayar DB sorgularından bağımsız kalır.
TSE / ZVT / Drucker modülleri satış UI'sini donduramayacak şekilde ayrı adapter/queue olarak tasarlanacaktır.
