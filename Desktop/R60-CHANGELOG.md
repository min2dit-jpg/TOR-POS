# TOR POS R60 · Gmail SMTP Hotfix

Version: **0.7.33.600**

## Problem

Beim Gmail-Testversand konnte trotz `smtp.gmail.com`, Port 587, TLS und Google App-Passwort die Meldung `5.7.0 Authentication Required` erscheinen.

## Änderungen

- SMTP-Authentifizierung wird explizit in sicherer Reihenfolge aufgebaut:
  1. `DeliveryMethod = Network`
  2. `UseDefaultCredentials = false`
  3. `Credentials = NetworkCredential(SMTP-Benutzer, App-Passwort)`
  4. `EnableSsl = true` für Gmail/587 (STARTTLS)
- Google App-Passwörter werden vor der Anmeldung normalisiert; Leerzeichen, Tabs und Zeilenumbrüche werden entfernt.
- Maskierte Werte wie `****`, `••••` oder `●●●●` werden niemals als Passwort an SMTP gesendet.
- Das gespeicherte App-Passwort wird nicht mehr entschlüsselt in das sichtbare Einstellungsfeld geladen.
- Bleibt das Passwortfeld leer, wird ein vorhandenes DPAPI-geschütztes Passwort beibehalten und für den Test sicher entschlüsselt.
- Kann das gespeicherte Passwort mit dem aktuellen Windows-Benutzer nicht entschlüsselt werden, fordert TOR POS zur Neueingabe auf statt still ein leeres Passwort zu verwenden.
- Gmail-Prüfung erzwingt für `smtp.gmail.com` Port **587** und **TLS/STARTTLS**.
- Neuer Button **GMAIL-STANDARD ÜBERNEHMEN** setzt `smtp.gmail.com / 587 / TLS`.
- Neues mehrzeiliges **SMTP-Diagnose**-Feld zeigt StatusCode und sichere technische Hinweise; App-Passwort wird nie angezeigt.
- Gmail-Authentifizierungsfehler 5.7.0/5.7.8 erhalten eine gezielte Erklärung.

## Sicherheit

- App-Passwort bleibt mit Windows `ProtectedData` / CurrentUser geschützt gespeichert.
- App-Passwort wird weder Audit-Log noch Diagnosefeld hinzugefügt.
- Monatsberichte, PDF-Export, Druckerprüfung und bestehende R59-Funktionen bleiben unverändert.

## Testziel

R59 hatte 234 Kontrollen. R60 ergänzt 6 SMTP/Gmail-Kontrollen. Erwartet unter Windows:

`ALL 240 CHECKS PASSED`
