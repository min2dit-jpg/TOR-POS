# R46 – Software & Update / automatischer Cloud-Bestand

Basis: **R45 Update / Lizenz / Cloud-2FA**. SumUp, ZVT-Zahlungslogik, TSE-Transaktionslogik und produktive Fiskalsperre wurden nicht verändert.

## 1. Software & Update für Betreiber
- In `Einstellungen` gibt es einen eigenen Hauptpunkt **Software & Update**.
- Betreiber/Admin sieht dort installierte Version, Revision, Edition, Lizenzstatus und Lizenzlaufzeit ohne Techniker-Passwort.
- Automatische Update-Prüfung kann dort ein-/ausgeschaltet und gespeichert werden.
- **JETZT NACH UPDATE SUCHEN** prüft sofort den TOR-Update-Server.
- Wird ein Update gefunden, erklärt die Seite den nächsten Schritt: zurück zur Hauptkasse; dort erscheint für Admin der vorhandene **UPDATE**-Button.
- Die technische **Update-Server-URL** bleibt bewusst unter `Erweitert / Techniker → System` und ist damit vom normalen Betreiberweg getrennt.
- Bestehende Sicherheitsregeln bleiben: kein Installieren während offenem Verkauf/ungeklärter Zahlung; Download-Hash, produktiv Signatur/Thumbprint und Backup vor Installation.

## 2. Cloud-Bestand automatisch aktuell
- Artikel-/Bestandsänderungen in **Artikelverwaltung**, **Inventur**, **Warengruppe** oder **Gruppe** markieren den Cloud-Bestand automatisch als geändert.
- Der Hintergrunddienst erstellt daraus einen vollständigen Snapshot, ohne den Kassen-UI-Thread oder Kassieren auf Internet warten zu lassen.
- Mehrere noch nicht gesendete `stock.snapshot`-Ereignisse desselben Geräts werden zu dem neuesten Snapshot zusammengefasst; Umsatzereignisse bleiben davon unberührt und FIFO.
- Beim Start nach kurzer Verzögerung und danach stündlich wird ein Reparatur-Snapshot erzeugt. Dadurch kann z. B. ein harter Stromausfall eine verpasste Produktänderung später selbst heilen.
- Manuell bleibt unter `Einstellungen → Geräte → TOR POS Cloud` **JETZT VOLLSTÄNDIG ABGLEICHEN** erhalten.

## 3. Verkauf aktualisiert Cloud-Bestand ohne Vollsnapshot
- `sale.completed` bleibt das unveränderbare/idempotente Umsatzereignis aus derselben lokalen Verkaufstransaktion.
- TOR Cloud reduziert bei einem akzeptierten Verkauf den Bestand der betroffenen `product_key` direkt um die Verkaufsmenge.
- Ein identisches wiederholtes Sale-Event wird als `duplicate` erkannt und reduziert den Bestand **nicht** ein zweites Mal.
- Kommt ein verspäteter Verkauf erst nach einem neueren vollständigen Bestandssnapshot an, wird der Bestand nicht nochmals abgezogen. Der neuere Snapshot gewinnt.
- Damit bleibt Umsatz praktisch sofort sichtbar, während große Artikelbestände nicht nach jedem einzelnen Kassiervorgang komplett neu übertragen werden müssen.

## Prüfung
- TOR Cloud Node-Syntaxprüfung: bestanden.
- Cloud Regression: **13/13 Tests bestanden**, inklusive Sale→Bestandsabzug, Duplicate-Schutz, verspätetes Sale vs. neuer Snapshot, 2FA, Update-API, Tenant-Trennung und bestehender R42-Artikeldaten.
- Desktop XAML/CSProj und JSON strukturell geprüft.
- 54 C#-Dateien tokenbasiert auf Klammerstruktur geprüft; keine strukturellen Auffälligkeiten.
- SumUp-Dateien SHA-256-identisch zu R45.
- In dieser Entwicklungsumgebung ist kein .NET-10-SDK installiert. Echter Windows/Avalonia-Build weiterhin auf dem TOR-Test-PC mit `Desktop\1-SETUP-ERSTELLEN.bat`.
