# TOR POS Pro – Kartenzahlung / Terminal-Kompatibilität
Stand: 04.09.2026

## TOR Standard für Deutschland

TOR POS verwendet als primäre universelle Kassenschnittstelle:

**ZVT über TCP/IP**

ZVT ist eine in Deutschland breit eingesetzte herstellerunabhängige Schnittstelle
zwischen Kassensystem und Zahlungsterminal.

TOR v0.6.1 nutzt für den ZVT-Client die .NET-Bibliothek Portalum.Zvt 3.4.0 (MIT).

## Aktive TOR-Funktionen

- TCP/IP-Verbindung zum Terminal
- ZVT-Verbindungstest
- ZVT-Anmeldung
- Betrag an das Terminal senden
- Zahlungsergebnis abwarten
- Verkauf nur nach erfolgreicher Terminalbestätigung abschließen
- Terminal-ID / Terminalbelegnummer / Trace-Nummer / Kartenname für Abstimmung
- keine Speicherung von PAN, Track-Daten, PIN oder CVV/CVC

## Hersteller / Anbieter

### Ingenico
**TOR-Status: ZVT aktiv**

Ingenico dokumentiert ZVT für klassische Terminals und auch für aktuelle AXIUM-
Lösungen. Die konkrete Payment-Applikation bzw. der Netzbetreiber muss die
Kassenschnittstelle freischalten.

### CCV
**TOR-Status: ZVT aktiv**

CCV dokumentiert ZVT und O.P.I. für integrierte Terminals wie Pad Next, Pad und Q25.
TOR v0.6.1 verwendet ZVT TCP/IP.

### Verifone / TeleCash
**TOR-Status: ZVT aktiv**

TeleCash dokumentiert ZVT-Verbindungen über COM, USB und TCP/IP.
TOR v0.6.1 implementiert den TCP/IP-Weg.

### PAX
**TOR-Status: Test je Netzbetreiber**

PAX-Hardware allein garantiert keine ZVT-Kompatibilität. Entscheidend ist die installierte
Payment-Applikation des Netzbetreibers/Acquirers. Wenn sie ZVT TCP/IP bereitstellt,
kann der TOR-ZVT-Modus verwendet und am konkreten Gerät getestet werden.

### Weitere ZVT-Terminals
**TOR-Status: Test je Gerät**

Wenn Terminalsoftware/Netzbetreiber ZVT TCP/IP aktiv anbietet, kann der
herstellerunabhängige TOR-Profilmodus `AUTO_ZVT` verwendet werden.

## Nicht als ZVT pauschal freigegeben

### SumUp
SumUp wird nicht als universelles ZVT-Terminal zugesagt. Eine separate offiziell
unterstützte SumUp-Integration muss implementiert und getestet werden.

### Stripe Terminal
Stripe nutzt Terminal SDK bzw. server-driven Terminal API. Dafür ist ein separater
TOR-Adapter erforderlich.

### Adyen
Adyen nutzt Terminal API auf Basis des nexo Retailer Protocol, lokal oder über Cloud.
Dafür ist ein separater TOR-Adapter erforderlich.

## Typische ZVT-Netzwerkeinstellungen

- Terminal-IP: statisch oder DHCP-Reservierung empfohlen
- ZVT TCP-Port: häufig 20007 oder 20008
- Kasse und Terminal: gegenseitig im LAN erreichbar
- Firewall: lokalen TCP-Verkehr zum konfigurierten Port erlauben

Die tatsächliche Port-/Kassenschnittstellenkonfiguration des Terminals hat Vorrang.

## Abnahmetest vor Kundenfreigabe

Für jedes konkrete Netzbetreiber-/Terminalprofil mindestens testen:

1. Verbindung
2. ZVT-Anmeldung
3. erfolgreiche Zahlung
4. abgelehnte Zahlung
5. Abbruch am Terminal
6. Timeout
7. Terminal ausgeschaltet
8. Netzwerk während Zahlung getrennt
9. Doppelzahlungsschutz
10. Kassenbeleg/Terminalbeleg
11. Tagesabschluss / Abstimmung
12. Neustart von Kasse und Terminal

Ein Modell darf erst als "TOR getestet" beworben werden, nachdem dieser Test am realen
Gerät und mit der realen Netzbetreiber-Payment-App bestanden wurde.
