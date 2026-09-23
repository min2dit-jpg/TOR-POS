# TSE-Startprüfung – Betreiberbericht vom 23.09.2026

Dies ist ein **Bericht**, kein durchlaufenes Protokoll. Das Protokoll ist
`verification/HARDWARE-E2E-TEMPLATE.md`; dort ist keine Zeile gekreuzt, kein
Tester unterschrieben, und das Gesamtergebnis bleibt offen. Was hier steht, ist
festgehalten, damit es nicht verloren geht.

## Prüfstand

| | |
|---|---|
| Datum | 23.09.2026 |
| Produkte | TOR Einzelhandel und TOR Gastro |
| Stand | CI-Run 662, Commit `c13d971` |
| TSE | **nicht angeschlossen**; kein Swissbit SDK installiert |
| Belege | Fotos des Betreibers |

## Gemeldetes Teilergebnis

Der Betreiber hat auf einem echten Windows-PC **ohne angeschlossene TSE** beide
Produkte gestartet und gemeldet, dass das Warnfenster in **TOR Einzelhandel und
TOR Gastro** erschienen ist. Geprüft wurde der Stand aus CI-Run 662
(Commit `c13d971`).

Weiter gemeldet und per Foto belegt (TOR Einzelhandel, Admin-Sitzung):

- das Fenster benennt den Zustand korrekt mit **„Swissbit SDK nicht gefunden“**
  und gibt die Gerätemeldung „WormAPI.dll nicht gefunden“ darunter aus
- nach dem Hinweis ist **Anmeldung und Verkauf möglich**
- der Hinweis erscheint **nur einmal** pro Programmlauf
- die Schaltfläche **TSE-EINSTELLUNGEN ÖFFNEN** ist in der Admin-Sitzung vorhanden
- in der **Kassenkraft-Sitzung** fehlt diese Schaltfläche; stattdessen steht dort
  „Bitte die Betreiberin oder den Betreiber informieren. Der Verkauf kann
  weiterlaufen.“ – es bleibt die einzelne Schaltfläche **WEITER OHNE TSE**
- **nach dem Schließen** des Fensters bleibt das Badge **TSE-AUSFALL** in der
  Kopfzeile stehen, und die Statuszeile trägt den vollen Satz
  „Swissbit SDK nicht gefunden · Kasse bleibt bedienbar · Vorgänge werden nicht
  signiert“

Wichtig für die weitere Abnahme: der gemeldete Zustand ist **SdkMissing**, nicht
NotFound. Ohne die offizielle `WormAPI.dll` bricht die Prüfung ab, **bevor** nach
Hardware gesucht wird. Die Zeile „nach Anstecken einer betriebsbereiten TSE kein
Warnfenster mehr“ kann deshalb erst geprüft werden, wenn das Swissbit SDK
eingerichtet ist; bis dahin bleibt der Hinweis unabhängig von der angesteckten
Hardware bestehen.

Damit sind die entsprechenden Zeilen der Tabelle als gemeldet zu lesen. Sie ist hier
festgehalten, damit sie nicht verloren geht, und ersetzt das Protokoll **nicht**:
keine Zeile ist gekreuzt, kein Tester ist unterschrieben, und das Gesamtergebnis
bleibt offen. Offen sind damit nur noch drei Zeilen: das Badge-Tooltip mit
Beginn und Grund (braucht eine Maus, am Touchscreen schwer zu prüfen), der
Eintrag in `tse_outage_log` (nachweisbar über den DSFinV-K-Export) und das
Verschwinden des Hinweises nach dem Anstecken einer betriebsbereiten TSE - das
letzte ist bis zur Einrichtung des Swissbit SDK ohnehin nicht prüfbar.


## Was daraus folgt

Sechs der neun Zeilen der Startprüfung sind damit als **gemeldet** zu lesen.
Offen bleiben drei:

| Offene Zeile | Warum |
|---|---|
| Badge-Tooltip mit Beginn und Grund | braucht eine Maus, am Touchscreen schwer zu prüfen |
| Eintrag in `tse_outage_log` | nachweisbar über den DSFinV-K-Export |
| Kein Warnfenster nach Anstecken einer betriebsbereiten TSE | erst prüfbar, wenn das Swissbit SDK eingerichtet ist |

Die letzte Zeile ist ohne `WormAPI.dll` grundsätzlich nicht prüfbar: der
gemeldete Zustand war durchgehend `SdkMissing`, nicht `NotFound`, weil die
Prüfung abbricht, bevor überhaupt nach Hardware gesucht wird.
