# R182 – TOR KIOSK / TOR DÖNER Erst-Installationsabnahme (Windows)

Diese Abnahme prüft ausschließlich die **Produkttrennung** von R182. Fiskalische
Hardware-Abnahme (TSE, Terminal, Drucker) ist nicht Teil dieses Protokolls und
wird weiterhin über `verification/HARDWARE-E2E-TEMPLATE.md` geführt.

Die Setups sind **Testfreigaben**. Sie werden nicht als Produktivrelease an
Endkunden gegeben.

## Prüfstand

- Commit SHA:
- CI-Run:
- Artefakt `TOR-POS-Split-Setups-<sha>`:
- Windows-Version:
- Test-PC:
- Datum:
- Tester:

## 0. Ausgangslage

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Beide Setups stammen aus einem grünen CI-Run des geprüften HEAD | ☐ PASS ☐ FAIL | |
| `TOR-KIOSK-Setup.exe` im Artefakt vorhanden | ☐ PASS ☐ FAIL | |
| `TOR-DOENER-Setup.exe` im Artefakt vorhanden | ☐ PASS ☐ FAIL | |
| Zustand von `%APPDATA%\TOR-POS-Pro` vor dem Test notiert | ☐ PASS ☐ FAIL | |

## 1. Installation TOR KIOSK

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Setup läuft ohne Fehler durch | ☐ PASS ☐ FAIL | |
| Programmordner `...\TOR KIOSK\TOR-KIOSK.exe` | ☐ PASS ☐ FAIL | |
| Desktop-/Startmenüeintrag heißt „TOR KIOSK“ | ☐ PASS ☐ FAIL | |
| Programm startet und meldet Edition **EINZELHANDEL** | ☐ PASS ☐ FAIL | |
| Datenordner `%APPDATA%\TOR-KIOSK` angelegt | ☐ PASS ☐ FAIL | |

## 2. Installation TOR DÖNER neben TOR KIOSK

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Setup läuft durch, ohne TOR KIOSK zu verändern | ☐ PASS ☐ FAIL | |
| Programmordner `...\TOR DÖNER\TOR-DOENER.exe` | ☐ PASS ☐ FAIL | |
| Beide Produkte stehen getrennt in „Apps & Features“ | ☐ PASS ☐ FAIL | |
| Programm startet und meldet Edition **GASTRONOMIE** | ☐ PASS ☐ FAIL | |
| Datenordner `%APPDATA%\TOR-DOENER` angelegt | ☐ PASS ☐ FAIL | |
| Beide Programme laufen gleichzeitig (getrennte Mutexe) | ☐ PASS ☐ FAIL | |
| Artikel in einem Produkt erscheinen **nicht** im anderen | ☐ PASS ☐ FAIL | |

## 3. Editionsbindung gegen Manipulation

Jeweils in einer Eingabeaufforderung starten:

```
set TOR_POS_PRODUCT_EDITION=IMBISS
"...\TOR KIOSK\TOR-KIOSK.exe"
```

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| TOR KIOSK bleibt Einzelhandel und nutzt weiter `%APPDATA%\TOR-KIOSK` | ☐ PASS ☐ FAIL | |
| Gleicher Test mit `TOR_POS_EDITION=IMBISS`: keine Wirkung | ☐ PASS ☐ FAIL | |
| Umgekehrter Test auf TOR DÖNER mit `KIOSK`: keine Wirkung | ☐ PASS ☐ FAIL | |
| Gemeinsamer Build (`TorPos.App.exe`) nutzt trotz gesetzter Variable `%APPDATA%\TOR-POS-Pro` | ☐ PASS ☐ FAIL | |
| Im Anmeldefenster ist die fremde Edition nicht wählbar | ☐ PASS ☐ FAIL | |

## 4. Legacy-Übernahme aus R181

Voraussetzung: die R181-Installation ist über eine gültige Lizenz dauerhaft an
eine Edition gebunden, `%APPDATA%\TOR-POS-Pro\edition.permanent.lock` existiert
und enthält genau die Edition des getesteten Produkts.

Ohne diese Datei wird **bewusst nicht** migriert (`LegacyEditionUnproven`); das
Produkt startet dann als leere Kasse. Das ist kein Fehler.

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Vor dem ersten Start ist das Produkt-Datenverzeichnis leer | ☐ PASS ☐ FAIL | |
| Nach dem ersten Start sind Artikel/Stammdaten übernommen | ☐ PASS ☐ FAIL | |
| Backup `%APPDATA%\TOR-POS-Migration-Backups\TOR-POS-Pro-before-*-split-*.zip` erzeugt | ☐ PASS ☐ FAIL | |
| `split-migration.json` im Produktordner vorhanden | ☐ PASS ☐ FAIL | |
| `%APPDATA%\TOR-POS-Pro` unverändert und vollständig | ☐ PASS ☐ FAIL | |
| Übernahme läuft nur einmal; ein zweiter Start kopiert nicht erneut | ☐ PASS ☐ FAIL | |
| Falsche Edition wird abgelehnt (Gegenprobe am anderen Produkt) | ☐ PASS ☐ FAIL | |

### 4b. Unterbrochene R181-Kasse (WAL)

R181 im Task-Manager hart beenden, damit `torpos.db-wal` erhalten bleibt, und
danach das Split-Produkt erstmals starten.

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| `%APPDATA%\TOR-POS-Pro\torpos.db-wal` vor dem Start vorhanden | ☐ PASS ☐ FAIL | |
| Split-Produkt startet normal (kein stilles Beenden) | ☐ PASS ☐ FAIL | |
| Daten inklusive der zuletzt gebuchten Vorgänge übernommen | ☐ PASS ☐ FAIL | |
| `%TEMP%\TOR-POS-split-migration-error.txt` wurde **nicht** erzeugt | ☐ PASS ☐ FAIL | |

> Startet das Produkt ohne Fenster, ist diese Datei der erste Anlaufpunkt.

## 5. Demo-Recht: einmal pro PC und Produkt

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| `%PROGRAMDATA%\TOR-KIOSK\trial-installation.id` existiert | ☐ PASS ☐ FAIL | |
| `%PROGRAMDATA%\TOR-DOENER\trial-installation.id` existiert und ist verschieden | ☐ PASS ☐ FAIL | |
| TOR KIOSK deinstallieren, neu installieren: **kein** zweites Demo-Fenster | ☐ PASS ☐ FAIL | |
| Demo-Ablauf in einem Produkt beendet das andere Produkt nicht | ☐ PASS ☐ FAIL | |

## 6. Deinstallation

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| TOR KIOSK deinstallieren lässt `%APPDATA%\TOR-KIOSK` bestehen | ☐ PASS ☐ FAIL | |
| TOR DÖNER bleibt installiert, startbar und vollständig | ☐ PASS ☐ FAIL | |
| Verknüpfungen des anderen Produkts bleiben erhalten | ☐ PASS ☐ FAIL | |
| `%APPDATA%\TOR-POS-Pro` bleibt unberührt | ☐ PASS ☐ FAIL | |
| Gegenprobe mit TOR DÖNER | ☐ PASS ☐ FAIL | |

## Nicht Bestandteil dieser Abnahme

- reale TSE-/Swissbit-Transaktionen
- fiskaltrust-Produktivpfad
- Kartenterminal, Drucker, Kassenschublade, Scanner
- DSFinV-K-Export

Die sechs `FiscalRelease`-Nachweisflags bleiben geschlossen.

## Ergebnis

- Gesamtergebnis: ☐ PASS ☐ FAIL
- Offene Punkte:
- Freigabe für weitere Tests durch:
