# TOR POS Cloud R62 · Google OAuth / QR Kurulumu

## 1. Public HTTPS adresi
TOR POS Cloud, telefondan erişilebilen gerçek bir HTTPS alan adı altında yayınlanmalıdır. Örnek:

`https://cloud.tor-pos.de`

`TOR_CLOUD_PUBLIC_URL` tam olarak bu origin olmalıdır; sonuna `/` koymayın.

## 2. Google Cloud projesi
1. Google Cloud Console'da proje oluşturun/seçin.
2. Gmail API'yi etkinleştirin.
3. OAuth consent screen / Branding / Audience / Data Access alanlarını doldurun.
4. Minimum gönderme scope'u olarak `https://www.googleapis.com/auth/gmail.send` ekleyin.
5. OAuth Client oluşturun: **Web application**.
6. Authorized redirect URI olarak şunu ekleyin:
   `https://<TOR_CLOUD_PUBLIC_URL-host>/google/oauth/callback`

## 3. Cloud environment
Örnek (değerleri kendi gerçek değerlerinizle değiştirin):

```text
TOR_CLOUD_PUBLIC_URL=https://cloud.tor-pos.de
TOR_GOOGLE_OAUTH_CLIENT_ID=1234567890-....apps.googleusercontent.com
TOR_GOOGLE_OAUTH_CLIENT_SECRET=...
TOR_CLOUD_GOOGLE_TOKEN_KEY=<en-az-32-karakter-rastgele-gizli-deger>
COOKIE_SECURE=true
TOR_CLOUD_DEMO=false
```

`TOR_CLOUD_GOOGLE_TOKEN_KEY` değişirse eski refresh token'lar çözülemez; bu anahtarı güvenli secret storage içinde yedekleyin ve source-code'a koymayın.

## 4. TOR POS cihazı
`Einstellungen > Geräte > TOR POS Cloud` bölümünde public Cloud URL, Gerätecode ve Gerätetoken kaydedilmiş olmalıdır.

Sonra:
`Einstellungen > Berichte & E-Mail > MIT GOOGLE ANMELDEN (QR)`

QR telefondan okutulur ve izin Google'ın kendi sayfasında verilir.

## 5. Veri akışı
- Telefon -> TOR POS Cloud: OAuth callback.
- Cloud -> Google: authorization-code / refresh-token exchange.
- Kasa -> Cloud: device-authenticated kısa ömürlü access-token isteği.
- Kasa -> Gmail API: MIME + PDF doğrudan gönderim.

Rapor PDF'leri TOR POS Cloud üzerinden taşınmaz.

## 6. Üretim öncesi
Public müşterilere dağıtım yapmadan önce Google OAuth app verification gereksinimlerini tamamlayın. `gmail.send` kullanıcı adına e-posta gönderme yetkisi verir; daha geniş Gmail okuma/değiştirme scope'ları R62 tarafından istenmez.
