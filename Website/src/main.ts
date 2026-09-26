import './styles.css';
import { api } from '@appdeploy/client';
import { mountTorSimulator } from './simulator';

type Lang = 'de' | 'tr';

type SiteData = {
  products: Array<{ name: string; price: string }>;
};

const app = document.querySelector<HTMLDivElement>('#app')!;
const demoUrl = 'https://tor-pos-trial-api-xifmg0.v2.appdeploy.ai/api/v1/trial/download';

let lang: Lang = localStorage.getItem('torpos-lang') === 'tr' ? 'tr' : 'de';
let loadedSiteData: SiteData | null = null;

const copy = {
  de: {
    access: 'Privater TOR POS Testzugang',
    hint: 'Diese Vorschau ist noch nicht öffentlich. Zugangscode eingeben.',
    code: 'Zugangscode',
    open: 'Vorschau öffnen',
    wrong: 'Zugangscode ist nicht korrekt.',
    logout: 'Abmelden',
    navFeatures: 'Funktionen',
    navEditions: 'Einzelhandel / Gastronomie',
    navHardware: 'Hardware',
    navSimulator: 'Online testen',
    navCloud: 'Cloud',
    navFaq: 'FAQ',
    navStatus: 'Status',
    eyebrow: 'TOR POS · Windows Kassensystem',
    title: 'Kassieren, bestellen, verwalten – ohne unnötige Umwege.',
    lead: 'TOR POS arbeitet Local-First: Kassieren, Artikel, Warenwirtschaft und Bestellungen bleiben lokal verfügbar – auch wenn die Internetverbindung ausfällt. Cloud-Dienste ergänzen das System und synchronisieren wieder, sobald die Verbindung zurück ist.',
    demo: '7 Tage kostenlos testen',
    onlineDemo: 'Online testen',
    simulatorEyebrow: 'INTERAKTIVE DEMO · KEINE INSTALLATION',
    simulatorTitle: 'TOR POS direkt im Browser testen',
    simulatorLead: 'Probieren Sie den Verkaufsablauf sofort aus: farbige Warengruppen, echte Demo-Artikel mit Preisen, Warenkorb, BAR/KARTE-Simulation und Gastronomie-Bestellungen – ohne Installation.',
    demoSmall: 'Demo herunterladen',
    demoNote: 'Einmalig 7 Tage pro Windows-PC. Eine Neuinstallation startet keine neue Demo.',
    learn: 'Funktionen ansehen',
    development: 'ENTWICKLUNGS-/VALIDIERUNGSSTAND',
    fiscalNotice: 'TSE, DSFinV-K und Belegprüfung sind technisch vorbereitet. Die fiskalische Produktivfreigabe bleibt bis zur realen Hardware- und Abschlussprüfung gesperrt.',
    editionsTitle: 'Für Einzelhandel und Gastronomie',
    kiosk: 'TOR POS EINZELHANDEL',
    kioskText: 'Schneller Verkauf mit Barcode, Scanner, Touch-Tasten, Warenwirtschaft, Bestand, Mindestbestand, Einkaufspreis, Inventur und Pfand.',
    retailSectors: ['Kiosk', 'Spätkauf', 'Lebensmittelgeschäft', 'Supermarkt', 'Minimarkt', 'Getränkemarkt', 'Blumenladen', 'Friseur / Barbershop', 'Schneiderei', 'Textilgeschäft', 'Handyshop', 'Schreibwaren', 'Tabak & Lotto', 'Geschenk- & Souvenirshop'],
    imbiss: 'TOR POS GASTRONOMIE',
    imbissText: 'Touch-orientierter Verkauf mit Varianten, Bestellungen, Abholnummern, Küchenablauf und separatem Bestellmonitor.',
    gastroSectors: ['Döner', 'Imbiss', 'Restaurant', 'Café', 'Bäckerei', 'Pizzeria', 'Bistro', 'Eiscafé', 'Bar', 'Foodtruck', 'Kantine', 'Catering', 'Burger', 'Sushi / Asia'],
    hardwareTitle: 'Eine Kasse, die zu Ihrer Hardware passt.',
    hardwareLead: 'TOR POS läuft auf Windows 10/11 x64 – auf klassischen Windows-Kassensystemen, All-in-One-Touch-PCs und geeigneten Windows-Tablets. Sie können vorhandene Hardware weiterverwenden oder einen modernen, kompakten Kassenplatz neu aufbauen.',
    hardwareCards: [
      ['Windows flexibel', 'Kassen-PC, All-in-One-Touchsystem oder geeignetes Windows-Tablet: TOR POS bleibt bei der Hardwarewahl flexibel.'],
      ['Kartenzahlung markenübergreifend', 'TOR POS führt die Terminaleinrichtung über einen Marken-Assistenten. PAYONE, CCV und freigegebene Sparkasse/S-Händlerservice-ZVT-Terminals können über ZVT/TCP konfiguriert werden; SumUp, myPOS, readyPay/readyMini, Zettle/iZettle und Flatpay werden mit ihrem tatsächlichen Integrationsstatus angezeigt und ohne freigegebenen Adapter nicht automatisch belastet.'],
      ['Weniger Kabel', 'Netzwerk- oder Wi-Fi-fähige Bondrucker und Kartenterminals können, sofern das jeweilige Gerät dies unterstützt, über das lokale Netz eingebunden werden.'],
      ['Sauberer Kassenplatz', 'Touchgerät, Bondrucker, Kartenterminal und optional Kundendisplay oder Bestellmonitor lassen sich kompakt kombinieren – mit weniger Datenkabeln auf der Theke.'],
      ['Offline kassieren', 'Fällt das Internet aus, bleibt der lokale Kassenbetrieb verfügbar. Cloud-Funktionen synchronisieren weiter, sobald die Verbindung zurück ist.'],
      ['Erweiterbar', 'Scanner, Kassenschublade, Küchen- oder A4-Drucker, Kundendisplay und Bestellmonitor können passend zum Betrieb ergänzt werden.']
    ],
    featuresTitle: 'Heute schon deutlich mehr als nur Kassieren',
    cloudTitle: 'TOR POS Cloud',
    cloudLead: 'Die Kasse ist nicht von TOR Cloud abhängig. Der operative Kassenbetrieb läuft lokal weiter; Cloud ergänzt Übersicht, Synchronisierung und digitale Dienste, sobald eine Verbindung verfügbar ist.',
    trialTitle: '7 Tage ausprobieren – mit fester Trial-ID',
    trialText: 'Die Demo startet beim ersten Online-Start. TOR POS verwendet eine zufällig erzeugte Trial-ID, die bei einer normalen Neuinstallation auf demselben Windows-PC erhalten bleibt. Hardwaredaten wie MachineGuid oder Laufwerksseriennummer werden nicht verwendet.',
    download: 'TOR POS Demo herunterladen',
    downloadHint: 'Windows 10/11 · Einzelhandel & Gastronomie · 7 Tage',
    statusTitle: 'Aktueller Projektstatus',
    ready: 'Vorhanden',
    validation: 'Validierung',
    locked: 'Gesperrt bis Abnahme',
    footer: 'Private Entwicklungsvorschau · noindex',
    faqTitle: 'Häufig gestellte Fragen',
    faqLead: 'Die wichtigsten Fragen zu Verkauf, Warenwirtschaft, Gastronomie, Fiskaltechnik, Cloud, Hardware und täglichem Betrieb – direkt beantwortet.',
    faqGroups: [
      {
        title: 'Kassieren & Bedienung',
        items: [
          ['Für welche Betriebe ist TOR POS gedacht?', 'TOR POS hat getrennte Arbeitsweisen für Einzelhandel und Gastronomie. Dazu gehören z. B. Kiosk, Spätkauf, Lebensmittelhandel, Getränkemarkt, Friseur, Döner, Imbiss, Restaurant, Café, Bäckerei, Pizzeria, Foodtruck und ähnliche Betriebe.'],
          ['Funktioniert die Kasse auch ohne Internet?', 'Ja. TOR POS ist Local-First aufgebaut. Verkauf, Artikel, Warenkorb, Bestellungen und lokale Daten bleiben verfügbar. Cloud-Funktionen synchronisieren weiter, sobald die Verbindung wieder vorhanden ist.'],
          ['Kann ich mit einem Barcode-Scanner direkt kassieren?', 'Ja. EAN-Barcodes können im Verkaufsfenster direkt gescannt werden. Ein gefundener Artikel wird in den Warenkorb übernommen; unbekannte EANs werden sichtbar gemeldet. Neu angelegte Artikel werden beim nächsten Scan automatisch erneut aus dem Katalog geladen.'],
          ['Kann TOR POS per Touch bedient werden?', 'Ja. Große Warengruppen, Artikeltasten, Schnellartikel, Nummernblock und klar getrennte Kassenfunktionen sind für Touch-Kassen und All-in-One-Systeme ausgelegt.'],
          ['Welche Zahlungsarten gibt es?', 'BAR, KARTE und GEMISCHT für geteilte Bar-/Kartenzahlungen sind vorgesehen. Ein einziger KASSIEREN-Button öffnet die Zahlungsseite; BAR, KARTE oder GEMISCHT wechseln dort nur den Detailbereich, ohne den Bediener auf eine zweite Zahlungsseite zu schicken.'],
          ['Welche Kartenterminals kann TOR POS einbinden?', 'TOR POS bündelt PAYONE, CCV, Sparkasse/S-Händlerservice, SumUp, myPOS, readyPay/readyMini, Zettle/iZettle und Flatpay in einem Marken-Assistenten. Für freigegebene PAYONE-, CCV- und Sparkasse/S-Händlerservice-ZVT-Terminals steht der ZVT/TCP-Weg bereit. SumUp hat einen getrennten Geräte-/Pairingpfad; myPOS, readyPay/readyMini, Zettle/iZettle und Flatpay bleiben bis zur offiziellen Partner-/API- bzw. Adapterfreigabe für automatische Belastungen deaktiviert. Damit wird ein gelisteter Hersteller nicht fälschlich als produktionsbereit dargestellt.'],
          ['Wie funktionieren AUSSER HAUS und IM HAUS?', 'In der Gastronomie wird die Verkaufsart pro Verkauf im Zahlungsbereich gewählt. AUSSER HAUS ist der sichere Standard für einen neuen Verkauf; IM HAUS kann bewusst ausgewählt werden. Die steuerliche Behandlung folgt der Artikel- und Menükonfiguration.']
        ]
      },
      {
        title: 'Artikel & Warenwirtschaft',
        items: [
          ['Welche Artikeldaten kann TOR POS verwalten?', 'Unter anderem Artikelnummer, EAN, SKU, Warengruppe, Einheit, Verkaufspreis, Steuersatz, Einkaufspreis, Bestand und Mindestbestand.'],
          ['Gibt es Import und Export für viele Artikel?', 'Ja. Artikel können gesammelt per CSV importiert und exportiert werden. Zusätzlich sind Datenaustausch- und TOR-Datenbank-Importpfade vorgesehen.'],
          ['Wie funktioniert die Inventur?', 'Die Inventur ist scanner-orientiert aufgebaut. Zählmengen können schnell erfasst werden; der Warenbestand und der Einkaufspreis ermöglichen zusätzlich eine Warenwert-Auswertung.'],
          ['Warnt das System bei niedrigem Bestand?', 'Ja. Für Artikel kann ein Mindestbestand hinterlegt werden. Niedrige Bestände werden in Warenwirtschaft und Cloud-Auswertungen sichtbar gemacht.'],
          ['Kann ein Artikel Varianten haben?', 'Ja. Varianten können einem Artikel zugeordnet und beim Verkauf ausgewählt werden. Dadurch lassen sich z. B. Größen, Ausführungen oder ähnliche Auswahlmöglichkeiten abbilden.'],
          ['Kann ich Menüs, Angebote, Rabatt, Pfand und Extras nutzen?', 'Ja. Gastronomie-Menüs können aus vorhandenen Artikeln zusammengestellt werden; auf dem Bon kann nur der Menüname erscheinen, während Bestand und Steueraufteilung intern aus den Komponenten berechnet werden. Zusätzlich unterstützt TOR POS Angebotszeiträume, nachvollziehbare Rabatte, Pfand und Extras.']
        ]
      },
      {
        title: 'Gastronomie, Bestellung & Bon',
        items: [
          ['Kann ich Bestellungen annehmen und gleichzeitig normal verkaufen?', 'Ja. Offene Bestellungen und normale Direktverkäufe sind getrennte Vorgänge, sodass der laufende Kassenbetrieb nicht für jede Bestellung blockiert werden muss.'],
          ['Gibt es Abholnummern?', 'Ja. Für Gastronomie-Bestellungen können fortlaufende Abhol- bzw. Bestellnummern verwendet werden, damit Kunde und Personal denselben Vorgang eindeutig erkennen.'],
          ['Gibt es einen separaten Bestellmonitor?', 'Ja. Ein eigener Bestellmonitor kann Nummern mit Zuständen wie „Wird vorbereitet“ und „Fertig“ anzeigen. Er ist getrennt vom normalen Kundendisplay.'],
          ['Kann ich Bons parken und später weiterbearbeiten?', 'Ja. Offene Vorgänge können geparkt und später wieder aufgenommen werden. Zusätzlich gibt es eine Übersicht offener Bestellungen und Vorgänge.'],
          ['Gibt es eine Bon-Historie?', 'Ja. Die Bon-Historie zeigt die Bons des aktuellen Tages. Berechtigte Benutzer können von dort aus vorgesehene Korrekturvorgänge wie Bon-Storno oder Teilretoure starten.'],
          ['Welche Bon-Funktionen gibt es?', 'TOR POS unterstützt Bondruck, konfigurierbare Firmendaten und Logo, Zahlart- und Steuerdarstellung sowie einen digitalen Bon. Für den digitalen Bon kann ein QR-Code zur Browseransicht und zum PDF-Abruf genutzt werden.']
        ]
      },
      {
        title: 'Berichte, DATEV & Fiskal',
        items: [
          ['Welche Berichte sind vorhanden?', 'Dazu gehören X-Bericht, Z-Bericht, Umsatzberichte, Monatsbericht, Verkaufsstatistik, Warenbestand, Kassenjournal, Kassensturz, Bedienerabrechnung und Stornobericht.'],
          ['Kann ich Einlagen und Entnahmen dokumentieren?', 'Ja. Kassenbewegungen wie Einlage und Entnahme werden getrennt erfasst und können im Kassenjournal und in passenden Auswertungen berücksichtigt werden.'],
          ['Kann TOR POS Daten für DATEV bereitstellen?', 'Ja. Ein DATEV-Kassenbuch-Export im Standard-ASCII/CSV-Pfad ist vorgesehen. Eine direkte Kassenarchiv-/API-Anbindung ist ein separater Integrationsweg und benötigt die jeweiligen DATEV-Vertrags-, Berechtigungs- und API-Voraussetzungen.'],
          ['Welche Fiskal- und Finanzamt-Exporte gibt es?', 'Vorgesehen sind unter anderem DSFinV-K-Export, TSE-Export, Buchungsdaten, GDPdU/GoBD-Werkzeuge, Programmierungsprotokoll, Kassenmeldung nach § 146a AO und Fiskal-Prüfpfade.'],
          ['Unterstützt TOR POS eine TSE?', 'Die Architektur ist für unterstützte TSE-Wege vorbereitet, darunter lokale Hardware- und Cloud-/Middleware-Szenarien. Die produktive fiskalische Freigabe erfolgt erst nach realer TSE-, DSFinV-K- und Abschlussprüfung; die aktuelle Website bleibt ausdrücklich eine Test-/Aufbauversion.'],
          ['Wie werden Korrekturen und kritische Vorgänge nachvollzogen?', 'TOR POS verwendet Berechtigungen und Audit-Protokolle für kritische Aktionen. Storno, Retouren, Zahlungszustände und weitere kontrollierte Vorgänge werden nicht nur als freie Bildschirmaktion behandelt, sondern nachvollziehbar geführt.']
        ]
      },
      {
        title: 'Cloud, Sicherheit, Backup & Hardware',
        items: [
          ['Was bietet TOR POS Cloud?', 'TOR Cloud ergänzt die lokale Kasse um synchronisierte Umsatz-, Bon-, Bestands-, Geräte- und Statusinformationen. Das Datenmodell ist auf Kunde, Filiale und Kasse ausgelegt und damit für mehrere Kassen bzw. Filialen vorbereitet.'],
          ['Ist die Kasse von der Cloud abhängig?', 'Nein. Cloud ist eine Ergänzung. Der operative Verkauf bleibt lokal möglich; nach einer Unterbrechung kann die Synchronisierung fortgesetzt werden.'],
          ['Kann TOR POS Berichte per E-Mail versenden?', 'Ja, der Berichtsversand ist vorgesehen. Je nach Bereitstellung kann ein zentral verwalteter TOR-Mail-Versand oder alternativ Google OAuth bzw. ein eigener SMTP-Ausgang genutzt werden.'],
          ['Wie werden Daten gesichert?', 'Es gibt eine automatische tägliche Datensicherung mit konfigurierbarer Uhrzeit sowie kontrollierte Wiederherstellungswege. Zusätzlich schützt die Kasse offene Vorgänge durch Recovery-Mechanismen gegen typische Abbruch- und Neustartsituationen.'],
          ['Welche Benutzer- und Sicherheitsfunktionen gibt es?', 'Neben dem Admin stehen in der aktuellen Desktop-Version drei Mitarbeiterkonten mit eigenen Funktionsrechten zur Verfügung. Anmeldung kann je nach Konfiguration über Passwort oder PIN erfolgen. Zusätzlich gibt es Audit-Logik, Schutz vor Doppelzahlungen und kontrollierte Zahlungs-/Recovery-Zustände.'],
          ['Wie richtet TOR POS Bondrucker und Kassenschublade ein?', 'Die R169 Drucker-Zentrale liest Windows-Druckername, Treiber und Port und erkennt geprüfte Epson- und Star-Bondrucker nur dann mit einem konkreten Modell, wenn der Modellname eindeutig ist. Die Auswahl bleibt ausdrücklich kontrollierbar. Über TESTBON DRUCKEN kann der gewählte Drucker geprüft werden. KASSENSCHUBLADE TESTEN sendet genau einen ESC-p/StarPRNT-Schubladenimpuls über den ausgewählten Bondrucker; dabei wird kein Verkauf und kein Bon erzeugt. Die mechanische Öffnung wird anschließend direkt am Gerät kontrolliert.'],
          ['Welche Hardware und welche Testmöglichkeit werden unterstützt?', 'TOR POS läuft auf Windows 10/11 x64 und ist für Touch-PCs, All-in-One-Kassen und geeignete Windows-Tablets ausgelegt. Scanner, Bondrucker, Kassenschublade, Kartenterminal, Kundendisplay und Bestellmonitor können passend zur Konfiguration ergänzt werden. Eine 7-Tage-Testversion ist für die Erprobung vorgesehen.']
        ]
      }
    ],
    featureItems: [
      ['Offline weiterarbeiten', 'Der lokale Kassenbetrieb bleibt auch ohne Internet verfügbar. Cloud-Synchronisierung wird nach Wiederherstellung der Verbindung fortgesetzt.'],
      ['Touch-Kasse', 'Große Artikeltasten, Scanner, Schnellartikel und klare Zahlungsarten.'],
      ['Ein-Schritt-Zahlung', 'Ein KASSIEREN-Button öffnet eine einzige Zahlungsseite. BAR, KARTE und GEMISCHT werden direkt darin gewählt – ohne doppelten Seitenwechsel.'],
      ['Kartenterminal-Zentrale', 'Marke auswählen statt Protokolle raten: PAYONE, CCV, Sparkasse/S-Händlerservice, SumUp, myPOS, readyPay/readyMini, Zettle/iZettle und Flatpay sind in einem Assistenten gebündelt. Automatische Zahlungen werden nur für technisch freigegebene Profile aktiviert.'],
      ['Drucker-Zentrale', 'R169 prüft Windows-Druckername, Treiber und Port und erkennt geprüfte Epson-/Star-Bondruckermodelle konservativ. Testbon und Kassenschubladen-Test helfen bei der Einrichtung; ein unklarer Modellname wird nicht automatisch geraten.'],
      ['Bestellungen', 'Bestellung annehmen und parallel normale Verkäufe weiterführen.'],
      ['Bestellmonitor', 'Separater Bildschirm mit „Wird vorbereitet“ und „Fertig“.'],
      ['Warenwirtschaft', 'Bestand, Mindestbestand, Einkaufspreis, Inventur und Warenwert.'],
      ['Angebote & Rabatte', 'Aktionszeiträume, Angebotslogik und nachvollziehbare Rabattdarstellung.'],
      ['Berichte & PDF', 'Tages-, Monats-, Waren-, Bediener- und Kassenberichte zentral verfügbar.'],
      ['Digitaler Bon', 'QR-Beleg mit Browseransicht und PDF-Download über getrennte Bon-Domain.'],
      ['Datensicherung', 'Tägliche automatische Sicherung plus kontrollierte Wiederherstellung.'],
      ['Cloud & E-Mail', 'Bestands-/Umsatzsync, Dashboard und Google-OAuth-basierter Berichtsversand.']
    ]
  },
  tr: {
    access: 'Özel TOR POS test erişimi',
    hint: 'Bu önizleme henüz herkese açık değildir. Erişim kodunu girin.',
    code: 'Erişim kodu',
    open: 'Önizlemeyi aç',
    wrong: 'Erişim kodu doğru değil.',
    logout: 'Çıkış',
    navFeatures: 'Özellikler',
    navEditions: 'Einzelhandel / Gastronomi',
    navSimulator: 'Online dene',
    navCloud: 'Cloud',
    navFaq: 'SSS',
    navStatus: 'Durum',
    eyebrow: 'TOR POS · Windows Kasa Sistemi',
    title: 'Satış, sipariş ve yönetim – gereksiz karmaşa olmadan.',
    lead: 'TOR POS Local-First çalışır: satış, ürünler, stok yönetimi ve siparişler internet kesilse bile yerel olarak kullanılmaya devam eder. Cloud servisleri bağlantı geri geldiğinde senkronizasyona devam eder.',
    demo: '7 Gün Ücretsiz Dene',
    onlineDemo: 'Online dene',
    simulatorEyebrow: 'İNTERAKTİF DEMO · KURULUM YOK',
    simulatorTitle: 'TOR POS’u doğrudan tarayıcıda deneyin',
    simulatorLead: 'Satış akışını hemen deneyin: renkli ürün grupları, fiyatlı demo ürünleri, sepet, NAKİT/KART simülasyonu ve Gastronomi siparişleri – kurulum gerektirmez.',
    demoSmall: 'Demo indir',
    demoNote: 'Her Windows PC için yalnız bir kez 7 gün. Yeniden kurulum yeni demo başlatmaz.',
    learn: 'Özellikleri gör',
    development: 'GELİŞTİRME / DOĞRULAMA DURUMU',
    fiscalNotice: 'TSE, DSFinV-K ve fiş kontrolleri teknik olarak hazırlanmıştır. Mali production kullanımı gerçek donanım ve son kabul testleri tamamlanana kadar kapalıdır.',
    editionsTitle: 'Einzelhandel ve Gastronomi için',
    kiosk: 'TOR POS EINZELHANDEL',
    kioskText: 'Barkod, scanner, dokunmatik tuşlar, stok yönetimi, minimum stok, alış fiyatı, sayım ve depo takibiyle hızlı satış.',
    retailSectors: ['Kiosk', 'Spätkauf', 'Market', 'Süpermarket', 'Mini market', 'İçecek marketi', 'Çiçekçi', 'Kuaför / Berber', 'Terzi', 'Tekstil mağazası', 'Telefon mağazası', 'Kırtasiye', 'Tütün & Lotto', 'Hediyelik eşya'],
    imbiss: 'TOR POS GASTRONOMIE',
    imbissText: 'Dokunmatik satış, varyantlar, siparişler, sıra numarası, mutfak akışı ve ayrı sipariş takip ekranı.',
    gastroSectors: ['Döner', 'Imbiss', 'Restaurant', 'Cafe', 'Fırın / Bäckerei', 'Pizzeria', 'Bistro', 'Dondurmacı', 'Bar', 'Foodtruck', 'Kantin', 'Catering', 'Burger', 'Sushi / Asia'],
    hardwareTitle: 'Donanımınıza uyum sağlayan bir kasa sistemi.',
    hardwareLead: 'TOR POS Windows 10/11 x64 üzerinde; klasik Windows kasa sistemlerinde, All-in-One dokunmatik bilgisayarlarda ve uygun Windows tabletlerde çalışır. Mevcut donanımla devam edebilir veya modern ve kompakt yeni bir kasa noktası kurabilirsiniz.',
    hardwareCards: [
      ['Windows esnekliği', 'Kasa PC, All-in-One dokunmatik sistem veya uygun Windows tablet: TOR POS donanım seçiminde esneklik sağlar.'],
      ['Markalar arası kart terminali', 'TOR POS terminal kurulumunu marka seçimiyle yönetir. PAYONE, CCV ve onaylı Sparkasse/S-Händlerservice ZVT terminalleri ZVT/TCP üzerinden yapılandırılabilir; SumUp, myPOS, readyPay/readyMini, Zettle/iZettle ve Flatpay gerçek entegrasyon durumlarıyla gösterilir ve onaylı adaptör olmadan otomatik ödeme başlatılmaz.'],
      ['Daha az kablo', 'Ağ veya Wi-Fi destekli fiş yazıcıları ve kart terminalleri, cihaz destekliyorsa yerel ağa kablosuz bağlanabilir.'],
      ['Şık kasa alanı', 'Dokunmatik cihaz, fiş yazıcısı, kart terminali ve isteğe bağlı müşteri ekranı veya sipariş monitörü kompakt biçimde kurulabilir; tezgahta daha az veri kablosu olur.'],
      ['Offline satış', 'İnternet kesilse bile yerel kasa çalışmaya devam eder. Cloud servisleri bağlantı geri geldiğinde senkronizasyona devam eder.'],
      ['Genişletilebilir', 'Barkod okuyucu, para çekmecesi, mutfak veya A4 yazıcı, müşteri ekranı ve sipariş monitörü işletmeye göre eklenebilir.']
    ],
    featuresTitle: 'Bugün yalnızca bir kasa programından çok daha fazlası',
    cloudTitle: 'TOR POS Cloud',
    cloudLead: 'Kasa TOR Cloud’a bağımlı değildir. Günlük kasa işlemleri yerel olarak devam eder; Cloud ise bağlantı olduğunda senkronizasyon, genel görünüm ve dijital servisleri tamamlar.',
    trialTitle: '7 gün dene – kalıcı Trial-ID ile',
    trialText: 'Demo ilk çevrimiçi açılışta başlar. TOR POS rastgele oluşturulan bir Trial-ID kullanır ve bu kimlik normal yeniden kurulumda aynı Windows PC’de korunur. MachineGuid veya disk seri numarası gibi donanım bilgileri kullanılmaz.',
    download: 'TOR POS Demo indir',
    downloadHint: 'Windows 10/11 · Einzelhandel & Gastronomi · 7 gün',
    statusTitle: 'Güncel proje durumu',
    ready: 'Mevcut',
    validation: 'Doğrulama',
    locked: 'Kabul testine kadar kapalı',
    footer: 'Özel geliştirme önizlemesi · noindex',
    faqTitle: 'Sık Sorulan Sorular',
    faqLead: 'Satış, stok yönetimi, gastronomi, mali işlemler, Cloud, donanım ve günlük kullanım hakkında en önemli soruların kısa ve net cevapları.',
    faqGroups: [
      {
        title: 'Satış & Kullanım',
        items: [
          ['TOR POS hangi işletmeler için uygun?', 'TOR POS Einzelhandel ve Gastronomi için ayrı çalışma yapıları sunar. Kiosk, Spätkauf, market, içecek marketi, kuaför, döner, imbiss, restaurant, café, fırın, pizzeria, foodtruck ve benzeri işletmeler için kullanılabilir.'],
          ['İnternet olmadan kasa çalışır mı?', 'Evet. TOR POS Local-First yapısındadır. Satış, ürünler, sepet, siparişler ve yerel veriler internet olmadan da kullanılabilir. Bağlantı geri geldiğinde Cloud senkronizasyonu devam eder.'],
          ['Satış ekranında barkod okuyucu kullanabilir miyim?', 'Evet. EAN barkodları doğrudan satış ekranında okutulabilir. Bulunan ürün sepete eklenir, bilinmeyen EAN açıkça bildirilir. Yeni eklenen ürünlerde katalog otomatik olarak tekrar yüklenir.'],
          ['Dokunmatik ekran için uygun mu?', 'Evet. Büyük ürün grupları, ürün tuşları, hızlı ürün, numara alanı ve kasa fonksiyonları dokunmatik POS sistemleri için tasarlanmıştır.'],
          ['Hangi ödeme türleri var?', 'NAKİT, KART ve nakit/kart bölünmüş ödeme için GEMISCHT bulunur. Tek KASSIEREN butonu ödeme ekranını açar; NAKİT, KART veya GEMISCHT seçildiğinde ikinci bir ödeme sayfasına geçmeden aynı ekranın ayrıntı bölümü değişir.'],
          ['Hangi kart terminali markalarını TOR POS yönetebilir?', 'TOR POS; PAYONE, CCV, Sparkasse/S-Händlerservice, SumUp, myPOS, readyPay/readyMini, Zettle/iZettle ve Flatpay seçeneklerini tek bir marka yardımcısında toplar. Onaylı PAYONE, CCV ve Sparkasse/S-Händlerservice ZVT terminalleri için ZVT/TCP yolu hazırdır. SumUp ayrı cihaz/pairing yolunda tutulur; myPOS, readyPay/readyMini, Zettle/iZettle ve Flatpay ise resmi partner/API veya adaptör onayı gelene kadar otomatik tahsilat için kapalı kalır. Böylece listede görünmek, yanlış biçimde “production hazır” anlamına gelmez.'],
          ['AUSSER HAUS ve IM HAUS nasıl çalışır?', 'Gastronomide satış türü ödeme alanında her satış için seçilir. Yeni satışta güvenli varsayılan AUSSER HAUS’tur; gerektiğinde IM HAUS seçilir. Vergi işlemi ürün ve menü ayarlarına göre yürütülür.']
        ]
      },
      {
        title: 'Ürün & Stok Yönetimi',
        items: [
          ['Hangi ürün bilgileri tutulabilir?', 'Ürün numarası, EAN, SKU, ürün grubu, birim, satış fiyatı, KDV, alış fiyatı, stok ve minimum stok gibi bilgiler yönetilebilir.'],
          ['Toplu ürün import/export var mı?', 'Evet. Ürünler CSV ile toplu içe ve dışa aktarılabilir. Ayrıca veri aktarımı ve TOR veritabanından import yolları da öngörülmüştür.'],
          ['Sayım nasıl yapılır?', 'Inventur scanner odaklıdır. Sayım miktarları hızlı girilebilir; stok ve alış fiyatı verileriyle toplam stok maliyeti hesaplanabilir.'],
          ['Düşük stok uyarısı var mı?', 'Evet. Ürünlere minimum stok tanımlanabilir. Düşük stoklar hem stok yönetiminde hem Cloud tarafındaki özetlerde görülebilir.'],
          ['Ürün varyantları destekleniyor mu?', 'Evet. Ürüne varyantlar atanabilir ve satış sırasında seçim yapılabilir; böylece boyut, tür veya benzeri seçenekler yönetilebilir.'],
          ['Menü, kampanya, indirim, depozito ve ekstra var mı?', 'Evet. Gastronomi menüleri mevcut ürünlerden oluşturulabilir; fişte yalnız menü adı gösterilirken stok ve vergi dağılımı içeriklere göre hesaplanabilir. Kampanya dönemleri, izlenebilir indirim, Pfand ve Extra işlemleri de desteklenir.']
        ]
      },
      {
        title: 'Gastronomi, Sipariş & Fiş',
        items: [
          ['Sipariş alırken normal satış yapmaya devam edebilir miyim?', 'Evet. Açık siparişler ile doğrudan satışlar ayrı işlemlerdir; sipariş alınırken normal kasa satışı devam edebilir.'],
          ['Sipariş numarası var mı?', 'Evet. Gastronomi siparişlerinde müşteri ve personelin aynı işlemi takip edebilmesi için sıra/abhol numarası kullanılabilir.'],
          ['Ayrı sipariş takip ekranı var mı?', 'Evet. Ayrı bir Bestellmonitor “Hazırlanıyor” ve “Hazır” durumlarını gösterebilir. Bu ekran normal müşteri ekranından ayrıdır.'],
          ['Fişi park edip sonra devam edebilir miyim?', 'Evet. Açık fişler park edilebilir ve daha sonra tekrar açılabilir. Açık sipariş ve işlemler için ayrı genel görünüm de bulunur.'],
          ['Fiş geçmişi var mı?', 'Evet. Gün içindeki fişler Bon-Historie içinde görülebilir. Yetkili kullanıcılar buradan fiş iptali veya kısmi iade gibi tanımlı düzeltme işlemlerini başlatabilir.'],
          ['Fiş tarafında hangi özellikler var?', 'Fiş yazdırma, firma bilgileri ve logo ayarı, ödeme/KDV gösterimi ve dijital fiş desteklenir. Dijital fiş için QR ile tarayıcı görünümü ve PDF erişimi kullanılabilir.']
        ]
      },
      {
        title: 'Raporlar, DATEV & Mali',
        items: [
          ['Hangi raporlar var?', 'X ve Z raporu, ciro raporları, aylık rapor, ürün satış istatistiği, stok raporu, kasa günlüğü, kasa sayımı, personel hesabı ve iptal raporu bulunur.'],
          ['Kasaya para giriş/çıkışı kaydedilebilir mi?', 'Evet. Einlage ve Entnahme ayrı kasa hareketi olarak kaydedilir ve kasa günlüğü ile ilgili raporlarda dikkate alınabilir.'],
          ['DATEV için veri çıkarabilir miyim?', 'Evet. DATEV Kassenbuch için Standard-ASCII/CSV export yolu hazırlanmıştır. Doğrudan Kassenarchiv/API bağlantısı ayrı bir entegrasyondur ve ilgili DATEV sözleşme, yetki ve API şartlarını gerektirir.'],
          ['Finanzamt için hangi exportlar var?', 'DSFinV-K Export, TSE Export, muhasebe verileri, GDPdU/GoBD araçları, programlama protokolü, § 146a AO kasa bildirimi ve mali kontrol yolları öngörülmüştür.'],
          ['TSE desteği var mı?', 'Mimari desteklenen fiziksel TSE ve Cloud/Middleware senaryolarına hazırlanmıştır. Gerçek production mali kullanımı ancak gerçek TSE, DSFinV-K ve son kabul kontrollerinden sonra açılır; web sitesi şu anda açıkça test/kurulum aşamasındadır.'],
          ['İptal ve kritik işlemler izlenebilir mi?', 'Evet. Kritik işlemler kullanıcı yetkileri ve audit mantığıyla kontrol edilir. İptal, iade, ödeme durumu ve kontrollü işlemler yalnızca ekrandaki serbest bir işlem olarak bırakılmaz.']
        ]
      },
      {
        title: 'Cloud, Güvenlik, Yedek & Donanım',
        items: [
          ['TOR POS Cloud ne sağlar?', 'TOR Cloud yerel kasaya ciro, fiş, stok, cihaz ve durum senkronizasyonu ekler. Veri modeli müşteri, şube ve kasa yapısına göre tasarlandığı için birden fazla kasa ve şubeye hazırdır.'],
          ['Kasa Cloud’a bağlı olmak zorunda mı?', 'Hayır. Cloud yardımcı katmandır. Satış yerel olarak çalışır; bağlantı kesildikten sonra senkronizasyon tekrar devam edebilir.'],
          ['Raporlar e-posta ile gönderilebilir mi?', 'Evet, rapor gönderimi için altyapı vardır. Kuruluma göre merkezi TOR Mail, Google OAuth veya işletmenin kendi SMTP çıkışı kullanılabilir.'],
          ['Yedekleme nasıl çalışır?', 'Günlük otomatik yedekleme ve ayarlanabilir yedek saati vardır. Kontrollü geri yükleme yolları bulunur; ayrıca açık satışları tipik kapanma/yeniden başlatma durumlarına karşı koruyan recovery mekanizmaları kullanılır.'],
          ['Kullanıcı ve güvenlik özellikleri neler?', 'Admin dışında güncel masaüstü sürümünde üç çalışan hesabı ve ayrı yetkiler bulunur. Kuruluma göre parola veya PIN ile giriş yapılabilir. Audit, çift ödeme koruması ve kontrollü ödeme/recovery durumları da sistemin parçasıdır.'],
          ['TOR POS fiş yazıcısını ve para çekmecesini nasıl kurar?', 'R169 Yazıcı Merkezi Windows yazıcı adı, sürücü ve port bilgisini okur; test edilmiş Epson ve Star fiş yazıcılarında somut model yalnız model adı açıkça tanındığında gösterilir. Seçim kullanıcı kontrolünde kalır. TESTBON DRUCKEN ile seçilen yazıcı sınanabilir. KASSENSCHUBLADE TESTEN seçili fiş yazıcısı üzerinden yalnız bir ESC-p/StarPRNT çekmece darbesi gönderir; satış veya fiş oluşturmaz. Çekmecenin fiziksel olarak açıldığı cihaz üzerinde kontrol edilir.'],
          ['Hangi donanımlar ve deneme sürümü destekleniyor?', 'TOR POS Windows 10/11 x64 üzerinde Touch-PC, All-in-One kasa ve uygun Windows tabletlerde çalışır. Barkod okuyucu, fiş yazıcısı, para çekmecesi, kart terminali, müşteri ekranı ve sipariş monitörü kuruluma göre eklenebilir. Sistemi denemek için 7 günlük test sürümü öngörülmüştür.']
        ]
      }
    ],
    featureItems: [
      ['İnternetsiz çalışmaya devam', 'Yerel kasa işlemleri internet olmasa da kullanılabilir. Bağlantı geri geldiğinde Cloud senkronizasyonu devam eder.'],
      ['Dokunmatik kasa', 'Büyük ürün tuşları, barkod okuyucu, hızlı ürün ve net ödeme seçenekleri.'],
      ['Tek adımda ödeme', 'Tek KASSIEREN butonu bir ödeme ekranı açar. NAKİT, KART ve GEMISCHT aynı ekran içinde seçilir; gereksiz ikinci sayfa yoktur.'],
      ['Kart terminali merkezi', 'Protokol tahmin etmek yerine marka seçilir: PAYONE, CCV, Sparkasse/S-Händlerservice, SumUp, myPOS, readyPay/readyMini, Zettle/iZettle ve Flatpay tek kurulum yardımcısında toplanır. Otomatik ödeme yalnız teknik olarak onaylanmış profillerde açılır.'],
      ['Yazıcı Merkezi', 'R169 Windows yazıcı adı, sürücü ve port bilgisini kontrol eder; test edilmiş Epson/Star fiş yazıcısı modellerini temkinli biçimde tanır. Test fişi ve para çekmecesi testi kurulumu kolaylaştırır; belirsiz model adı otomatik olarak tahmin edilmez.'],
      ['Siparişler', 'Sipariş alırken aynı anda normal satışa devam et.'],
      ['Sipariş ekranı', '“Hazırlanıyor” ve “Hazır” için ayrı müşteri ekranı.'],
      ['Stok yönetimi', 'Stok, minimum stok, alış fiyatı, sayım ve stok değeri.'],
      ['Kampanya & indirim', 'Tarih aralıklı teklifler ve izlenebilir indirim mantığı.'],
      ['Raporlar & PDF', 'Günlük, aylık, stok, personel ve kasa raporları.'],
      ['Dijital fiş', 'QR ile tarayıcıda fiş görüntüleme ve PDF indirme.'],
      ['Otomatik yedek', 'Günlük otomatik yedekleme ve kontrollü geri yükleme.'],
      ['Cloud & e-posta', 'Stok/ciro senkronu, dashboard ve Google OAuth ile rapor gönderimi.']
    ]
  }
} as const;

