# TOR POS R61 · Explicit Gmail STARTTLS Hotfix

Version: **0.7.33.610**

## Neden R61?
Gerçek Windows/Gmail testinde R60, `smtp.gmail.com:587`, TLS açık ve doğru App-Passwort kullanılmasına rağmen şu yanıtı aldı:

`530 5.7.0 Must issue a STARTTLS command first`

Bu yanıt kimlik doğrulama parolasından önce SMTP oturumunun TLS'e yükseltilmediğini gösterir. R60'ta `System.Net.Mail.SmtpClient.EnableSsl=true` zaten ayarlıydı; buna rağmen gerçek istemci/sunucu akışında Gmail STARTTLS görmedi.

## Çözüm
R61, aylık rapor ve test e-postası için `System.Net.Mail.SmtpClient` transportunu kaldırır ve RFC 3207 akışını doğrudan uygular:

1. TCP bağlantısı `smtp.gmail.com:587`
2. Server `220`
3. `EHLO tor-pos.local`
4. Server capability içinde `STARTTLS` doğrulanır
5. `STARTTLS` gönderilir ve `220` beklenir
6. `SslStream` ile gerçek TLS handshake yapılır
7. TLS sonrası zorunlu ikinci `EHLO`
8. `AUTH LOGIN`
9. Kullanıcı ve App-Passwort yalnızca TLS kanalında gönderilir
10. `MAIL FROM / RCPT TO / DATA`

## Güvenlik
- Sertifika doğrulaması kapatılmaz; `SslStream` varsayılan host/sertifika doğrulamasını kullanır.
- SMTP kullanıcı adı veya App-Passwort protokol hatalarına/loglara yazılmaz.
- App-Passwort boşluk temizleme ve DPAPI saklama R60'tan korunur.
- 60 saniye bağlantı timeout'u vardır.
- Mail `DATA` sonrası sunucu tarafından kabul edildiyse `QUIT` hatası tekrar gönderime yol açmaz.

## Diagnose
Yeni hata metni SMTP fazını gösterir (`CONNECT`, `EHLO`, `STARTTLS`, `AUTH`, `MAIL-FROM`, `RCPT-TO`, `DATA`). Böylece tekrar bir hata olursa hangi protokol adımının başarısız olduğu görülür.

## Test
R61 mevcut 240 regresyon testini korur. Bu ortamda .NET SDK bulunmadığından gerçek Windows derlemesi burada çalıştırılmamıştır; Windows'ta `7-SICHERHEITSTESTS.bat` ve ardından `1-SETUP-ERSTELLEN.bat` çalıştırılmalıdır.
