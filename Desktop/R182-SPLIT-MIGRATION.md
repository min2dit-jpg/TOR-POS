# R182 – TOR KIOSK / TOR DÖNER Split-Migration

## Ziel

R182 trennt die bisher gemeinsame TOR-POS-Installation in zwei feste Produkte, ohne die R181-Daten beim Übergang zu zerstören:

- **TOR KIOSK** – Edition `KIOSK`, EXE `TOR-KIOSK.exe`, AppData `TOR-KIOSK`
- **TOR DÖNER** – Edition `IMBISS`, EXE `TOR-DOENER.exe`, AppData `TOR-DOENER`

Beide Produkte werden aus demselben geprüften Quellcode gebaut. Produktidentität, Windows-AppId, Installationsordner, Prozess-Mutex, Benutzer-Datenpfad und maschinenweiter Lizenz-/Trial-Pfad sind getrennt.

## R181 bleibt Rückfallbasis

Die bestehende R181-Installation und `%APPDATA%\TOR-POS-Pro` werden bei der Split-Migration **nicht verschoben, nicht gelöscht und nicht überschrieben**. Eine Migration ist Copy-once und backup-first.

Vor der Kopie wird ein verifiziertes Backup erzeugt. Die dedizierte Zielinstallation erhält einen Provenance-Marker mit Quelle und einem SHA-256-Zustandsfingerprint der migrierten SQLite-Daten. Ein bereits initialisiertes Ziel wird nicht erneut überschrieben.

## Unterbrochene R181-Installationen (WAL im Quellstand)

Eine R181-Kasse, die nicht sauber beendet wurde – Stromausfall, Absturz, hartes Ausschalten – behält bereits committed Transaktionen in `torpos.db-wal`. Genau in diesem Fall braucht der Kunde die Übernahme am dringendsten.

Die Staging-Kopie wird deshalb **vor jeder Datenbankprüfung** und über `torpos.db` **und** `torpos.db-wal` gegen den Quellstand verglichen. Erst danach wird die Kopie mit `PRAGMA quick_check` geöffnet; dieses Öffnen führt die WAL-Frames in die Hauptdatei zusammen und entfernt die WAL-Datei. Der Provenance-Marker wird anschließend aus dem so entstandenen Startzustand gebildet, damit die spätere Rollback-Bewertung genau diesen Stand kennt.

Ohne diese Reihenfolge würde eine fehlerfreie Kopie als `Legacy database changed` abgewiesen; das dedizierte Produkt würde mit Exit-Code 182 und ohne Fenster beenden. Der Quellstand unter `%APPDATA%\TOR-POS-Pro` wird dabei zu keinem Zeitpunkt geöffnet oder verändert – er wird ausschließlich gelesen.

## Demo-Recht je PC und Produkt

Das 7-Tage-Demo gilt **einmal pro PC und pro Produkt**. TOR KIOSK und TOR DÖNER führen je eine eigene maschinenweite Demo-Identität unter `%PROGRAMDATA%\TOR-KIOSK` bzw. `%PROGRAMDATA%\TOR-DOENER`. Beide Ordner sind im Installer als `uninsneveruninstall` markiert und überstehen eine Deinstallation, sodass ein erneutes Setup kein zweites Demo-Fenster öffnet.

Die Split-Migration kopiert ausschließlich `%APPDATA%`. Eine bereits verbrauchte R181-Demo wird damit nicht in ein Produkt übernommen, und kein Produkt kann die Demo des anderen verbrauchen.

## Produktidentität gegen Laufzeitmanipulation

Die feste Produktidentität entsteht im Build und ist jeder Laufzeitquelle übergeordnet. Der gemeinsame Build setzt `TOR_POS_PRODUCT_EDITION` beim Start ausdrücklich zurück, damit eine von außen gesetzte Variable weder den Datenpfad noch die maschinenweite Demo-/Lizenzidentität in ein Split-Produkt umlenken kann. `InstallationEdition.EnforceAsync` weist in einem dedizierten Build jede abweichende Edition ab, statt sie anzuwenden; der `TOR_POS_EDITION`-Fallback bleibt allein dem gemeinsamen Build vorbehalten.

## Editionssicherheit

Eine automatische Übernahme ist nur zulässig, wenn `edition.permanent.lock` der alten R181-Installation exakt zur Ziel-Edition passt. Eine temporäre Testauswahl genügt nicht. Eine KIOSK-Historie darf nicht automatisch in TOR DÖNER übernommen werden und umgekehrt.

## Rollback / Rejoin

Der R182-Format-3-Marker qualifiziert den persistenten SQLite-Zustand aus `torpos.db` **und** `torpos.db-wal`. `torpos.db-shm` wird bewusst nicht einbezogen, weil es nur transienter Shared-Memory-Zustand ist. Damit können committed Writes, die noch ausschließlich im WAL liegen und die Hauptdatenbankdatei noch nicht verändert haben, nicht fälschlich als unveränderter Stand gelten.

Solange dieser Zustandsfingerprint der dedizierten Datenbank noch dem unmittelbar migrierten Stand entspricht, kann die unveränderte R181-Kopie als verlustfreie Rückfallbasis verwendet werden (`SafeBeforeDedicatedWrites`). Ältere Format-2-Marker werden konservativ behandelt: sobald eine WAL-Datei vorhanden ist, wird kein automatischer sicherer Rollback mehr behauptet.

Sobald in TOR KIOSK oder TOR DÖNER neue Daten geschrieben wurden, wird ein automatischer Rejoin als unsicher bewertet (`DedicatedDataChanged`). Ab diesem Punkt dürfen zwei fiskalische Historien nicht still zusammenkopiert werden. Eine spätere Zusammenführung benötigt eine ausdrücklich geprüfte Migration mit fachlicher Datenabstimmung.

## Side-by-side Windows-Installation

TOR KIOSK und TOR DÖNER besitzen unterschiedliche stabile Inno-Setup-AppIds, Installationsordner, EXE-Namen, Mutex-Namen und Startmenü-/Desktop-Identitäten. Deshalb können beide Produkte parallel installiert sein. Das Deinstallieren eines Produkts darf den Datenordner nicht löschen (`uninsneveruninstall`) und darf die Installation des anderen Produkts nicht adressieren.

Die alte gemeinsame TOR POS Pro AppId bleibt unverändert. Dadurch wird R181 nicht versehentlich als Upgrade-Ziel eines der neuen Produkte behandelt.

## Freigaberegeln

PR #58 bleibt Draft, bis alle folgenden Punkte nachweislich erfüllt sind:

1. vollständige Safety-Test-Suite grün;
2. TOR KIOSK und TOR DÖNER jeweils als Release-Build erfolgreich;
3. beide dedizierten Publish-Ausgaben erfolgreich;
4. beide Setup-EXE erfolgreich kompiliert und als CI-Artefakt vorhanden;
5. backup-first Migration und Editions-Mismatch regressionsgetestet;
6. Rollback vor dedizierten Writes als sicher und nach dedizierten Writes als gesperrt regressionsgetestet, einschließlich WAL-only Writes;
7. Side-by-side AppId/Installations-/Datenpfad-Trennung regressionsgetestet;
8. Migration einer unterbrochenen, WAL-behafteten R181-Installation regressionsgetestet;
9. Produktidentität gegen Umgebungs-/Konfigurationsmanipulation regressionsgetestet, einschließlich getrennter Demo-Identität je PC und Produkt.

Physische TSE-/Terminaltests bleiben von dieser reinen Produkttrennung getrennt und werden erst mit verfügbarer Hardware durchgeführt.
