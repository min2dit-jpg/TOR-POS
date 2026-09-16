# TOR POS Lizenz-Issuer – nur für den Hersteller

Dieser Ordner gehört **nicht** in das Kunden-Setup. Der private Schlüssel wird
separat als Händler-Geheimnis übergeben und ist aus dem normalen Quellpaket
ausgeschlossen. Er darf ausschließlich beim Hersteller in einem geschützten
Secret-/Key-Management aufbewahrt werden.

Der Windows-Installer enthält nur den veröffentlichten App-Ordner und nimmt
`dealer-tools` nicht auf. Verteilen Sie an Kunden ausschließlich die erzeugte
Setup-EXE und die jeweils für Kunden-Nr. und PC-Gerätecode ausgestellte Lizenzdatei.

## Ablauf

1. Kunde öffnet `Einstellungen → Lizenzierung`.
2. Kunde trägt die vom Händler vergebene Kunden-Nr. ein, erstellt die
   Aktivierungsanfrage und sendet die JSON-Datei sicher an
   den Hersteller.
3. Kunden-Nr., Kundenname, Installations-ID, PC-Gerätecode und Edition in der
   Anfrage werden geprüft. Die Werte müssen nicht manuell kopiert werden.
4. Hersteller erstellt die signierte Lizenz:

Einfach: `1-LIZENZ-ERSTELLEN.bat` starten oder die Kundenanfrage auf diese
BAT-Datei ziehen. Alternativ per PowerShell:

```powershell
dotnet run --project .\TorPos.LicenseIssuer\TorPos.LicenseIssuer.csproj -- `
  --request "C:\Kunden\TOR-Aktivierungsanfrage.json" `
  --days "365" `
  --key "D:\TOR-Secrets\TOR-POS-LICENSE-PRIVATE.pem" `
  --output ".\Musterfirma-TOR-Lizenz.json"
```

5. Der Kunde importiert die signierte Datei unter `Einstellungen → Lizenzierung`.

Die erzeugte Lizenz funktioniert ausschließlich mit der in der Anfrage
enthaltenen Kunden-Nr., Installations-ID, Edition und dem PC-Gerätecode.

KIOSK ve IMBISS ayrı ürün lisanslarıdır. Aktivasyon isteği `KIOSK` içeriyorsa
yalnız KIOSK lisansı, `IMBISS` içeriyorsa yalnız IMBISS lisansı üretilir.
Edition alanını elle değiştirerek lisans üretmeyin; müşterinin gönderdiği
aktivasyon isteğini esas alın.

Eine aktive kommerzielle Lizenz hebt die fiskale TESTBETRIEB-Sperre nicht auf.
