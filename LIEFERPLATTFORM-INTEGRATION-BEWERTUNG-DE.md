# Bewertung: Lieferando / Wolt / Uber Eats Integration

> Entscheidungsgrundlage vor der Implementierung, wie im ROADMAP.md
> gefordert ("önce sağlayıcı/middleware değerlendirmesi"). Kein Code in
> diesem Dokument - die eigentliche Anbieterwahl hat einen echten
> monatlichen Kostenfaktor und eine Vertragsbindung, das ist eine
> geschäftliche Entscheidung, keine rein technische.

## Ausgangslage

Drei Plattformen, drei unabhängige Partnerprogramme, drei unabhängige
APIs/Formate:

- **Lieferando** (Just Eat Takeaway): kein offenes Self-Service-API-Signup
  für kleine Kassenanbieter. Die POS-Anbindung läuft über einen
  Lieferando-Support-Kontakt bzw. über zertifizierte "Sales-/POS-Partner"
  - entweder per manueller Artikel-ID-Zuordnung im Lieferando-Partnerportal
  oder per CSV-Abgleich der Artikel-IDs. [Deliverect – Lieferando](https://www.deliverect.com/en/integrations/lieferando) · [MERGEPORT – Lieferando Sales](https://www.mergeport.com/lieferando-sales/)
- **Wolt**: offizielles "Wolt for Developers"-Partnerprogramm mit gelisteten
  zertifizierten Integrationspartnern (u. a. Deliverect, allO). [Wolt for Developers](https://developer.wolt.com/integration-partners/deliverect)
- **Uber Eats**: offizielles "Integration Partners"-Programm mit eigener
  Zertifizierung. [Uber Eats Integration Partners](https://merchants.ubereats.com/us/en/integration-partners/eats/)

Für einen einzelnen Kassenanbieter wie TOR bedeutet "direkt" pro Plattform:
ein eigenes Partnerzertifizierungsverfahren, ein eigenes Order-Format, eine
eigene laufende Pflege bei jeder API-Änderung - **dreimal**, unabhängig
voneinander.

## Option A – Direkt pro Plattform

**Aufwand:** hoch, wiederkehrend. Jede Plattform verlangt eine öffentlich
erreichbare Serverkomponente für eingehende Bestell-Webhooks (nicht der
einzelne Windows-Kassenrechner selbst - der hat keine feste, erreichbare
Adresse). TOR POS Pro ist aktuell ein reiner Client, der aktiv zu "TOR POS
Cloud" sendet (`TorCloudSyncService`) - es gibt noch keinen Server, der
eingehende Webhooks von Dritten annehmen könnte. Diese Option würde also
zusätzlich einen echten Backend-Dienst voraussetzen, den TOR selbst
betreiben und warten müsste, plus drei separate Zertifizierungsprozesse.

**Vorteil:** keine monatliche Aggregator-Gebühr, volle Kontrolle über das
Datenformat.

## Option B – Middleware/Aggregator (empfohlen zur Prüfung)

Ein Aggregator ist bereits bei Lieferando, Wolt und Uber Eats zertifiziert
und bietet EINE einheitliche API/EIN Webhook-Format für Bestellungen aus
allen angebundenen Plattformen:

- **Deliverect** - größte Marktabdeckung, direkte Anbindungen an
  Lieferando, Wolt und Uber Eats bestätigt. [Deliverect – Delivery Channels](https://www.deliverect.com/en-us/integrations/delivery-channels)
- **allO** - explizit für den deutschen Markt beworben, deckt Wolt,
  Lieferando und Uber Eats gemeinsam ab. [Wolt for Developers – allO](https://developer.wolt.com/integration-partners/allo)
- Weitere im US-Raum verbreitete Aggregatoren (Chowly, Cuboh, Otter) -
  deren Deutschland-/Lieferando-Abdeckung wurde in dieser Recherche nicht
  bestätigt, vor einer Empfehlung separat prüfen.

**Typische Kosten:** ca. 150-400 USD/Monat pro Standort bei US-Aggregatoren
[GeekyAnts – POS/Delivery Integration](https://geekyants.com/en-us/blog/how-to-integrate-your-pos-system-with-uber-eats-doordash--grubhub-in-the-usa)
- deutsche/europäische Anbieter (Deliverect EU, allO) müssen separat
  angefragt werden, hier lag kein belastbarer EUR-Preis vor.

**Architektonisch passt das gut zu TOR**: der Aggregator würde Bestellungen
an "TOR POS Cloud" pushen (dort müsste ein Webhook-Endpunkt entstehen -
ein serverseitiges, nicht clientseitiges Stück Arbeit), TOR POS Cloud
reicht die Bestellung über den bereits bestehenden Sync-Kanal an die
konkrete Kasse weiter. Die Windows-Kasse selbst bleibt wie bisher ohne
öffentlich erreichbaren Port - selbes Sicherheitsmodell wie jetzt schon.
Eine eingehende Lieferbestellung ließe sich fachlich wie eine IMBISS-ORDER
behandeln (Küchenbon, Abholnummer, eigener `Bestellung-V1`-TSE-Vorgang -
R83 ist dafür bereits gebaut), nicht wie ein komplett neues Konzept.

## Offene, wirklich geschäftliche Entscheidungen (nicht technisch lösbar)

1. **Welcher Aggregator** - Deliverect (breiteste Abdeckung, vermutlich
   höherer Preis) vs. allO (DACH-fokussiert) vs. eine direkte
   Lieferando-Partnerschaft ohne Aggregator für nur eine Plattform.
2. **Wer zahlt die monatliche Gebühr** - TOR POS Pro selbst (im
   Lizenzpreis einkalkuliert) oder der jeweilige Kunde direkt beim
   Aggregator?
3. **Reihenfolge** - alle drei Plattformen gleichzeitig oder zunächst nur
   eine (Lieferando hat in Deutschland die größte Verbreitung)?
4. **"TOR POS Cloud"-Backend-Erweiterung** - ein Webhook-Endpunkt dort ist
   eine neue, dauerhaft laufende Serverkomponente, kein reines
   Desktop-App-Feature - das ändert den Betriebsaufwand des Produkts.

## Empfehlung

Nicht direkt mit allen drei Plattformen einzeln verhandeln. Zunächst mit
**einem** Aggregator (Deliverect oder allO) Kontakt aufnehmen, reale
Preise/Vertragsbedingungen für den deutschen Markt einholen, und erst
danach entscheiden, ob sich der Umbau von "TOR POS Cloud" zu einem
Webhook-Empfänger lohnt. Bis diese drei geschäftlichen Fragen oben
beantwortet sind, ist ein Code-Vorgriff verfrüht - ein einseitig gebauter
Webhook-Empfänger ohne feststehenden Aggregator-Vertrag würde vermutlich
nicht zum tatsächlich gewählten API-Format passen.

## Quellen

- [Deliverect – Lieferando](https://www.deliverect.com/en/integrations/lieferando)
- [Deliverect – Wolt](https://www.deliverect.com/en/integrations/wolt)
- [Deliverect – Uber Eats](https://www.deliverect.com/en-us/integrations/uber-eats)
- [Deliverect – Delivery Channels Übersicht](https://www.deliverect.com/en-us/integrations/delivery-channels)
- [Wolt for Developers – Integration Partners](https://developer.wolt.com/integration-partners/deliverect)
- [Wolt for Developers – allO](https://developer.wolt.com/integration-partners/allo)
- [Uber Eats – Integration Partners](https://merchants.ubereats.com/us/en/integration-partners/eats/)
- [Uber Eats – POS Integration Artikel](https://merchants.ubereats.com/us/en/resources/articles/pos-integration/)
- [MERGEPORT – Lieferando Sales-Partner](https://www.mergeport.com/lieferando-sales/)
- [getorder.biz – Lieferando API Integration](https://getorder.biz/lieferando/)
- [GeekyAnts – POS-Integrationskosten (US-Marktreferenz)](https://geekyants.com/en-us/blog/how-to-integrate-your-pos-system-with-uber-eats-doordash--grubhub-in-the-usa)
