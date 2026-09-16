# Versionskontrolle und CI — Stand

**Stand: 16.09.2026 (R127)**

## Aufbau

Privates GitHub-Repository: <https://github.com/min2dit-jpg/TOR-POS>

**Das Repository ist der gesamte Projektordner** — genau so, wie er auf dem PC
liegt:

| Ordner | Inhalt |
|---|---|
| `Desktop/` | TOR POS Pro (Kasse, Tests, Werkzeuge) |
| `Cloud/` | TOR POS Cloud (Server, Portal, Kundeneinrichtung, Deploy-Vorlagen) |
| `Dokumentation/`, `verification/` | Unterlagen und frühere Prüfprotokolle |
| Stammordner | Gesamtaudit, ältere Changelogs R54–R75.1, `10-ALLE-TESTS.bat` |

Bis R126 enthielt das Repository nur `Desktop/`, und die Cloud lag kurzzeitig
auf einem eigenen Branch `cloud`. Beides ist jetzt in `main` zusammengeführt;
die Git-Historie der Desktop-Dateien bleibt als Umbenennung erhalten.

## CI

`.github/workflows/tor-pos-ci.yml` läuft bei jedem Push auf `main`:

- **Windows:** Build, Sicherheitstests (`ALL … CHECKS PASSED`) und die
  Bildschirm-Layoutprüfung in fünf Größen (Screenshots als Artefakt)
- **Linux:** Cloud-Syntaxprüfung und Cloud-Tests

Ergebnis: GitHub → Reiter **Actions**.

## Arbeitsweise

- Lokal vor jeder Auslieferung: `10-ALLE-TESTS.bat` im Stammordner.
- Build-Ausgaben (`Desktop/publish`, `Desktop/installer-output`), Cloud-Daten
  (`Cloud/data`) und Geheimnisse (`.env`, Zertifikate, private Lizenzschlüssel)
  sind ausgeschlossen (`.gitignore` im Stamm, in `Desktop/` und in `Cloud/`).
- Installer für Kunden nicht über das Repository verteilen, sondern über
  GitHub **Releases**.
- Die erwartete Prüfungszahl steht in
  `Desktop/tests/TorPos.SafetyTests/Program.cs` (`ExpectedSafetyChecks`) und
  wird bewusst angehoben, nie stillschweigend angepasst.