function t() {
  return copy[lang];
}

function languageButtons() {
  return `
    <button class="lang ${lang === 'de' ? 'active' : ''}" data-lang="de">DE</button>
    <button class="lang ${lang === 'tr' ? 'active' : ''}" data-lang="tr">TR</button>
  `;
}

function bindLanguage(renderAgain: () => void) {
  document.querySelectorAll<HTMLButtonElement>('[data-lang]').forEach((button) => {
    button.addEventListener('click', () => {
      lang = button.dataset.lang as Lang;
      localStorage.setItem('torpos-lang', lang);
      renderAgain();
    });
  });
}

function renderLogin() {
  document.body.classList.remove('simulator-only-mode');
  const text = t();
  app.innerHTML = `
    <main class="lock">
      <section class="login-card">
        <div class="logo-mark">T</div>
        <div class="private-chip">PRIVATE PREVIEW</div>
        <h1>${text.access}</h1>
        <p class="muted">${text.hint}</p>
        <form id="login-form">
          <input id="access-code" class="code" type="password" autocomplete="current-password" placeholder="${text.code}" required>
          <button class="primary" type="submit">${text.open}</button>
          <div id="login-error" class="error" role="alert"></div>
        </form>
        <div class="language-row">${languageButtons()}</div>
      </section>
    </main>
  `;

  bindLanguage(renderLogin);

  document.querySelector<HTMLFormElement>('#login-form')!.addEventListener('submit', async (event) => {
    event.preventDefault();
    const code = document.querySelector<HTMLInputElement>('#access-code')!.value;
    const error = document.querySelector<HTMLDivElement>('#login-error')!;
    error.textContent = '';

    try {
      const response = await api.post('/api/unlock', { code });
      if (!response.data?.token) {
        error.textContent = text.wrong;
        return;
      }

      sessionStorage.setItem('torpos-preview-token', response.data.token);
      await loadSite();
    } catch {
      error.textContent = text.wrong;
    }
  });
}

