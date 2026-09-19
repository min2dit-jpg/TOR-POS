# TOR POS – Security Policy

## Zweck

Diese Richtlinie beschreibt, wie Sicherheitsprobleme in TOR POS vertraulich
gemeldet und behandelt werden sollen. Sie ist getrennt von der fiskalischen
Produktivfreigabe: Ein bestandener Security-Test allein bestätigt keine
KassenSichV-, TSE- oder DSFinV-K-End-to-End-Freigabe.

## Unterstützter Stand

Sicherheitskorrekturen beziehen sich grundsätzlich auf den aktuellen Stand von
`main` und den dort in
`Desktop/src/TorPos.Core/ReleaseInfo.cs` ausgewiesenen Release.

Ältere Revisionen werden nur unterstützt, wenn dies vertraglich oder für eine
konkrete Fehlerbehebung ausdrücklich vereinbart ist.

## Sicherheitslücken vertraulich melden

Bitte sicherheitsrelevante Schwachstellen **nicht als öffentliches GitHub-Issue**
veröffentlichen.

Bevorzugt:

1. GitHubs private Vulnerability-Reporting / Security-Advisory-Funktion nutzen,
   sofern sie für das Repository verfügbar ist.
2. Andernfalls den bereits vereinbarten privaten TOR-Support-/Vertragskanal
   verwenden.

Ein Bericht sollte möglichst enthalten:

- TOR-Revision und Version,
- betroffene Komponente,
- reproduzierbare Schritte,
- erwartetes und tatsächliches Verhalten,
- mögliche Auswirkung,
- notwendige Voraussetzungen,
- relevante Log-Auszüge in bereinigter Form.

## Keine sensiblen Daten mitsenden

Sicherheitsberichte dürfen insbesondere keine unbereinigten Kundendaten oder
Geheimnisse enthalten. Nicht mitsenden:

- produktive Kassen-Datenbanken,
- Passwörter oder PINs,
- TSE-PIN, TSE-PUK oder Credential-Seed,
- private Lizenz- oder Code-Signing-Schlüssel,
- OAuth-/API-Secrets,
- vollständige Zahlungsdaten,
- PAN, PIN oder CVV,
- personenbezogene Daten, wenn sie für die Reproduktion nicht zwingend nötig
  sind.

Logs und Screenshots vor dem Versand entsprechend schwärzen.

## Besonders sicherheitskritische Bereiche

Folgende Themen sollen als Security-Finding behandelt werden, wenn sie eine
reale Schutzwirkung umgehen oder Datenintegrität gefährden:

- Authentifizierungs- oder Berechtigungsumgehung,
- Umgehung der Lizenz- oder Editionsbindung,
- Manipulation oder Umgehung der Update-Signaturprüfung,
- unberechtigte Veränderung von Audit-, Kassen-, TSE- oder Fiskaldaten,
- Checkout-Replay, Doppelbuchung oder erneute Belastung bei ungeklärtem
  Terminalstatus,
- Verlust oder falsche Wiederherstellung eines offenen Zahlungsvorgangs,
- Manipulation der TSE-/DSFinV-K-Zuordnung,
- Geheimnis- oder Schlüsseloffenlegung,
- Cloud-Authentifizierungs- oder Tenant-Isolation-Probleme,
- lokale Rechteausweitung durch TOR POS.

## Bearbeitungsprinzip

Ein bestätigtes Finding wird zunächst vertraulich reproduziert und eingegrenzt.
Eine Korrektur soll nach Möglichkeit zusammen mit einem automatisierten
Regressionstest erfolgen. Vor Übernahme in `main` müssen die vorhandenen
Build-, Safety-/Regression-, UI- und Cloud-Prüfungen erfolgreich sein.

Öffentliche technische Details sollen erst veröffentlicht werden, wenn eine
Korrektur bereitsteht und eine Offenlegung keine aktiven Installationen unnötig
gefährdet.

Diese Richtlinie verspricht keine feste Reaktions- oder Behebungsfrist; konkrete
Supportzeiten richten sich nach dem jeweiligen Vertrag.

## Abhängigkeiten und Herstellerkomponenten

Schwachstellen in Drittkomponenten wie Avalonia, .NET, SQLite, Portalum.Zvt,
Swissbit WORM API, Windows-Druckertreibern oder anderer Herstellersoftware
sollen ebenfalls gemeldet werden, wenn TOR POS dadurch konkret betroffen ist.

Lizenz- und Herstellerhinweise befinden sich in
`Desktop/THIRD-PARTY-NOTICES.md`.
