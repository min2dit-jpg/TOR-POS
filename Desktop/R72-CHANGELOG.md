# TOR POS R72 – Authentication / KDF Hardening + Angebot 40/50

## Angebot
Unterstützt jetzt: 10 / 15 / 20 / 25 / 30 / 40 / 50 %.
Schema V4 baut den R71-Constraint transaction-sicher neu auf und übernimmt bestehende Kampagnen-IDs/History.

## KDF
Legacy: PBKDF2-HMAC-SHA256 / 180.000.
Neu: PBKDF2-HMAC-SHA256 / 600.000.
Pro Passwort und PIN werden Algorithmus + Iterationen gespeichert.
Nach erfolgreichem Login wird nur das verwendete Legacy-Secret mit neuem Salt auf 600.000 rehashed.
Fehlgeschlagene Logins ändern den Hash nicht. Unbekannte KDF-Metadaten sind fail-closed.

Diagnose: AUTH-KDF = AKTUELL / UPGRADE BEI LOGIN / KONFIGURATION FEHLER.

Schema: V4.
R72 neue Safety Checks: 15.
Windows-Ziel: ALL 298 CHECKS PASSED.

Hinweis: 4-stellige PIN bleibt ein Komfort-Zugang mit niedriger Entropie. Bestehender Online-Lockout bleibt aktiv; kritische Admin-Funktionen bleiben passwort-/berechtigungsgebunden.
