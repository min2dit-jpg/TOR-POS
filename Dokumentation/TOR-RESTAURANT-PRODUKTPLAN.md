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

TOR Restaurant und TOR Restaurant Plus müssen später eigene:
- EXE-Identität
- Installer
- App-ID
- Mutex
- Datenverzeichnis
- Trial-Identität
- Lizenz-Produktcode
- Update-Kanal

erhalten.

Ein Restaurant-Kunde darf niemals auf dasselbe lokale Datenverzeichnis wie TOR Gastro oder ein anderer TOR-Kunde zugreifen.

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

1. Produktidentität Restaurant / Restaurant Plus
2. eigene Datenpfade und Installer
3. Lizenzcodes
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
