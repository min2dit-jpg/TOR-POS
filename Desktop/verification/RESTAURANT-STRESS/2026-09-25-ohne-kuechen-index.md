# TOR Restaurant Lasttest · 25.09.2026 23:58

Geräte: 20 · Tische: 100 · Dauer: 60 s · Küchen-Index (nur Testdatenbank): nein · Rechner: vm · 4 CPU · Ubuntu 24.04.4 LTS

Gemessen wird die Service-/Datenbankschicht (IoQueue, SQLite WAL) unter Restaurant-Last - ohne HTTP/TLS und ohne TSE (siehe Kopf von Program.cs).

| Messung | Anzahl | p50 ms | p95 ms | p99 ms | max ms |
|---|---:|---:|---:|---:|---:|
| Kellner: Position hinzufügen | 5309 | 0.8 | 1.6 | 4.6 | 25.8 |
| Kellner: Wiederholung (verlorene Antwort) | 537 | 0.1 | 0.2 | 0.6 | 6.4 |
| Tischplan abrufen | 600 | 2.2 | 5.1 | 17.6 | 147.3 |
| KDS-Board | 69 | 144.7 | 2786.0 | 4250.5 | 4250.5 |
| Kasse: Checkout-Vorbereitung | 60 | 2.2 | 4.8 | 68.4 | 68.4 |
| Kasse: IoQueue-Wartezeit | 599 | 0.1 | 0.3 | 0.5 | 15.5 |

Versionskonflikte (erwartbar bei gleichzeitigen Änderungen): 0 · Fehler: 0 · Tischpositionen: 5309 · Küchenaufträge: 5309

| Prüfung | Ergebnis |
|---|---|
| Kasse Checkout-Vorbereitung p95 < 1000 ms | BESTANDEN |
| Kasse Checkout-Vorbereitung p99 < 2000 ms | BESTANDEN |
| IoQueue-Wartezeit der Kasse p99 < 500 ms | BESTANDEN |
| KDS-Board p95 < 1000 ms | NICHT BESTANDEN |
| keine doppelte Tischposition bei wiederholtem Befehl | BESTANDEN |
| kein doppelter Küchenauftrag bei wiederholtem Befehl | BESTANDEN |
| keine unerwarteten Fehler | BESTANDEN |
| Last wurde tatsächlich erzeugt | BESTANDEN |
