# R43 – Geparkte Bons / Varianten / Bonlogo

Basis: **R42 KIOSK Waren / Bestand / Berichte**. SumUp, ZVT-Zahlungslogik, TSE-Transaktionslogik und die produktive Fiskalsperre wurden nicht verändert.

## 1. Geparkte Bons – hängenbleibende Positionen behoben
- Ein zurückgeholter geparkter Bon wird nicht mehr als alte unsichtbare Kopie in der Liste behalten, wenn alle Positionen entfernt wurden.
- Wird die letzte Position über **SOFORT STORNO** oder **-1** entfernt, setzt TOR den Parkvorgang auf `CANCELLED` und entfernt ihn sofort aus **GEPARKTE BONS**.
- **C** auf einem zurückgeholten Parkbon leert ihn und beendet den offenen Parkvorgang ebenfalls.
- In der Liste **GEPARKTE BONS** gibt es zusätzlich **BON LÖSCHEN**. Technisch wird der Datensatz nicht hart aus der Datenbank gelöscht, sondern auf `CANCELLED` gesetzt; die Aktion wird im Audit-Log protokolliert.
- Offene Z-Sperre zählt weiterhin ausschließlich `status='OPEN'`. Ein stornierter Parkbon blockiert den Z-Abschluss daher nicht mehr.

## 2. Varianten direkt im Artikel
- Der separate Tab **VARIANTEN / GRÖSSEN** wurde entfernt.
- Varianten werden direkt im Tab **ARTIKEL** gepflegt: `+ VARIANTE`, `BEARBEITEN`, `LÖSCHEN`.
- Beim Speichern des Artikels werden Artikelstammdaten, Bestand und Varianten gemeinsam gespeichert.
- Bestehende Varianten eines ausgewählten Artikels werden weiterhin geladen.
- IMBISS behält **EXTRAS** als eigenen Bereich; Varianten bleiben trotzdem direkt beim Artikel.

## 3. Kundenlogo auf dem Bon
- Unter **Einstellungen → Firma & Bon → Bonlogo** gibt es nur noch die praktischen Aktionen **BONLOGO AUSWÄHLEN** und **LOGO ENTFERNEN**.
- Unterstützt: PNG, JPG/JPEG, BMP; maximal 5 MB.
- TOR kopiert die gewählte Datei nach `%APPDATA%\TOR-POS-Pro\ReceiptAssets`, deshalb muss der Kunde den ursprünglichen Bildpfad später nicht behalten.
- Das Logo wird proportional skaliert und zentriert oben auf normalen Bons und Testbons ausgegeben.
- Eine fehlende oder beschädigte Logodatei darf den Bondruck nicht blockieren; der Bon wird dann ohne Logo weitergedruckt.

## Prüfung
- Cloud-Regression: Node-Testlauf **9/9 bestanden**.
- XAML/CSProj und JSON strukturell geprüft.
- 49 C#-Quelldateien auf Klammer-/Stringstruktur geprüft.
- R43-Spezialprüfungen: Park-Cancel-Pfad, Varianten-Integration und Bonlogo-Pipeline vorhanden.
- In dieser Entwicklungsumgebung ist weiterhin kein .NET-10-SDK installiert. Der echte Windows/Avalonia-Build muss auf dem TOR-Test-PC mit `1-SETUP-ERSTELLEN.bat` erfolgen.
