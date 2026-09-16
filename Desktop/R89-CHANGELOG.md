# TOR POS R89 – Drucker-Statusprüfung vor jedem einzelnen Druckauftrag

## Kontext
`TryReadWindowsPrinterStatus`/`PrinterStatusProblem` (Windows-Spooler-
Statusabfrage: offline, Papier leer, Papierstau, Abdeckung offen, ...)
existierten bereits seit R58, wurden aber nur einmalig in `ProbeAsync`
aufgerufen - also beim manuellen "Drucker testen" in den Einstellungen
oder beim Geräte-Manager-Check. Zwischen diesem Probe und dem nächsten
tatsächlichen Bondruck (Minuten oder Stunden später) konnte der Drucker
längst offline gegangen oder das Papier ausgegangen sein, ohne dass TOR
das vorher wusste - der erste Hinweis wäre dann eine wenig aussagekräftige
Treiber-Exception oder ein stiller Spooler-Hänger gewesen. Teil der
Bewertung des Roadmap-Punkts "Paper-out monitoring".

## Änderung
`StarMcPrint3PrinterService.ProcessQueueAsync` ruft dieselbe, bereits
getestete `TryReadWindowsPrinterStatus`/`PrinterStatusProblem`-Prüfung
jetzt direkt vor JEDEM Druckauftrag auf (Bon, Fehlerzettel, Bericht,
Küchenbon, Abholschein) - nicht nur einmalig beim Probe. Liefert Windows
ein bekanntes Problem (offline, Papier leer, Papierstau, Abdeckung offen,
angehalten), wird der Auftrag sofort mit der klaren Meldung abgelehnt,
ohne den Spooler/Treiber überhaupt erst anzusprechen.

Bewusst **nicht** über `_spoolerStateUncertain` geführt: dieses Flag ist
für echt unklare Ausgänge reserviert (z. B. ein Timeout, bei dem nicht
bekannt ist, ob doch noch gedruckt wurde) und sperrt die gesamte
Warteschlange bis zu einer manuellen `ResolveQueueAsync`-Prüfung. Ein
"Papier leer" ist dagegen ein eindeutiger, sicherer Zustand - nichts wurde
gesendet. Diesen Fall trotzdem als "unklar" zu behandeln, hätte die
Warteschlange nach jedem simplen Papierwechsel unnötig gesperrt.

## Ergebnis
- 1 neue Prüfung in `R89ReviewTests.cs`: bestätigt die Sicherheitseigenschaft,
  von der die gesamte übrige Drucker-Testsuite bereits stillschweigend
  abhängt - liefert Windows für einen Druckernamen keinen Live-Status
  (Normalfall für jeden in Tests verwendeten synthetischen Namen und für
  reale Treiber ohne Statusunterstützung), muss die neue Prüfung "offen
  fehlschlagen", also den Druck ganz normal zulassen, statt fälschlich zu
  blockieren. Ein echter "Drucker meldet Problem"-Fall lässt sich ohne
  reale, absichtlich gestörte Hardware in dieser Suite nicht auslösen -
  die zugrunde liegende `PrinterStatusProblem`-Zuordnung ist bereits seit
  R58 separat abgedeckt. Sicherheits-Testsuite: **454/454 PASS**.
- `dotnet build -c Release` für `TorPos.App`: 0 Warnung(en), 0 Fehler.

## Einordnung
Reine Drucker-Zuverlässigkeitsverbesserung ohne Bezug zu Fiskalisierung,
TSE oder Zahlungsabwicklung. "Paper-out monitoring" ist damit nur
teilweise geschlossen: dies ist eine Prüfung unmittelbar vor jedem Druck,
kein echtes Live-Push-Monitoring während des Wartens in der
Warteschlange - dafür bräuchte es weiterhin entweder das offizielle
StarIO10-SDK oder eine direkte Port-Anbindung (siehe
`STAR-MCP31CBI-INTEGRATION.md`).