function featureCards(items: readonly (readonly [string, string])[]) {
  return items
    .map(([title, description], index) => `
      <article class="feature-card">
        <span class="feature-no">${String(index + 1).padStart(2, '0')}</span>
        <h3>${title}</h3>
        <p>${description}</p>
      </article>
    `)
    .join('');
}

function faqGroupCards(groups: readonly { readonly title: string; readonly items: readonly (readonly [string, string])[] }[]) {
  return groups.map((group) => `
    <article class="faq-group">
      <div class="faq-group-head">
        <span>${group.title}</span>
        <small>${group.items.length} ${lang === 'de' ? 'Fragen' : 'soru'}</small>
      </div>
      <div class="faq-list">
        ${group.items.map(([question, answer]) => `
          <details class="faq-item">
            <summary><span>${question}</span><b class="faq-toggle" aria-hidden="true"></b></summary>
            <p>${answer}</p>
          </details>
        `).join('')}
      </div>
    </article>
  `).join('');
}

function renderSimulatorPage(data: SiteData) {
  const text = t();
  document.body.classList.add('simulator-only-mode');

  app.innerHTML = `
    <main class="simulator-page">
      <div class="simulator-test-strip">${lang === 'de' ? 'TEST- / AUFBAUPHASE · KEIN VERKAUF · KEINE BESTELLANNAHME · KEINE ECHTE TSE- ODER KARTENTRANSAKTION' : 'TEST / KURULUM AŞAMASI · SATIŞ YOK · SİPARİŞ ALINMIYOR · GERÇEK TSE VEYA KART İŞLEMİ YOK'}</div>
      <div class="simulator-page-bar">
        <a class="simulator-back" href="#top">← ${lang === 'de' ? 'Website' : 'Siteye dön'}</a>
        <div class="simulator-page-title"><span class="simulator-brand-logo" role="img" aria-label="TOR POS Logo"></span><strong>TOR POS · ${text.navSimulator}</strong></div>
        <div class="simulator-page-langs">${languageButtons()}</div>
      </div>
      <div class="simulator-page-stage">
        <div id="tor-simulator"></div>
      </div>
    </main>
  `;

  bindLanguage(() => renderSimulatorPage(data));
  mountTorSimulator('tor-simulator', lang);
}

