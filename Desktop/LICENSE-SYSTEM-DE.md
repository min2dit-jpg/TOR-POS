# TOR POS Pro – kommerzielles Lizenzsystem

Stand: 05.09.2026 · Version 0.7.19

## Sicherheitsmodell

TOR POS erzeugt pro Installation eine zufällige Installations-ID und zusätzlich
einen PC-Gerätecode. Dieser Gerätecode wird mit SHA-256 aus Installations-ID,
Windows MachineGuid und Serienkennung des Windows-Systemlaufwerks gebildet. Die
Rohwerte werden weder angezeigt noch exportiert.

Eine Lizenz enthält Kunden-Nr., Kundenname, Installations-ID, PC-Gerätecode,
Edition (`KIOSK`/`IMBISS`), Lizenz-ID, Gültigkeitsende und Funktionsmerkmale.
Der Hersteller signiert den exakten
JSON-Nutzinhalt mit RSA-3072/SHA-256. Die Anwendung enthält nur den öffentlichen
Schlüssel und lehnt veränderte, abgelaufene, fremde, PC-falsche oder
editionsfalsche Dateien ab.

Die Lizenzdatei ist damit für genau den PC-Gerätecode gültig, für den sie
ausgestellt wurde. Ein Hardwaretausch, Neuaufsetzen von Windows oder Verlust des
TOR-Datenordners kann eine Neuausstellung erforderlich machen.

## Kundenablauf

1. `Einstellungen → Lizenzierung` öffnen.
2. Die vom Händler erhaltene Kunden-Nr. und den Kundennamen eintragen.
3. `AKTIVIERUNGSANFRAGE ERSTELLEN` wählen. Die Anfrage enthält Kunden-Nr.,
   Installations-ID, PC-Gerätecode und Edition.
4. Anfrage an den Hersteller übermitteln.
5. Vom Hersteller signierte JSON-Lizenz mit `LIZENZDATEI IMPORTIEREN` einlesen.
6. Status, Kunden-Nr., PC-Gerätecode, Edition und Ablaufdatum prüfen.

## Herstellerablauf

Der Hersteller verwendet ausschließlich das Tool unter `dealer-tools`. Der separat
übergebene private Schlüssel ist ein Entwicklungsstartschlüssel und muss vor echtem
Vertrieb durch einen kontrolliert erzeugten Produktionsschlüssel ersetzt werden.
Er gehört in ein Secret-/Key-Management und niemals in Quellpakete, Backups für
Kunden, Tickets oder Setup-Dateien.

## Edition-Regel in 0.7.19

- Für `KIOSK` und `IMBISS` werden getrennte Lizenzdateien ausgestellt.
- Die Edition ist unveränderlicher Bestandteil der signierten Lizenz.
- Eine KIOSK-Lizenz wird in IMBISS abgelehnt; eine IMBISS-Lizenz wird in KIOSK abgelehnt.
- Ohne passende aktive Edition-Lizenz sind BAR und KARTE gesperrt.
- Ein Update ändert die bereits installierte Edition nicht.

## Grenzen in 0.7.19

- Keine Online-Sperrliste/Revocation.
- Keine Cloud-Aktivierung und keine Übertragung von Kundendaten.
- Kein TPM-gebundener privater Geräteschlüssel.
- Eine Offline-Lizenz verhindert die Verwendung derselben Lizenzdatei auf einem
  anderen PC. Dass eine Kunden-Nr. nicht versehentlich für mehrere PC-Lizenzen
  ausgestellt wird, muss der Händler dokumentieren. Eine zentrale Echtzeit-
  Sperre erfordert später einen Aktivierungsserver.
- Fehlende, abgelaufene, PC-falsche oder editionsfalsche Lizenz sperrt den Kassiervorgang.
- Eine Softwarelizenz ist kein Nachweis der KassenSichV-/TSE-/DSFinV-K-Konformität.

Vor Produktivvertrieb sind Schlüsselwechsel, Recovery, Sperrung, Datenschutz,
Supportprozess und eine manipulationsresistentere Aktivierungsrichtlinie abzunehmen.


## Lizenz-Deaktivierung (ab v0.7.25)

Unter **Einstellungen → Lizenzierung** steht die Funktion **LIZENZ DEAKTIVIEREN** zur Verfügung.

Ablauf:
- nur eine aktuell aktive Lizenz kann deaktiviert werden;
- vor der Deaktivierung erscheint eine eindeutige Sicherheitsabfrage;
- die Lizenz-ID wird lokal in einem Deaktivierungsjournal gespeichert;
- dieselbe Lizenz-ID kann auf diesem PC anschließend nicht erneut importiert/aktiviert werden;
- auf dem Desktop wird ein `TOR-Lizenz-Deaktivierung-....json` Beleg erzeugt;
- der Vorgang wird zusätzlich im TOR-POS Audit-Log dokumentiert;
- für eine spätere Wiederaktivierung muss der Händler eine **neue Lizenz mit neuer Lizenz-ID** ausstellen.

Hinweis: Diese Funktion ist eine lokale Offline-Deaktivierung. Eine zentrale, serverweite Sperr-/Freigabeliste wird erst mit dem TOR POS Remote/License Server umgesetzt.
