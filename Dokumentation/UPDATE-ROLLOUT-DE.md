# TOR POS – Kunden-Update / Rollout

Stand: R178 · 0.7.33.878

## Zielbild

TOR POS prüft standardmäßig den offiziellen Update-Endpunkt:

`https://updates.torpos.de/`

Die Update-Funktion ist unabhängig davon, ob der Kunde TOR Cloud verwendet.
Produktive Updates werden niemals direkt aus GitHub oder aus einem beliebigen
Download-Link installiert.

## Sicherheitskette

Ein produktives Kunden-Update wird nur akzeptiert, wenn alle folgenden Prüfungen
erfolgreich sind:

1. Update-Metadaten werden über HTTPS vom konfigurierten TOR Update Server geladen.
2. Download-URL muss auf demselben Origin liegen wie der Update Server.
3. Installer-SHA-256 muss exakt dem Manifest entsprechen.
4. Das Manifest muss den im TOR-POS-Build fest hinterlegten Code-Signing-Thumbprint nennen.
5. Windows Authenticode muss für den tatsächlich heruntergeladenen Installer gültig sein.
6. Vor der Installation erstellt TOR POS ein Datenbank-Backup.
7. Installation beginnt erst, wenn TOR POS sauber beendet ist.
8. Nach erfolgreichem Installer-Exit wird TOR POS automatisch neu gestartet.

Der produktive `UpdateSignerThumbprint` bleibt absichtlich leer, bis ein
TOR/Demirkaan-Code-Signing-Zertifikat beschafft, sicher verwahrt und der
Thumbprint ausdrücklich in einem Release-Build fest hinterlegt wurde. Ohne
diesen Schritt ist Remote-Installation fail-closed.

## Kanäle

### PILOT

PILOT ist für kontrollierte Vorabtests gedacht. Nur ausgewählte Testkassen
werden in den technischen Einstellungen auf `PILOT` gestellt.

Empfohlener Ablauf:

- neue Version CI-grün bauen;
- Setup signieren;
- auf PILOT veröffentlichen;
- mindestens eine echte KIOSK- und eine echte IMBISS-Installation aktualisieren;
- Start, Datenbankmigration, Drucker, Scanner, Kassenschublade, Zahlung und
  relevante Hardware prüfen;
- erst danach STABLE veröffentlichen.

### STABLE

STABLE ist der normale Kundenkanal und Standard jedes TOR-POS-Systems.
Bestehende ältere Clients ohne Kanalparameter verwenden weiterhin
`manifest.json` und bleiben damit kompatibel.

## Server-Bereitstellung

Für den produktiven Betrieb wird `updates.torpos.de` per DNS/TLS auf den
TOR-Update/Cloud-Service geroutet.

Empfohlene Server-Umgebung:

`TOR_UPDATE_PUBLIC_URL=https://updates.torpos.de`

Der Reverse Proxy muss HTTPS terminieren und `/api/v1/updates/check` sowie
`/updates/*` an den Node-Service weiterleiten. Der Update-Ordner wird über
`TOR_CLOUD_UPDATES` festgelegt.

## Veröffentlichung

Nur den geprüften Publisher verwenden.

PILOT:

```powershell
.\PUBLISH-UPDATE.ps1 `
  -SetupPath C:\Build\TOR-POS-Pro-Setup.exe `
  -Version 0.7.33.878 `
  -Revision R178 `
  -SignerThumbprint <TOR-CODESIGN-THUMBPRINT> `
  -Channel PILOT `
  -ReleaseNotes "Scanner/Kassenschublade/Update-Delivery"
```

STABLE:

```powershell
.\PUBLISH-UPDATE.ps1 `
  -SetupPath C:\Build\TOR-POS-Pro-Setup.exe `
  -Version 0.7.33.878 `
  -Revision R178 `
  -SignerThumbprint <TOR-CODESIGN-THUMBPRINT> `
  -Channel STABLE `
  -ReleaseNotes "Freigegebener Kundenstand"
```

Der Publisher prüft vor Veröffentlichung Authenticode und SHA-256. Die
hashbenannte EXE wird atomar bereitgestellt; ein fehlgeschlagener Publish
überschreibt das bisher aktive Manifest nicht.

## Sofortstopp / Rollback

Wenn nach Veröffentlichung ein Problem gefunden wird:

```powershell
.\DISABLE-UPDATE.ps1 -Channel STABLE
```

oder für Pilot:

```powershell
.\DISABLE-UPDATE.ps1 -Channel PILOT
```

Damit wird keine weitere Kasse auf diese Version angeboten. Bereits
aktualisierte Installationen werden nicht automatisch zurückgestuft. Für einen
Rollback wird eine zuvor archivierte, signierte und kompatible Version als
neuer kontrollierter Release bereitgestellt; Datenbank-Schema-Kompatibilität
muss vorher geprüft werden.

## Kundenerlebnis

- TOR POS prüft standardmäßig alle sechs Stunden im Hintergrund.
- Bei einer neuen Version sieht ein Admin oben `UPDATE · Rxxx`.
- Während Verkauf, offener Zahlung oder Recovery ist Update gesperrt.
- Nach Klick werden Download, Hash/Signaturprüfung und Backup durchgeführt.
- TOR POS schließt sauber.
- Windows zeigt bei Bedarf die UAC-Abfrage.
- Installer läuft still.
- Bei Erfolg startet TOR POS automatisch neu.

Ein `mandatory` Manifest markiert ein wichtiges Update sichtbar. Auch ein
wichtiges Update darf keine laufende Zahlung oder einen offenen Verkauf
gewaltsam unterbrechen.

## Vor dem ersten echten Kunden-Rollout

- [ ] `updates.torpos.de` DNS und gültiges TLS aktiv
- [ ] Reverse Proxy / Node Update-Endpunkt erreichbar
- [ ] TOR/Demirkaan Code-Signing-Zertifikat vorhanden
- [ ] Zertifikat-Thumbprint in `TorRelease.UpdateSignerThumbprint` gepinnt
- [ ] Installer CI sonrası signiert und Authenticode `Valid`
- [ ] PILOT-Update von mindestens zwei realen Kassen erfolgreich
- [ ] Backup + Migration + Neustart geprüft
- [ ] STABLE erst danach aktiviert
