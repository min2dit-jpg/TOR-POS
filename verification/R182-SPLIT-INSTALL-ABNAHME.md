# R182 – TOR Einzelhandel / TOR Gastro Erst-Installationsabnahme (Windows)

Diese Abnahme prüft ausschließlich die **Produkttrennung** von R182. Fiskalische
Hardware-Abnahme (TSE, Terminal, Drucker) ist nicht Teil dieses Protokolls und
wird weiterhin über `verification/HARDWARE-E2E-TEMPLATE.md` geführt.

Die Setups sind **Testfreigaben**. Sie werden nicht als Produktivrelease an
Endkunden gegeben.

## Gemeldete Teilergebnisse (Betreiber, 22.09.2026)

Der Betreiber hat nach eigener Angabe auf einem echten Windows-PC geprüft und
als bestanden gemeldet:

- Installation von **TOR Einzelhandel** aus dem echten Setup
- Installation von **TOR Gastro** aus dem echten Setup
- beide Produkte **parallel** auf demselben PC lauffähig
- die Produkte **sehen die Daten des jeweils anderen nicht**
- Deinstallation des einen **beschädigt das andere nicht**

Diese Meldung ist hier festgehalten, damit sie nicht verloren geht. Sie ersetzt
das Protokoll **nicht**: die einzelnen Zeilen unten bleiben ungekreuzt, weil sie
nicht einzeln bestätigt wurden, und das Gesamtergebnis bleibt offen, bis das
Protokoll durchlaufen und unterschrieben ist. Insbesondere sind die
Editionsbindung gegen Manipulation (3), die Legacy-Übernahme aus R181 (4/4b),
das Demo-Recht (5) und die Gegenproben der Deinstallation (6) noch offen.

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
| `TOR-Einzelhandel-Setup.exe` im Artefakt vorhanden | ☐ PASS ☐ FAIL | |
| `TOR-Gastro-Setup.exe` im Artefakt vorhanden | ☐ PASS ☐ FAIL | |
| Zustand von `%APPDATA%\TOR-POS-Pro` vor dem Test notiert | ☐ PASS ☐ FAIL | |
| Frühere Testinstallationen unter den alten Namen (TOR KIOSK / TOR DÖNER) deinstalliert | ☐ PASS ☐ FAIL | |
| Reste `%APPDATA%\TOR-KIOSK`, `%APPDATA%\TOR-DOENER`, `%PROGRAMDATA%\TOR-KIOSK`, `%PROGRAMDATA%\TOR-DOENER` entfernt | ☐ PASS ☐ FAIL | |

## 1. Installation TOR Einzelhandel

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Setup läuft ohne Fehler durch | ☐ PASS ☐ FAIL | |
| Programmordner `...\TOR Einzelhandel\TOR-Einzelhandel.exe` | ☐ PASS ☐ FAIL | |
| Desktop-/Startmenüeintrag heißt „TOR Einzelhandel“ | ☐ PASS ☐ FAIL | |
| Setup fragt **keine** Zugangsdaten mehr ab | ☐ PASS ☐ FAIL | |
| Programm startet und meldet Edition **EINZELHANDEL** | ☐ PASS ☐ FAIL | |
| Anmeldung mit `admin` / `admin` funktioniert sofort, ohne erzwungene Passwortänderung | ☐ PASS ☐ FAIL | |
| Kassenart steht mittig und gross über der ganzen Zeile | ☐ PASS ☐ FAIL | |
| Datenordner `%APPDATA%\TOR-Einzelhandel` angelegt | ☐ PASS ☐ FAIL | |

## 2. Installation TOR Gastro neben TOR Einzelhandel

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| Setup läuft durch, ohne TOR Einzelhandel zu verändern | ☐ PASS ☐ FAIL | |
| Programmordner `...\TOR Gastro\TOR-Gastro.exe` | ☐ PASS ☐ FAIL | |
| Beide Produkte stehen getrennt in „Apps & Features“ | ☐ PASS ☐ FAIL | |
| Programm startet und meldet Edition **GASTRONOMIE** | ☐ PASS ☐ FAIL | |
| Anmeldung mit `admin` / `admin` funktioniert sofort | ☐ PASS ☐ FAIL | |
| Kassenart steht mittig und gross über der ganzen Zeile | ☐ PASS ☐ FAIL | |
| Datenordner `%APPDATA%\TOR-Gastro` angelegt | ☐ PASS ☐ FAIL | |
| Beide Programme laufen gleichzeitig (getrennte Mutexe) | ☐ PASS ☐ FAIL | |
| Artikel in einem Produkt erscheinen **nicht** im anderen | ☐ PASS ☐ FAIL | |

## 3. Editionsbindung gegen Manipulation

Jeweils in einer Eingabeaufforderung starten:

```
set TOR_POS_PRODUCT_EDITION=IMBISS
"...\TOR Einzelhandel\TOR-Einzelhandel.exe"
```

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| TOR Einzelhandel bleibt Einzelhandel und nutzt weiter `%APPDATA%\TOR-Einzelhandel` | ☐ PASS ☐ FAIL | |
| Gleicher Test mit `TOR_POS_EDITION=IMBISS`: keine Wirkung | ☐ PASS ☐ FAIL | |
| Umgekehrter Test auf TOR Gastro mit `KIOSK`: keine Wirkung | ☐ PASS ☐ FAIL | |
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
| `%PROGRAMDATA%\TOR-Einzelhandel\trial-installation.id` existiert | ☐ PASS ☐ FAIL | |
| `%PROGRAMDATA%\TOR-Gastro\trial-installation.id` existiert und ist verschieden | ☐ PASS ☐ FAIL | |
| TOR Einzelhandel deinstallieren, neu installieren: **kein** zweites Demo-Fenster | ☐ PASS ☐ FAIL | |
| Demo-Ablauf in einem Produkt beendet das andere Produkt nicht | ☐ PASS ☐ FAIL | |

## 6. Deinstallation

| Prüfung | Ergebnis | Bemerkung |
|---|---|---|
| TOR Einzelhandel deinstallieren lässt `%APPDATA%\TOR-Einzelhandel` bestehen | ☐ PASS ☐ FAIL | |
| TOR Gastro bleibt installiert, startbar und vollständig | ☐ PASS ☐ FAIL | |
| Verknüpfungen des anderen Produkts bleiben erhalten | ☐ PASS ☐ FAIL | |
| `%APPDATA%\TOR-POS-Pro` bleibt unberührt | ☐ PASS ☐ FAIL | |
| Gegenprobe mit TOR Gastro | ☐ PASS ☐ FAIL | |

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
