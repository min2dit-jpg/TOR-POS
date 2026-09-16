# R44 – Update / Lizenz-Betriebssicherheit

Basis: R43 Parkbon / Varianten / Bonlogo.

- `TorUpdateService` eklendi: TOR update manifest kontrolü, HTTPS/localhost kuralı, Setup indirme, SHA-256 doğrulama.
- Uzak/produktif update için Authenticode `Valid` + beklenen signer thumbprint kontrolü zorunlu; localhost geliştirme testi hash ile yapılabilir.
- Update başlamadan önce SQLite backup oluşturulur; açık satış, ödeme veya recovery durumu update'i engeller.
- Installer TOR POS prosesi kapandıktan sonra çalıştırılır; satış sırasında uygulama kendi kendine kapanmaz.
- `Einstellungen → Erweitert / Techniker → System → TOR Update`: Server URL, otomatik kontrol ve manuel kontrol.
- Ana kasada yalnız admin için yeni sürüm düğmesi görünür.
- Lisans bitişine 30 gün veya daha az kaldığında ana kasada kesintisiz badge; 7/3/1 günlerde renk belirginleşir.
- Lisans ekranında kalan süre açık metinle gösterilir.
- SumUp kaynakları değiştirilmedi.

Not: Gerçek remote update yayınlamak için signed `TOR-POS-Pro-Setup.exe` ve `Cloud/PUBLISH-UPDATE.ps1` ile manifest oluşturulmalıdır.


> Güvenlik notu: Canlı/uzak otomatik güncelleme, TOR/Demirkaan GmbH code-signing sertifikasının thumbprint değeri `TorRelease.UpdateSignerThumbprint` içine sabitlenmeden bilinçli olarak engellenir. Manifest içindeki thumbprint tek başına güven kaynağı değildir. Localhost geliştirme testi bu kilitten muaftır.
