# TOR POS Pro – Drittanbieter- und Lizenzhinweise

Stand: 05.09.2026 · Produktstand: 0.7.19

Dieses Verzeichnis ist eine technische Bestandsaufnahme, keine anwaltliche
Lizenzprüfung. Vor jeder Kundenfreigabe müssen außerdem alle transitiven NuGet-
Abhängigkeiten aus dem tatsächlich veröffentlichten Build inventarisiert werden.

## Direkt verwendete Open-Source-Komponenten

| Komponente | Projektversion | Lizenz/Quelle | Vertriebsstatus |
|---|---:|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | 12.1.2 | MIT, <https://github.com/AvaloniaUI/Avalonia/blob/main/licence.md> | Lizenztext und Copyright-Hinweis im Produkt beilegen |
| Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT, .NET Foundation / dotnet/runtime | DI composition root / window factory |
| Microsoft.Data.Sqlite | 10.0.11 | MIT, <https://github.com/dotnet/efcore/blob/main/LICENSE.txt> | Lizenztext und Copyright-Hinweis im Produkt beilegen |
| .NET Runtime / System.Drawing.Common | 10.0.0 | MIT, <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> | Self-contained Runtime-Dateien und Notices des konkreten Builds prüfen/beilegen |
| Portalum.Zvt | 3.4.0 | MIT, <https://github.com/Portalum/Portalum.Zvt/blob/main/LICENSE> | Lizenztext und Copyright-Hinweis im Produkt beilegen |

MIT erlaubt grundsätzlich kommerzielle Nutzung und Weiterverteilung, verlangt
aber die Beibehaltung des Copyright- und Lizenzhinweises. Maßgeblich ist immer
die mit der konkret verwendeten Paketversion gelieferte Lizenzdatei.

## Herstellerkomponenten und Treiber

### Swissbit TSE / WORM API

- `WormAPI.dll` und `WormAPIUni.dll` werden von TOR POS nicht mitgeliefert.
- Vor einer Auslieferung mit diesen Dateien sind schriftlich zu klären:
  SDK-Nutzungsrecht, Kunden-/Runtime-Weiterverteilung, zulässige Version,
  Supportweg und Kompatibilität zur konkret gelieferten zertifizierten TSE.
- Herstellerkontakt: <https://www.swissbit.com/en/support/contact>
- Der private TOR-POS-Lizenzschlüssel ist davon vollständig getrennt.

### Star mC-Print3

- TOR nutzt aktuell den vom Betreiber installierten Windows-Druckertreiber über
  die Windows-Druckschnittstelle; es wird kein StarPRNT-SDK in TOR gebündelt.
- Wird später ein Star-SDK oder Treiber mit dem Setup weiterverteilt, müssen die
  zugehörigen Herstellerbedingungen vor Aufnahme in das Setup geprüft werden.

### Kartenterminal / Netzbetreiber

- TOR spricht ZVT über Portalum.Zvt an.
- Zertifizierung, Terminalvertrag, Netzbetreiberfreigabe und ZVT-Konfiguration
  des konkreten Zahlungsdienstleisters sind keine Rechte aus der MIT-Lizenz.

### DSFinV-K Beschreibungsdateien (BZSt)

- `src/TorPos.Infrastructure/Dsfinvk/index.xml` und `gdpdu-01-09-2004.dtd` stammen
  unverändert aus `dsfinv_k_v_2_4.zip`, veröffentlicht vom Bundeszentralamt für
  Steuern:
  https://www.bzst.de/DE/Unternehmen/Aussenpruefungen/DigitaleSchnittstelleFinV/digitaleschnittstellefinv_node.html
- Sie werden unverändert in jeden DSFinV-K-Export geschrieben, wie die DSFinV-K es
  vorsieht (SHA-256 in `R131ReviewTests`).

## Release-Nachweis

Für jeden freizugebenden Build archivieren:

- `dotnet list package --include-transitive` des finalen Source-Stands,
- SBOM bzw. Paketliste mit exakten Versionen,
- zugehörige Lizenztexte/Notices,
- schriftliche Herstellerfreigaben für gebündelte proprietäre Runtime-Dateien,
- Hashwerte der Kunden-Setup-Datei und der verwendeten Drittanbieterdateien.
