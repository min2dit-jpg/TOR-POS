# R31 – Drucker-Stabilität + automatisches Fehlerprotokoll

## Änderungen
- Star mC-Print3 Druckaufträge erhalten einen 15-Sekunden-Watchdog.
- Hängt der Windows-Spooler länger, bleibt die Kassenoberfläche frei.
- Nach einem Druck-Timeout wird der Druckerzustand als unklar behandelt; weitere Druckaufträge werden bis zum Neustart blockiert. Dadurch wird ein Bon nicht blind doppelt gedruckt.
- Ein bereits gespeicherter Verkauf bleibt gespeichert, auch wenn der Bondruck fehlschlägt.
- Die Hauptkasse zeigt in diesem Fall ausdrücklich: NICHT ERNEUT KASSIEREN.
- Kontrollierte Betriebsfehler (u.a. ZVT, Verkauf/DB, Parken, Z-Bericht) erhalten eine eindeutige Fehler-ID.
- Wenn ein Bondrucker aktiviert und ausgewählt ist, wird zu diesen Fehlern automatisch ein kurzer `TOR POS FEHLERPROTOKOLL`-Zettel gedruckt.
- Druckerfehler drucken absichtlich keinen weiteren Fehlerzettel. Damit entsteht keine Fehler-/Druckschleife.
- Das Fehlerprotokoll enthält Datum/Uhrzeit, Kassennummer, Bediener, Kategorie, Kurztext und Fehler-ID.
- Technische Details bleiben im R30-Log unter `%LOCALAPPDATA%\TOR POS Pro\Logs`.

## Nicht verändert
- Kein Eingriff in TSE Start/FinishTransaction.
- Kein Eingriff in DSFinV-K.
- Keine Änderung an Bonnummern, Steuern oder Verkaufsspeicherung.
- Kein automatisches Wiederholen eines unklaren Druckauftrags.

## Test
1. Normalen Testbon drucken.
2. Normalen Verkauf durchführen.
3. Drucker ausschalten/unerreichbar machen und einen Druckversuch auslösen.
4. TOR POS darf nicht dauerhaft einfrieren.
5. Bei einem kontrollierten Nicht-Druckerfehler soll – sofern Drucker verfügbar – ein Fehlerprotokoll mit Fehler-ID erscheinen.
