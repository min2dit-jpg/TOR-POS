# R45 – TOR POS Cloud 2FA

Basis: R44 Update / Lizenz-Sicherheit.

- Cloud portal OWNER hesabı için TOTP tabanlı iki faktörlü giriş eklendi.
- Login artık 2FA aktif kullanıcıda 5 dakikalık challenge üretir; en fazla 5 kod denemesi kabul edilir.
- Authenticator setup akışı: secret → 6 haneli doğrulama → 8 recovery code.
- Recovery code yalnız bir defa kullanılabilir ve veritabanında SHA-256 hash olarak tutulur.
- TOTP secret veritabanında düz metin tutulmaz; AES-256-GCM ile `TOR_CLOUD_TOTP_KEY` üzerinden korunur.
- Produktif Cloud'da OWNER için 2FA varsayılan olarak zorunludur; demo modunda isteğe bağlıdır.
- Portalda yeni `Sicherheit` bölümü eklendi.
- Cloud update endpointleri ve kontrollü Setup download alanı eklendi.
- 11 Cloud otomatik testi geçti: mevcut tenant/sync testlerine update ve TOTP testleri eklendi.
- Yeni üçüncü taraf paket eklenmedi; Node built-in crypto kullanılır.
