# TOR POS R62 · Google OAuth QR + Gmail API

Version: **0.7.33.620**

## Amaç

R61'de Gmail STARTTLS teknik olarak düzeltilmiş olmasına rağmen gerçek Gmail testi `535 5.7.8` ile App-Passwort'u reddetti. R62, müşteri tarafında Gmail/App-Passwort saklamak yerine **MIT GOOGLE ANMELDEN (QR)** akışını ekler.

## Kullanıcı akışı

1. `Einstellungen > Berichte & E-Mail > MIT GOOGLE ANMELDEN (QR)`.
2. Kasa ekranda tek kullanımlık QR-Code gösterir.
3. İşletme sahibi QR'ı kendi telefonuyla okutur.
4. Telefon Google'ın kendi OAuth giriş/izin ekranına yönlenir.
5. TOR POS yalnızca `gmail.send` yetkisini ister (ek olarak hesap kimliğini doğrulamak için `openid email`).
6. Başarılı bağlantı kasaya aktarılır ve `Google verbunden` olarak gösterilir.
7. Test e-postası ve aylık PDF-Berichte Gmail API üzerinden gönderilir.

## Güvenlik tasarımı

- Google parolası TOR POS'a **hiç girilmez**.
- Google App-Passwort artık gerekli değildir.
- Google refresh token kasa bilgisayarında tutulmaz.
- TOR POS Cloud refresh token'ı AES-256-GCM ile korunmuş biçimde saklar.
- Kasa, gönderim gerektiğinde Cloud'dan yalnızca kısa ömürlü access token alır.
- PDF ve MIME içeriği TOR POS Cloud'a yüklenmez; rapor doğrudan **Kasa-PC -> Gmail API** gider.
- QR pairing linki tek kullanımlıdır ve yaklaşık 10 dakika sonra geçersiz olur.
- Pair secret ve claim token Cloud veritabanında hash olarak tutulur.
- OAuth state ve PKCE verifier korunur; Google OAuth callback state doğrulaması yapar.
- Google bağlantısı ayarlardan iptal edilebilir; Cloud refresh token'ı Google tarafında revoke etmeye çalışır ve yerel bağlantı bilgileri temizlenir.

## Teknik bileşenler

### Desktop
- `GoogleGmailService.cs`: QR pairing, access-token alma, Gmail API ile doğrudan gönderim.
- `GooglePairingWindow.cs`: harici QR servisi kullanmadan Avalonia içinde QR çizimi ve pairing durumu.
- `MailMimeBuilder.cs`: RFC/MIME + PDF attachment + Gmail API base64url payload.
- `ReportEmailService.cs`: Google transport tercih edildiğinde aylık PDF paketini Gmail API ile gönderir; R61 SMTP fallback kalır.
- `SettingsWindow.axaml.cs`: Google login/test/disconnect UI.

### Cloud
- `qr-v6.js`: bağımlılıksız QR Model 2 Version 6-L encoder.
- Device-authenticated pairing start/status/ack.
- Telefon için public pairing sayfası.
- Google OAuth Web flow + PKCE.
- Şifreli refresh-token storage.
- Kasa için kısa ömürlü access-token broker.

## Üretim için zorunlu Cloud ayarı

QR Google girişi **sadece** TOR POS Cloud public HTTPS adresinde çalıştırılıp aşağıdaki environment değişkenleri tanımlanınca gerçek Google hesabıyla kullanılabilir:

- `TOR_CLOUD_PUBLIC_URL=https://cloud.example.com`
- `TOR_GOOGLE_OAUTH_CLIENT_ID=...`
- `TOR_GOOGLE_OAUTH_CLIENT_SECRET=...`
- `TOR_CLOUD_GOOGLE_TOKEN_KEY=<uzun-rastgele-gizli-anahtar>`

Google Cloud Console'da:

- Gmail API etkinleştirilir.
- OAuth consent screen hazırlanır.
- OAuth Client Type: **Web application**.
- Authorized redirect URI: `https://cloud.example.com/google/oauth/callback`
- Scope: `https://www.googleapis.com/auth/gmail.send` (minimum e-posta gönderme yetkisi) + `openid email`.

Public müşteri dağıtımında Google'ın OAuth verification gereksinimleri ayrıca tamamlanmalıdır.

## Doğrulama durumu

- Cloud `npm test`: **20/20 PASS**.
- Cloud `npm run check`: PASS.
- QR encoder gerçek decode testi: PASS (OpenCV ile üretilen URL tekrar okundu).
- XML / JSON parse kontrolleri: PASS.
- Değiştirilen ana C# dosyalarında delimiter/braces statik kontrolü: PASS.
- Bu çalışma ortamında `.NET SDK` bulunmadığı için gerçek Windows C# build yapılmamıştır.
- Mevcut Desktop regression hedefi değişmedi: Windows'ta `7-SICHERHEITSTESTS.bat` çalıştırıldığında **ALL 240 CHECKS PASSED** beklenir.
- Sonra `1-SETUP-ERSTELLEN.bat` ile gerçek build/setup alınmalıdır.

## Üretim statüsü

Fiskal/DSFinV-K/TSE üretim kilitleri R62 tarafından değiştirilmez. Paket yine mevcut TESTBETRIEB güvenlik sınırlarını korur.
