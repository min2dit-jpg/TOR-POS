# R45 verification

Date: 2026-09-08

## Automated Cloud checks
- `node --check server.js` PASS
- `node --check public/app.js` PASS
- `npm test` PASS: 11/11
- Included regression areas: login/tenant isolation, duplicate sale idempotency, batch rollback, stock snapshots, Berlin calendar/DST, receipt pagination, origin/rate-limit protection.
- New R45 checks: update manifest/download contract; TOTP enrollment → challenge login.

## Desktop static checks
- Avalonia XAML / project XML parsed successfully.
- Desktop and Cloud JSON parsed successfully.
- MainWindow handler scan includes partial class handlers (`MainWindow.Safety.cs`).
- New updater / license-warning source delimiters are balanced by static check.
- SumUp source hashes match R43 exactly.

## Not verified in this environment
- .NET 10 / Avalonia Windows compile: SDK unavailable in this environment.
- Real Inno Setup update install on Windows.
- Authenticode verification with TOR production signing certificate.
- Physical printer/TSE/payment hardware.

Run `Desktop\1-SETUP-ERSTELLEN.bat` on the Windows build PC before acceptance.


> Güvenlik notu: Canlı/uzak otomatik güncelleme, TOR/Demirkaan GmbH code-signing sertifikasının thumbprint değeri `TorRelease.UpdateSignerThumbprint` içine sabitlenmeden bilinçli olarak engellenir. Manifest içindeki thumbprint tek başına güven kaynağı değildir. Localhost geliştirme testi bu kilitten muaftır.
