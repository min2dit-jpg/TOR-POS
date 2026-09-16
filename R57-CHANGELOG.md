# TOR POS R57 · ORDER Direktverkauf, Bestellmonitor, tägliche Sicherung

Version: **0.7.33.570**  
Revision: **R57-OrderDisplay-DailyBackup**

## Neu
- ORDER-Modus blockiert BAR/KARTE nicht mehr. Der aktuelle Warenkorb kann als normaler Direktverkauf kassiert werden.
- BESTELLUNG ANNEHMEN bleibt eine separate Aktion und speichert den Warenkorb als offene Bestellung.
- Separater Bestellmonitor, unabhängig von der normalen Kundenanzeige.
- Bestellmonitor: ANGENOMMEN / IN VORBEREITUNG = WIRD VORBEREITET; ABHOLBEREIT = grün FERTIG; AUSGEGEBEN verschwindet.
- Bestellmonitor kann in Einstellungen aktiviert und einem Bildschirm 1–4 oder automatisch dem zweiten Bildschirm zugeordnet werden.
- Tägliche automatische Datenbanksicherung, standardmäßig 00:00.
- Verpasste Sicherung wird beim nächsten Programmstart nachgeholt.
- Nach erfolgreicher Tages-Sicherung wird am selben lokalen Kalendertag keine zweite geplante Sicherung erstellt.
- Bei nicht erreichbarem Sicherungslaufwerk: kein falscher Erfolgsmarker; neuer Versuch nach fünf Minuten.
- Einstellungen zeigen letzte erfolgreiche Sicherung, letzte Sicherungsdatei und letzten Fehler.
- Die tägliche Sicherung ist technisch getrennt von Z-Bericht, Tagesabschluss und Kassenabschluss.

## Prüfung
- Zu den bisherigen 203 Prüfungen wurden 19 R57-Prüfungen ergänzt; vollständiger Testlauf würde damit 222 Prüfungen umfassen.
- In der aktuellen Chat-Ausführungsumgebung ist kein `dotnet`/MSBuild vorhanden. Deshalb wurde dieser neu erzeugte R57-Stand hier **nicht** als erfolgreich kompiliert oder als „222 passed“ markiert.
- Vor Kundeneinsatz: Windows/.NET-Build, Avalonia-XAML-Compile, zweiter Monitor, Touch, Drucker und Backup-Ziel real testen.
- Fiskal-/TSE-Produktivfreigabe bleibt unverändert gesperrt.

## FIX1 · Avalonia 12.1.2 Compile-Hotfix
- `OrderCustomerDisplayWindow`: veraltete/falsch qualifizierte `SystemDecorations`-Zuweisung entfernt.
- Verwendet nun wie das bestehende Startfenster `WindowDecorations = Avalonia.Controls.WindowDecorations.None`.
- Behebt den Windows-Buildfehler `CS0176` in `OrderCustomerDisplayWindow.cs`.
