# R20 Compile Hotfix
R19 FirstRunSetupWindow yanlışlıkla `TseProbeResult.Success` kullanıyordu.
`TseProbeResult` başarı bilgisini `State` ile verir.

R20:
- Ready = başarılı
- Connected = başarılı/algılandı
- diğer durumlar = uyarı

TSE aktivasyonu, ödeme, veritabanı ve fiskal işlem mantığı değiştirilmedi.
