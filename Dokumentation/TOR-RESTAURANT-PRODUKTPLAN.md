# TOR Restaurant / TOR Restaurant Plus – Produktgrundlage

Stand: 22.09.2026

## 1. Produktlinie

TOR Restaurant wird als eigenständige Produktlinie neben TOR Einzelhandel und TOR Gastro aufgebaut.

Die erste Produktgeneration ist **ausschließlich deutschsprachig**. Sie verwendet keine Laufzeit-Umschaltung über UiLanguage und keine TR/EN-Wörterbücher.

Ziel:
- gemeinsame, geprüfte TOR-Basis für Verkauf, Zahlungen, TSE, DSFinV-K, Benutzer, Audit und Berichte,
- Restaurant-spezifische Funktionen in einer klar abgegrenzten Produktschicht,
- zwei kommerzielle Pakete mit zentral definierter Feature-Matrix.

## 2. Pakete

### TOR Restaurant

Muss den vollständigen normalen Restaurantbetrieb ohne Zusatzkauf ermöglichen.

Enthalten:
- Tischplan
- Tisch öffnen
- Tisch umbuchen
- Tische zusammenlegen
- Tischrechnung
- Splitrechnung nach Artikel
- Splitrechnung nach Person
- Kellnerkonten
- Kellnerrechte
- offene Tische
- grundlegendes Küchenrouting
- Küchendrucker
- Abholung / Außer-Haus

Grundsatz:
Eine Funktion, die für den täglichen Betrieb eines klassischen Restaurants notwendig ist, gehört nicht hinter eine Plus-Schranke.

### TOR Restaurant Plus

Enthält alle Funktionen von TOR Restaurant plus:
- Handheld-Bestellung
- Kitchen Display System (KDS)
- erweiterte Küchenstationen
- Gangsteuerung
- Reservierungen
- Kundenkartei
- Kundenbindung
- mehrere Kassen / Terminals
- Filialverbund
- erweiterte Restaurant-Auswertungen

## 3. Architektur

Gemeinsam bleiben:
- Produkt- und Warengruppenmodell
- Waren-/Preislogik
- Steuern / MwSt.
- Warenbestand
- Verkauf und Zahlung
- TSE
- DSFinV-K
- DATEV
- Audit Log
- Benutzer/Authentifizierung
- Drucker-Grundlage
- Kartenterminal-Grundlage
- Backup
- Cloud-Grundlage

Restaurant-spezifisch:
- TableSession / Tischvorgang
- Tischstatus
- Tischbelegung
- Service-/Kellnerzuordnung
- Bestellpositionen vor Kassenabschluss
- Splitrechnung
- Tischtransfer
- Küchenrouting
- Gänge
- KDS
- Handheld-Protokoll
- Reservierung

Diese Restaurant-Logik darf nicht in generische Verkaufsmodelle gedrückt werden, wenn sie dort Einzelhandel oder TOR Gastro belastet.

## 4. Daten- und Laufzeitisolation

TOR Restaurant erhält eine eigene:
- EXE-Identität
- Installer
- App-ID
- Mutex
- Datenverzeichnis
- Trial-Identität
- Lizenz-Produktlinie
- Update-Kanal

**TOR Restaurant Plus ist kein zweites Datenprodukt.** Plus ist eine Lizenzstufe derselben TOR-Restaurant-Installation. Ein Upgrade von Restaurant auf Restaurant Plus darf keine Datenmigration und keine Neuinstallation verlangen.

Restaurant und Restaurant Plus verwenden deshalb dasselbe lokale Restaurant-Datenverzeichnis. Die Plus-Lizenz schaltet ausschließlich zusätzliche Module frei.

Ein Restaurant-Kunde darf niemals auf dasselbe lokale Datenverzeichnis wie TOR Gastro, TOR Einzelhandel oder ein anderer TOR-Kunde zugreifen.

## 5. Lokaler Betriebsmodus

Der Restaurantbetrieb muss offline weiterarbeiten.

Lokale Hauptinstanz:
- Restaurant-Desktop / Server
- lokale Datenbank
- TSE
- Drucker
- Kartenterminal
- Tisch- und Bestellzustand

Plus-Endgeräte:
- Handheld
- KDS
- weitere Kassen

verbinden sich mit einem kontrollierten lokalen Restaurant-Service/API und **nicht direkt mit der SQLite-Datei**.

## 6. Gleichzeitigkeit

Für Tisch- und Bestellvorgänge werden spätere Schreiboperationen mit:
- Vorgangs-ID
- Versionsnummer
- Zeitstempel
- Bediener-ID
- Geräte-ID

versehen.

