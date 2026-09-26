# TOR Restaurant Lasttest · 25.09.2026 23:59

Geräte: 20 · Tische: 100 · Dauer: 60 s · Küchen-Index (nur Testdatenbank): ja · Rechner: vm · 4 CPU · Ubuntu 24.04.4 LTS

Gemessen wird die Service-/Datenbankschicht (IoQueue, SQLite WAL) unter Restaurant-Last - ohne HTTP/TLS und ohne TSE (siehe Kopf von Program.cs).

| Messung | Anzahl | p50 ms | p95 ms | p99 ms | max ms |
|---|---:|---:|---:|---:|---:|
| Kellner: Position hinzufügen | 5311 | 0.8 | 1.6 | 4.5 | 19.5 |
| Kellner: Wiederholung (verlorene Antwort) | 537 | 0.1 | 0.2 | 0.8 | 6.1 |
| Tischplan abrufen | 600 | 2.5 | 4.7 | 15.8 | 18.1 |
| KDS-Board | 117 | 16.0 | 34.1 | 43.8 | 47.0 |
| Kasse: Checkout-Vorbereitung | 60 | 2.1 | 4.9 | 59.1 | 59.1 |
| Kasse: IoQueue-Wartezeit | 599 | 0.1 | 0.3 | 0.4 | 17.7 |

Versionskonflikte (erwartbar bei gleichzeitigen Änderungen): 0 · Fehler: 0 · Tischpositionen: 5311 · Küchenaufträge: 5311

| Prüfung | Ergebnis |
|---|---|
| Kasse Checkout-Vorbereitung p95 < 1000 ms | BESTANDEN |
| Kasse Checkout-Vorbereitung p99 < 2000 ms | BESTANDEN |
| IoQueue-Wartezeit der Kasse p99 < 500 ms | BESTANDEN |
| KDS-Board p95 < 1000 ms | BESTANDEN |
| keine doppelte Tischposition bei wiederholtem Befehl | BESTANDEN |
| kein doppelter Küchenauftrag bei wiederholtem Befehl | BESTANDEN |
| keine unerwarteten Fehler | BESTANDEN |
| Last wurde tatsächlich erzeugt | BESTANDEN |