function renderCurrentView(data: SiteData) {
  if (window.location.hash === '#/simulator') {
    renderSimulatorPage(data);
    return;
  }
  renderSite(data);
}

function renderSite(data: SiteData) {
  document.body.classList.remove('simulator-only-mode');
  const text = t();

  app.innerHTML = `
    <div class="public-test-banner"><strong>${lang === 'de' ? 'TEST- / AUFBAUPHASE' : 'TEST / KURULUM AŞAMASI'}</strong><span>${lang === 'de' ? 'Diese Website befindet sich im Aufbau. Derzeit kein Verkauf, keine Bestellannahme, keine Verträge und keine kostenpflichtigen Leistungen.' : 'Bu site test ve kurulum aşamasındadır. Şu anda satış, sipariş, sözleşme veya ücretli hizmet kabul edilmemektedir.'}</span></div>
    <header class="topbar">
      <a class="brand" href="#top" aria-label="TOR POS">
        <span class="brand-logo" role="img" aria-label="TOR POS Logo"></span>
        <span>TOR POS</span>
      </a>
      <nav>
        <a href="#editions">${text.navEditions}</a>
        <a href="#/simulator">${text.navSimulator}</a>
        <a href="#hardware">${text.navHardware}</a>
        <a href="#features">${text.navFeatures}</a>
        <a href="#cloud">${text.navCloud}</a>
        <a href="#faq">${text.navFaq}</a>
        <a href="#status">${text.navStatus}</a>
      </nav>
      <div class="top-actions">
        ${languageButtons()}
        <a class="demo-mini" href="${demoUrl}">${lang === 'de' ? 'Testversion herunterladen' : 'Test sürümünü indir'}</a>
        <span class="test-chip">${lang === 'de' ? 'TEST / AUFBAU' : 'TEST / KURULUM'}</span>
      </div>
    </header>

    <main id="top">
      <section class="hero">
        <div class="hero-copy">
          <div class="eyebrow">${text.eyebrow}</div>
          <h1>${text.title}</h1>
          <p class="hero-lead">${text.lead}</p>
          <div class="sales-disabled-notice" role="note">
            <strong>${lang === 'de' ? 'WICHTIGER HINWEIS' : 'ÖNEMLİ BİLGİ'}</strong>
            <span>${lang === 'de'
              ? 'TOR POS und diese Website befinden sich derzeit in der Test- und Aufbauphase. Wir nehmen aktuell keine Käufe, Bestellungen oder kostenpflichtigen Aufträge entgegen und schließen über diese Website keine Verträge ab. Alle dargestellten Preise, Produkte und Abläufe dienen ausschließlich Demonstrations- und Testzwecken.'
              : 'TOR POS ve bu web sitesi şu anda test ve kurulum aşamasındadır. Şu anda satın alma, sipariş veya ücretli iş kabul etmiyoruz ve bu site üzerinden sözleşme yapılmamaktadır. Gösterilen tüm fiyatlar, ürünler ve işlemler yalnızca demo ve test amaçlıdır.'}</span>
          </div>
          <div class="hero-actions">
            <a class="cta" href="#/simulator">${text.onlineDemo}<span>→</span></a>
            <a class="secondary-link" href="${demoUrl}">${lang === 'de' ? 'Testversion herunterladen' : 'Test sürümünü indir'}</a>
            <a class="secondary-link" href="#features">${text.learn}</a>
          </div>
          <p class="demo-note">${text.demoNote}</p>
          <div class="hero-hardware-chips">
            <span>Windows 10/11</span>
            <span>Touch POS</span>
            <span>Windows Tablet</span>
            <span>Kartenterminal</span>
            <span>Multi-Terminal Center</span>
            <span>Netzwerk / Wi-Fi</span>
          </div>
          <div class="offline-highlight">
            <div class="offline-icon">↯</div>
            <div>
              <strong>${lang === 'de' ? 'OFFLINE-FÄHIG · LOCAL-FIRST' : 'OFFLINE ÇALIŞIR · LOCAL-FIRST'}</strong>
              <span>${lang === 'de' ? 'Internet weg? Der Kassenbetrieb bleibt lokal verfügbar. TOR Cloud synchronisiert weiter, sobald die Verbindung zurück ist.' : 'İnternet kesildi mi? Kasa işlemleri yerel olarak devam eder. Bağlantı geri geldiğinde TOR Cloud senkronizasyona devam eder.'}</span>
            </div>
          </div>
          <div class="fiscal-note">
            <strong>${text.development}</strong>
            <span>${text.fiscalNotice}</span>
          </div>
        </div>

        <div class="hero-showcase">
          <div class="hero-logo-card">
            <span class="hero-logo-image" role="img" aria-label="TOR POS Logo"></span>
            <div class="hero-logo-meta">
              <span>TOR POS</span>
              <strong>${lang === 'de' ? 'Modern. Lokal. Erweiterbar.' : 'Modern. Yerel. Genişletilebilir.'}</strong>
              <small>${lang === 'de' ? 'Windows Kassensystem für Einzelhandel & Gastronomie' : 'Einzelhandel ve Gastronomi için Windows kasa sistemi'}</small>
            </div>
          </div>
          <div class="hero-proof-grid">
            <div><b>LOCAL-FIRST</b><span>${lang === 'de' ? 'Offline kassieren' : 'Offline satış'}</span></div>
            <div><b>TOR CLOUD</b><span>${lang === 'de' ? 'Sync & Berichte' : 'Senkron & raporlar'}</span></div>
            <div><b>DATEV</b><span>${lang === 'de' ? 'Export vorbereitet' : 'Export hazır'}</span></div>
            <div><b>WINDOWS</b><span>${lang === 'de' ? 'Touch & Scanner' : 'Touch & scanner'}</span></div>
          </div>
          <div class="terminal-shell" aria-label="TOR POS cashier preview">
          <div class="terminal-top">
            <div><span class="dot"></span> TOR POS</div>
            <strong>KASSE 1 · EINZELHANDEL</strong>
          </div>
          <div class="terminal-body">
            <div class="product-grid">
              ${data.products.slice(0, 6).map((product) => `
                <div class="product">
                  <strong>${product.name}</strong>
                  <span>${product.price}</span>
                </div>
              `).join('')}
            </div>
            <div class="basket">
              <div class="basket-label">BON #1048</div>
              <div class="basket-line"><span>3 Positionen</span><strong>19,30 €</strong></div>
              <div class="total">19,30 €</div>
              <div class="pay-grid">
                <div class="checkout">KASSIEREN</div>
                <div class="order">BESTELLUNG</div>
              </div>
              <div class="terminal-state">LOCAL-FIRST · OFFLINE BEREIT · BACKUP BEREIT</div>
            </div>
          </div>
        </div>
        </div>
      </section>

      <section class="section editions" id="editions">
        <div class="section-heading">
          <div class="eyebrow">2 BRANCHENWELTEN · 1 PLATTFORM</div>
          <h2>${text.editionsTitle}</h2>
        </div>
        <div class="edition-grid">
          <article class="edition-card">
            <div class="edition-label">EINZELHANDEL</div>
            <h3>${text.kiosk}</h3>
            <p>${text.kioskText}</p>
            <div class="sector-title">${lang === 'de' ? 'Geeignet z. B. für' : 'Örneğin şu işletmeler için'}</div>
            <div class="chips sector-chips">${text.retailSectors.map((sector) => `<span>${sector}</span>`).join('')}</div>
          </article>
          <article class="edition-card warm">
            <div class="edition-label">GASTRONOMIE</div>
            <h3>${text.imbiss}</h3>
            <p>${text.imbissText}</p>
            <div class="sector-title">${lang === 'de' ? 'Geeignet z. B. für' : 'Örneğin şu işletmeler için'}</div>
            <div class="chips sector-chips">${text.gastroSectors.map((sector) => `<span>${sector}</span>`).join('')}</div>
          </article>
        </div>
      </section>

      <section class="section hardware-section" id="hardware">
        <div class="section-heading">
          <div class="eyebrow">${lang === 'de' ? 'HARDWARE · FLEXIBEL AUF WINDOWS' : 'DONANIM · WINDOWS ÜZERİNDE ESNEK'}</div>
          <h2>${text.hardwareTitle}</h2>
          <p class="section-lead">${text.hardwareLead}</p>
        </div>
        <div class="hardware-grid">
          ${text.hardwareCards.map(([title, description]) => `
            <article class="hardware-card">
              <div class="hardware-check">✓</div>
              <h3>${title}</h3>
              <p>${description}</p>
            </article>
          `).join('')}
        </div>
        <div class="hardware-note">
          <strong>${lang === 'de' ? 'MODERNER KASSENPLATZ' : 'MODERN KASA NOKTASI'}</strong>
          <span>${lang === 'de'
            ? 'Ein kompaktes Touchgerät plus Bondrucker und Kartenterminal kann für viele Betriebe bereits der komplette Arbeitsplatz sein. Netzwerkfähige Komponenten reduzieren – sofern vom jeweiligen Gerät unterstützt – zusätzliche Datenkabel.'
            : 'Birçok işletme için kompakt bir dokunmatik cihaz, fiş yazıcısı ve kart terminali tam bir kasa alanı oluşturabilir. Ağ destekli cihazlar, cihazın özelliğine bağlı olarak ek veri kablolarını azaltır.'}</span>
        </div>
      </section>

      <section class="section dark-section" id="features">
        <div class="section-heading">
          <div class="eyebrow">TOR POS FUNKTIONEN</div>
          <h2>${text.featuresTitle}</h2>
        </div>
        <div class="feature-grid">${featureCards(text.featureItems)}</div>
      </section>

      <section class="section cloud-section" id="cloud">
        <div>
          <div class="eyebrow">LOCAL FIRST · CLOUD OPTIONAL</div>
          <h2>${text.cloudTitle}</h2>
          <p class="section-lead">${text.cloudLead}</p>
          <div class="cloud-points">
            <span>Bestand & Warenwert</span>
            <span>Umsatzübersicht</span>
            <span>Digitalbon QR/PDF</span>
            <span>2FA</span>
            <span>Google OAuth</span>
            <span>Update Relay</span>
          </div>
        </div>
        <div class="cloud-panel">
          <div class="cloud-metric"><span>HEUTE</span><strong>1.842,60 €</strong></div>
          <div class="cloud-metric"><span>NIEDRIGER BESTAND</span><strong>7 Artikel</strong></div>
          <div class="cloud-metric"><span>KASSE 1</span><strong class="ok">ONLINE</strong></div>
          <div class="cloud-bars"><i style="height:34%"></i><i style="height:51%"></i><i style="height:44%"></i><i style="height:78%"></i><i style="height:64%"></i><i style="height:92%"></i><i style="height:70%"></i></div>
        </div>
      </section>

      <section class="section faq-section" id="faq">
        <div class="section-heading">
          <div class="eyebrow">FAQ · TOR POS</div>
          <h2>${text.faqTitle}</h2>
          <p class="section-lead">${text.faqLead}</p>
        </div>
        <div class="faq-grid">${faqGroupCards(text.faqGroups)}</div>
      </section>

      <section class="trial-section">
        <div>
          <div class="eyebrow">TOR POS DEMO</div>
          <h2>${text.trialTitle}</h2>
          <p>${text.trialText}</p>
        </div>
        <div class="trial-action">
          <a class="cta large" href="${demoUrl}">${lang === 'de' ? 'Testversion herunterladen' : 'Test sürümünü indir'}<span>↓</span></a>
          <small>${lang === 'de' ? 'Nur zu Testzwecken · kein Kauf · keine Bestellung · Windows 10/11' : 'Yalnızca test amaçlı · satış yok · sipariş yok · Windows 10/11'}</small>
        </div>
      </section>

      <section class="section" id="status">
        <div class="section-heading">
          <div class="eyebrow">TRANSPARENT ENTWICKELT</div>
          <h2>${text.statusTitle}</h2>
        </div>
        <div class="status-list">
          <div><span class="badge ready">${text.ready}</span><b>Einzelhandel / Gastronomie · Verkauf · Bestellung · Warenwirtschaft</b></div>
          <div><span class="badge ready">${text.ready}</span><b>Cloud-Sync · Digitalbon · PDF · Backup · Google OAuth</b></div>
          <div><span class="badge validation">${text.validation}</span><b>7-Tage-Demo · Trial-ID · Signierter Downloadkanal</b></div>
          <div><span class="badge locked">${text.locked}</span><b>TSE / DSFinV-K fiskalische Produktivfreigabe</b></div>
        </div>
      </section>
    </main>

    <footer>
      <div class="brand"><span class="logo-mark small">T</span><span>TOR POS</span></div>
      <span>${lang === 'de' ? 'Test- / Aufbauphase · Kein Verkauf · Keine Bestellannahme' : 'Test / kurulum aşaması · Satış yok · Sipariş alınmıyor'}</span>
      <span>torpos.de</span>
    </footer>
  `;

  bindLanguage(() => renderSite(data));
}

async function loadSite() {
  try {
    const response = await api.post('/api/site', {});
    if (!response.data?.products) throw new Error('invalid site payload');
    loadedSiteData = response.data as SiteData;
    renderCurrentView(loadedSiteData);
  } catch {
    document.body.classList.remove('simulator-only-mode');
    app.innerHTML = '<main class="public-load-error"><strong>TOR POS Testversion</strong><span>Die Testseite konnte nicht geladen werden. Bitte versuchen Sie es später erneut.</span></main>';
  }
}

window.addEventListener('hashchange', () => {
  if (!loadedSiteData) return;
  const wantsSimulator = window.location.hash === '#/simulator';
  const isSimulator = document.body.classList.contains('simulator-only-mode');
  if (wantsSimulator !== isSimulator) renderCurrentView(loadedSiteData);
});

void loadSite();