Ziel:
Keine stillen Überschreibungen, wenn zwei Kellner gleichzeitig denselben Tisch bearbeiten.

## 7. Fiskalgrenze

Ein Tischvorgang ist nicht automatisch ein abgeschlossener Kassenbon.

Die Restaurant-Schicht verwaltet den operativen Vorgang.
Der bestehende TOR-Fiskal-/Checkout-Kern bleibt für den tatsächlichen Verkauf und Abschluss zuständig.

TSE-, DSFinV-K- und steuerliche Regeln dürfen nicht durch eine Restaurant-Sonderlogik umgangen werden.

## 8. Feature-Gating

Die maßgebliche Feature-Matrix liegt in:

`Desktop/src/TorPos.Core/RestaurantProduct.cs`

Keine UI darf Plus-Funktionen nur anhand eines sichtbaren Buttons absichern.
Später müssen Lizenz, Application Service und UI dieselbe zentrale Feature-Entscheidung verwenden.

## 9. Erste Implementierungsreihenfolge

1. Produktidentität TOR Restaurant
2. eigener Datenpfad und Installer
3. Lizenzstufen Restaurant / Restaurant Plus
4. Restaurant-Schema
5. Tischplan
6. Tischvorgang
7. Kellnerzuordnung
8. Tischtransfer / Zusammenlegen
9. Splitrechnung
10. Küchenrouting
11. Restaurant-Testmatrix
12. Plus: lokaler Service/API
13. Plus: Handheld
14. Plus: KDS
15. Plus: Reservierungen / Multi-Terminal / Filialverbund

## 10. Nicht in Phase 1

Noch nicht implementieren:
- Cloud-Zwang
- Online-only Tischbetrieb
- direkte Handheld-Zugriffe auf SQLite
- Mehrsprachigkeit
- Plus-Funktionen im Standardpaket
- gemeinsame Datenordner mit TOR Gastro


## 11. Performance- und Lastgrenzen

Performance ist eine Produktanforderung und kein späteres Optimierungsprojekt.

Verbindliche Regeln:
- KASSIEREN, Warenkorb und Hauptkassen-UI dürfen niemals auf Cloud-Sync, KDS, Küchendrucker, Reservierungsabgleich oder Terminal-Heartbeat warten.
- Keine synchrone Netzwerk- oder Drucker-I/O auf dem UI-Thread.
- Restaurant-Geräte-API startet parallel und darf den sichtbaren Programmstart nicht blockieren.
- Handheld/KDS/Multi-Terminal lesen über read-only WAL-Verbindungen; reine Polling-/Sync-Lesevorgänge dürfen die SQLite-Schreibwarteschlange nicht belegen.
- Tischübersichten werden mit einer aggregierten Abfrage geladen; keine N+1-Abfragen pro Tisch.
- Delta-Sync überträgt nur Events nach der zuletzt bestätigten Event-ID.
- Operator-PIN wird pro Schicht/Login teuer verifiziert; Folgekommandos verwenden ein gerätegebundenes, gehasht gespeichertes Operator-Session-Token.
- Device- und Operator-`last_seen`-Writes werden gedrosselt; Polling darf keinen Write-Storm erzeugen.
- Mutationen verwenden Command-ID/Idempotency. Netzwerk-Retry darf weder Artikel noch TSE-Änderung noch Küchenbon duplizieren.
- Verschiedene physische Küchendrucker laufen in getrennten parallelen Lanes. Ein langsamer/offline Drucker darf andere Drucker nicht blockieren.
- Innerhalb desselben Druckers bleibt die Reihenfolge strikt erhalten.
- CHECK_REQUESTED/Checkout sperrt den Tisch gegen konkurrierende Änderungen; Zahlung hat Vorrang vor Hintergrund-Sync.
- Langsame oder ausgefallene externe Komponenten müssen mit festem Timeout/fail-safe enden und dürfen keine unbegrenzten Wartezustände erzeugen.

Lastziel für die Plus-Architektur:
- mindestens 20 gleichzeitig verbundene Restaurant-Endgeräte,
- mindestens 100 aktive/konfigurierte Tische,
- mehrere gleichzeitige Kellner-Schreibvorgänge,
- mehrere unabhängige Küchenstationen/Drucker,
- laufendes KDS und Delta-Sync,
- währenddessen weiterhin reaktionsfähige Hauptkasse und Checkout.

Diese Lastziele sind vor einer Production-Freigabe mit reproduzierbaren Stress-/Regressionsprüfungen zu verifizieren. Ein funktional korrekter Build, der die Kasse unter Last spürbar blockiert, gilt nicht als freigabefähig.
