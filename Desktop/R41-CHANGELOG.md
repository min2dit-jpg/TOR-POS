# R41 Cloud Sync

- App-eigener Hintergrunddienst mit begrenzten HTTP-Zeitlimits, ohne Redirects und mit expliziter Ereignisbestätigung.
- Persistente SQLite-Outbox; Fehler/Neustart behalten dieselben IDs. Backoff bis 5 Minuten.
- Neue echte Verkäufe bei eingerichteter Cloud: Outbox-Eintrag in derselben Transaktion wie Verkauf/Bestand/Checkout-Abschluss. Fiskalgate unverändert.
- Ziele bleiben je Ereignis fest gebunden; Zielwechsel bei wartenden Fremdzieldaten gesperrt.
- Verbindung separat speichern/testen. Token per Windows-DPAPI CurrentUser, nicht als Klartext im Log.
- Automatische Kassenmeldung; manueller vollständiger Artikel-/Bestandssnapshot bis 5000 aktive Artikel.
- Keine automatische Übernahme alter Verkäufe und keine Testbons als Echtumsatz.
- Pause stoppt Übertragung, verwirft keine Ereignisse. Shutdown wartet begrenzt auf den Dienst.
- SumUp, TSE, Bon-Druck und ursprüngliche Checkout-Schutzmaßnahmen unverändert.

Windows-DPAPI und reale Drucker/Terminal-Hardware müssen auf dem Windows-Testgerät bestätigt werden.
