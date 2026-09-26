import './simulator-match.css';

export type SimulatorLang = 'de' | 'tr';

type Mode = 'retail' | 'gastro';
type View = 'categories' | 'products';
type Screen =
  | 'cashier'
  | 'settings'
  | 'waren'
  | 'artikel'
  | 'inventur'
  | 'exchange'
  | 'kasse'
  | 'orders'
  | 'receiptHistory'
  | 'zreport'
  | 'cashMovement'
  | 'reports'
  | 'xreport'
  | 'cashCount'
  | 'cashJournal'
  | 'turnover'
  | 'monthly'
  | 'salesStats'
  | 'inventoryReport'
  | 'operatorReport'
  | 'stornoReport'
  | 'cloud';
type CloudTab = 'dashboard' | 'reports' | 'devices';
type Panel = 'payChoice' | 'cash' | 'card' | 'mixed' | 'search' | null;

type Product = {
  id: string;
  name: string;
  price: number;
};

type Category = {
  id: string;
  name: string;
  letter: string;
  color: string;
  products: Product[];
};

type SimulatorContract = {
  schemaVersion: number;
  publicEditions: {
    retail: { labelDe: string; labelTr: string };
    gastro: { labelDe: string; labelTr: string };
  };
  desktopUi: {
    topMenu: string[];
    statusBadges: string[];
    quickActions: {
      barcode: string;
      search: string;
      quickItem: string;
      discount: string;
      openOrders: string;
      receiptOn: string;
      checkout: string;
    };
    cashier: {
      currentSale: string;
      cash: string;
      card: string;
      extra: string;
      order: string;
    };
  };
  theme: {
    background: string;
    header: string;
    panel: string;
    productTile: string;
    productBorder: string;
    accent: string;
    cash: string;
    card: string;
  };
  demoCatalogs: Record<Mode, Category[]>;
  cloudPortal?: CloudPortalLabels;
};

// TOR Cloud customer portal preview. The labels come from the shared contract
// (checked in CI against Cloud/public); the numbers come only from this
// simulator session. Nothing is ever sent to TOR Cloud.
type CloudPortalLabels = {
  title: string;
  menu: string[];
  reports: { turnover: string; zArchive: string; cashMovements: string };
  deviceStatus: { mode: string; backlog: string; rejected: string; certificate: string };
  previewNote: string;
};

const fallbackCloudPortal: CloudPortalLabels = {
  title: 'TOR POS Cloud – Kundenportal',
  menu: ['Dashboard', 'Verkäufe', 'Bons', 'Berichte', 'Artikel', 'Warenbestand', 'Mitarbeiter', 'Filialen & Kassen', 'Gerätestatus', 'Sicherheit', 'Support'],
  reports: { turnover: 'Umsatz im Zeitraum', zArchive: 'Z-Bericht Archiv', cashMovements: 'Einlagen und Entnahmen' },
  deviceStatus: { mode: 'Betriebsart', backlog: 'Wartende Daten', rejected: 'Von der Cloud abgelehnt', certificate: 'TSE-Zertifikat bis' },
  previewNote: 'Vorschau mit den Daten dieser Simulator-Sitzung. Keine Verbindung zu TOR Cloud, keine echten Geschäftsdaten.'
};

function validCloudPortal(value: unknown): value is CloudPortalLabels {
  const v = value as CloudPortalLabels | undefined;
  return !!v && typeof v.title === 'string' && Array.isArray(v.menu) && v.menu.length >= 3 &&
    v.menu.every((item) => typeof item === 'string') &&
    typeof v.reports?.turnover === 'string' && typeof v.reports?.zArchive === 'string' && typeof v.reports?.cashMovements === 'string' &&
    typeof v.deviceStatus?.mode === 'string' && typeof v.deviceStatus?.backlog === 'string' &&
    typeof v.deviceStatus?.rejected === 'string' && typeof v.deviceStatus?.certificate === 'string' &&
    typeof v.previewNote === 'string';
}

const SIMULATOR_CONTRACT_URL =
  'https://raw.githubusercontent.com/min2dit-jpg/TOR-POS/main/Shared/simulator-contract.json';

async function loadSimulatorContract(): Promise<SimulatorContract | null> {
  try {
    const response = await fetch(SIMULATOR_CONTRACT_URL, { cache: 'no-store' });
    if (!response.ok) return null;
    const value = await response.json() as SimulatorContract;
    if (value.schemaVersion !== 1) return null;
    if (!Array.isArray(value.demoCatalogs?.gastro) || !Array.isArray(value.demoCatalogs?.retail)) return null;
    if (value.demoCatalogs.gastro.length === 0 || value.demoCatalogs.retail.length === 0) return null;
    return value;
  } catch {
    return null;
  }
}

type CartLine = Product & {
  qty: number;
};

type Receipt = {
  kind: 'sale' | 'order';
  number: number;
  lines: CartLine[];
  subtotal: number;
  discountPct: number;
  total: number;
  payment?: 'BAR' | 'KARTE' | 'GEMISCHT';
  imHaus?: boolean;
  cashGiven?: number;
  change?: number;
  cashPortion?: number;
  cardPortion?: number;
};

type HistoryReceipt = Receipt & {
  time: string;
  status: 'OK' | 'STORNO' | 'RETOURE';
};

type DemoOrder = {
  number: number;
  total: number;
  items: number;
  status: 'preparing' | 'ready';
  time: string;
};

type CashMovement = {
  type: 'EINLAGE' | 'ENTNAHME';
  amount: number;
  note: string;
  time: string;
};

const fallbackCatalogs: Record<Mode, Category[]> = {
  gastro: [
    {
      id: 'doener',
      name: 'Döner',
      letter: 'D',
      color: '#8DDC00',
      products: [
        { id: 'doener-kebab', name: 'Döner Kebab', price: 7.50 },
        { id: 'dueruem', name: 'Dürüm Döner', price: 8.00 },
        { id: 'doener-box', name: 'Döner Box', price: 7.00 },
        { id: 'lahmacun', name: 'Lahmacun Döner', price: 8.50 },
        { id: 'falafel', name: 'Falafel im Brot', price: 6.50 },
        { id: 'halloumi', name: 'Halloumi Dürüm', price: 7.50 }
      ]
    },
    {
      id: 'burger',
      name: 'Burger',
      letter: 'B',
      color: '#16A9E8',
      products: [
        { id: 'hamburger', name: 'Hamburger', price: 6.50 },
        { id: 'cheeseburger', name: 'Cheeseburger', price: 7.00 },
        { id: 'chickenburger', name: 'Chickenburger', price: 7.50 },
        { id: 'double-cheese', name: 'Double Cheeseburger', price: 9.50 },
        { id: 'veggie-burger', name: 'Veggie Burger', price: 7.20 },
        { id: 'burger-xl', name: 'Burger XL', price: 10.50 }
      ]
    },
    {
      id: 'fingerfood',
      name: 'Fingerfood',
      letter: 'F',
      color: '#DD27E9',
      products: [
        { id: 'nuggets', name: 'Chicken Nuggets', price: 6.50 },
        { id: 'mozzarella', name: 'Mozzarella Sticks', price: 5.50 },
        { id: 'wings', name: 'Chicken Wings', price: 7.00 },
        { id: 'onion-rings', name: 'Onion Rings', price: 4.50 },
        { id: 'chili-cheese', name: 'Chili Cheese Nuggets', price: 5.90 },
        { id: 'finger-mix', name: 'Fingerfood Mix', price: 8.90 }
      ]
    },
    {
      id: 'menue',
      name: 'Menü',
      letter: 'M',
      color: '#FF0064',
      products: [
        { id: 'doener-menue', name: 'Döner Menü', price: 12.90 },
        { id: 'burger-menue', name: 'Burger Menü', price: 11.90 },
        { id: 'dueruem-menue', name: 'Dürüm Menü', price: 13.50 },
        { id: 'nuggets-menue', name: 'Nuggets Menü', price: 10.90 },
        { id: 'pizza-menue', name: 'Pizza Menü', price: 13.90 },
        { id: 'veggie-menue', name: 'Veggie Menü', price: 11.50 }
      ]
    },
    {
      id: 'pizza',
      name: 'Pizza',
      letter: 'P',
      color: '#FF231D',
      products: [
        { id: 'pizza-margherita', name: 'Pizza Margherita', price: 8.00 },
        { id: 'pizza-salami', name: 'Pizza Salami', price: 9.00 },
        { id: 'pizza-tonno', name: 'Pizza Tonno', price: 10.00 },
        { id: 'pizza-veggie', name: 'Pizza Vegetaria', price: 9.50 },
        { id: 'pizza-doener', name: 'Pizza Döner', price: 11.00 },
        { id: 'pizza-funghi', name: 'Pizza Funghi', price: 9.00 }
      ]
    },
    {
      id: 'getraenke',
      name: 'Getränke',
      letter: 'G',
      color: '#A9CBCD',
      products: [
        { id: 'cola', name: 'Coca-Cola 0,33 l', price: 2.50 },
        { id: 'ayran', name: 'Ayran', price: 2.00 },
        { id: 'wasser', name: 'Mineralwasser 0,5 l', price: 1.80 },
        { id: 'fanta', name: 'Fanta 0,33 l', price: 2.50 },
        { id: 'sprite', name: 'Sprite 0,33 l', price: 2.50 },
        { id: 'redbull', name: 'Red Bull 0,25 l', price: 3.00 }
      ]
    }
  ],
  retail: [
    {
      id: 'retail-drinks',
      name: 'Getränke',
      letter: 'G',
      color: '#16A9E8',
      products: [
        { id: 'r-cola', name: 'Coca-Cola 0,33 l', price: 2.50 },
        { id: 'r-water', name: 'Mineralwasser 0,5 l', price: 1.80 },
        { id: 'r-redbull', name: 'Red Bull 0,25 l', price: 3.00 },
        { id: 'r-fanta', name: 'Fanta 0,33 l', price: 2.50 },
        { id: 'r-sprite', name: 'Sprite 0,33 l', price: 2.50 },
        { id: 'r-juice', name: 'Orangensaft 1 l', price: 2.99 }
      ]
    },
    {
      id: 'snacks',
      name: 'Snacks',
      letter: 'S',
      color: '#FF9B00',
      products: [
        { id: 'chips', name: 'Chips Paprika', price: 2.20 },
        { id: 'peanuts', name: 'Erdnüsse', price: 2.40 },
        { id: 'pretzels', name: 'Salzstangen', price: 1.90 },
        { id: 'nachos', name: 'Nachos', price: 2.90 },
        { id: 'cracker', name: 'Cracker', price: 2.10 },
        { id: 'popcorn', name: 'Popcorn', price: 2.50 }
      ]
    },
    {
      id: 'sweets',
      name: 'Süßwaren',
      letter: 'S',
      color: '#DD27E9',
      products: [
        { id: 'bueno', name: 'Kinder Bueno', price: 1.50 },
        { id: 'twix', name: 'Twix', price: 1.40 },
        { id: 'haribo', name: 'Haribo Goldbären', price: 2.20 },
        { id: 'milka', name: 'Milka Alpenmilch', price: 2.40 },
        { id: 'snickers', name: 'Snickers', price: 1.40 },
        { id: 'mentos', name: 'Mentos', price: 1.50 }
      ]
    },
    {
      id: 'food',
      name: 'Lebensmittel',
      letter: 'L',
      color: '#8DDC00',
      products: [
        { id: 'milk', name: 'Milch 1 l', price: 1.49 },
        { id: 'eggs', name: 'Eier 10er', price: 3.20 },
        { id: 'butter', name: 'Butter 250 g', price: 2.49 },
        { id: 'bread', name: 'Mischbrot', price: 2.80 },
        { id: 'cheese', name: 'Gouda 250 g', price: 3.49 },
        { id: 'pasta', name: 'Spaghetti 500 g', price: 1.79 }
      ]
    },
    {
      id: 'household',
      name: 'Haushalt',
      letter: 'H',
      color: '#FF0064',
      products: [
        { id: 'kitchen-roll', name: 'Küchenrolle', price: 3.49 },
        { id: 'trash-bags', name: 'Müllbeutel 20 Stk.', price: 2.99 },
        { id: 'batteries', name: 'Batterien AA 4 Stk.', price: 4.99 },
        { id: 'lighter', name: 'Feuerzeug', price: 1.50 },
        { id: 'foil', name: 'Alufolie', price: 2.49 },
        { id: 'tissues', name: 'Taschentücher', price: 1.99 }
      ]
    },
    {
      id: 'other',
      name: 'Sonstiges',
      letter: 'S',
      color: '#A9CBCD',
      products: [
        { id: 'newspaper', name: 'Tageszeitung', price: 2.50 },
        { id: 'magazine', name: 'TV-Magazin', price: 2.20 },
        { id: 'puzzle', name: 'Rätselheft', price: 3.00 },
        { id: 'gift-card', name: 'Geschenkkarte', price: 10.00 },
        { id: 'bag', name: 'Tragetasche', price: 0.30 },
        { id: 'quick', name: 'Schnellartikel', price: 1.00 }
      ]
    }
  ]
};

const labels = {
  de: {
    editionRetail: 'EINZELHANDEL',
    editionGastro: 'GASTRONOMIE',
    company: 'TOR POS Testbetrieb',
    register: 'Kasse 1',
    goods: 'WAREN',
    settings: 'EINSTELLUNGEN',
    cashDesk: 'KASSE',
    reports: 'BERICHTE',
    tseOutage: 'TSE-AUSFALL',
    noLicense: 'TEST · KEINE LIZENZ',
    user: 'admin',
    outside: 'AUSSER HAUS',
    inside: 'IM HAUS',
    mixed: 'GEMISCHT',
    logout: 'ABMELDEN',
    barcode: 'EAN / BARCODE',
    search: 'EAN SUCHEN',
    quickItem: 'SCHNELLARTIKEL',
    discount: 'RABATT',
    openOrders: 'OFFENE BESTELLUNGEN',
    receiptOn: 'BON EIN/AUS · EIN',
    receiptOff: 'BON EIN/AUS · AUS',
    checkout: 'KASSIEREN',
    paymentType: 'F5 · ZAHLART',
    groups: 'WARENGRUPPEN',
    touchHint: 'TOUCH · Warengruppe → Artikel',
    back: '◀ WARENGRUPPEN',
    productHint: 'Artikel antippen → direkt im Bon',
    currentSale: 'AKTUELLER VERKAUF',
    input: 'EINGABE',
    tapHint: 'WARENGRUPPE ODER ARTIKEL ANTIPPEN',
    tapSub: 'TOUCH → ARTIKEL · F1 BAR / F2 KARTE',
    total: 'GESAMT',
    positions: 'Positionen',
    extra: 'EXTRA',
    order: 'BESTELLUNG\nANNEHMEN',
    cash: 'BAR · TEST',
    card: 'KARTE · TEST',
    pay: 'BEZAHLEN',
    storno: 'SOFORT\nSTORNO',
    keyboard: 'TASTATUR',
    keyboardHint: 'Eingabefeld auswählen',
    emptyWarning: 'Bitte zuerst einen Artikel hinzufügen.',
    modeChanged: 'Edition gewechselt – Demo-Bon wurde zurückgesetzt.',
    menuDemo: 'Diese Funktion ist im Online-Simulator nur als Ansicht vorhanden.',
    extraDemo: 'EXTRA ist in dieser Online-Demo vereinfacht.',
    mixedDemo: 'Gemischte Zahlung ist in dieser Online-Demo vereinfacht.',
    parked: 'Bon wurde geparkt.',
    quickAdded: 'Schnellartikel 1,00 € wurde hinzugefügt.',
    qtyApplied: 'Menge wurde übernommen.',
    cashTitle: 'BARZAHLUNG · TEST',
    cardTitle: 'KARTENZAHLUNG · TEST',
    choosePay: 'ZAHLART WÄHLEN',
    amountGiven: 'GEGEBEN',
    change: 'RÜCKGELD',
    finish: 'ZAHLUNG ABSCHLIESSEN',
    simulateCard: 'KARTENZAHLUNG SIMULIEREN',
    cancel: 'ABBRECHEN',
    orderReady: 'Bestellung angenommen · Abholnummer',
    receipt: 'KASSENBON · DEMO',
    orderTicket: 'BESTELLUNG · DEMO',
    orderNumber: 'ABHOLNUMMER',
    preparing: 'WIRD VORBEREITET',
    payment: 'Zahlart',
    subtotal: 'Zwischensumme',
    discountLine: 'Rabatt',
    received: 'Gegeben',
    tseDemo: 'TSE DEMO · keine fiskalische Transaktion',
    continueSale: 'WEITER KASSIEREN',
    searchTitle: 'EAN / ARTIKEL SUCHEN',
    searchPlaceholder: 'Produktname eingeben',
    searchButton: 'SUCHEN',
    searchNone: 'Kein Demo-Artikel gefunden.',
    close: 'SCHLIESSEN',
    page: '1 / 1'
  },
  tr: {
    editionRetail: 'EINZELHANDEL',
    editionGastro: 'GASTRONOMİ',
    company: 'TOR POS Test Modu',
    register: 'Kasa 1',
    goods: 'ÜRÜNLER',
    settings: 'AYARLAR',
    cashDesk: 'KASA',
    reports: 'RAPORLAR',
    tseOutage: 'TSE-ARIZASI',
    noLicense: 'TEST · LİSANS YOK',
    user: 'admin',
    outside: 'PAKET',
    inside: 'İÇERİDE',
    mixed: 'KARIŞIK',
    logout: 'ÇIKIŞ',
    barcode: 'EAN / BARKOD',
    search: 'EAN ARA',
    quickItem: 'HIZLI ÜRÜN',
    discount: 'İNDİRİM',
    openOrders: 'AÇIK SİPARİŞLER',
    receiptOn: 'FİŞ AÇ/KAP · AÇIK',
    receiptOff: 'FİŞ AÇ/KAP · KAPALI',
    checkout: 'TAHSİLAT',
    paymentType: 'F5 · ÖDEME TÜRÜ',
    groups: 'ÜRÜN GRUPLARI',
    touchHint: 'DOKUN · Grup → Ürün',
    back: '◀ ÜRÜN GRUPLARI',
    productHint: 'Ürüne dokun → fişe ekle',
    currentSale: 'GÜNCEL SATIŞ',
    input: 'GİRİŞ',
    tapHint: 'ÜRÜN GRUBUNA VEYA ÜRÜNE DOKUN',
    tapSub: 'DOKUN → ÜRÜN · F1 NAKİT / F2 KART',
    total: 'TOPLAM',
    positions: 'Kalem',
    extra: 'EKSTRA',
    order: 'SİPARİŞİ\nAL',
    cash: 'NAKİT · TEST',
    card: 'KART · TEST',
    pay: 'ÖDE',
    storno: 'HEMEN\nİPTAL',
    keyboard: 'KLAVYE',
    keyboardHint: 'Giriş alanını seç',
    emptyWarning: 'Önce bir ürün ekleyin.',
    modeChanged: 'Bölüm değişti – demo fişi sıfırlandı.',
    menuDemo: 'Bu özellik online simülatörde yalnız görünüm olarak yer alır.',
    extraDemo: 'EKSTRA bu online demoda basitleştirilmiştir.',
    mixedDemo: 'Karışık ödeme bu online demoda basitleştirilmiştir.',
    parked: 'Fiş beklemeye alındı.',
    quickAdded: 'Hızlı ürün 1,00 € eklendi.',
    qtyApplied: 'Miktar uygulandı.',
    cashTitle: 'NAKİT ÖDEME · TEST',
    cardTitle: 'KART ÖDEME · TEST',
    choosePay: 'ÖDEME TÜRÜNÜ SEÇ',
    amountGiven: 'ALINAN',
    change: 'PARA ÜSTÜ',
    finish: 'ÖDEMEYİ TAMAMLA',
    simulateCard: 'KART ÖDEMESİNİ SİMÜLE ET',
    cancel: 'İPTAL',
    orderReady: 'Sipariş alındı · sıra numarası',
    receipt: 'KASA FİŞİ · DEMO',
    orderTicket: 'SİPARİŞ · DEMO',
    orderNumber: 'SIRA NUMARASI',
    preparing: 'HAZIRLANIYOR',
    payment: 'Ödeme',
    subtotal: 'Ara toplam',
    discountLine: 'İndirim',
    received: 'Alınan',
    tseDemo: 'TSE DEMO · mali işlem değildir',
    continueSale: 'SATIŞA DEVAM',
    searchTitle: 'EAN / ÜRÜN ARA',
    searchPlaceholder: 'Ürün adı yazın',
    searchButton: 'ARA',
    searchNone: 'Demo ürünü bulunamadı.',
    close: 'KAPAT',
    page: '1 / 1'
  }
} as const;

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#039;');
}

export function mountTorSimulator(rootId: string, lang: SimulatorLang): void {
  const root = document.getElementById(rootId);
  if (!root) return;

  const text: Record<string, string> = { ...labels[lang] };
  const locale = lang === 'de' ? 'de-DE' : 'tr-TR';
  let catalogs: Record<Mode, Category[]> = fallbackCatalogs;

  let mode: Mode = 'gastro';
  let screen: Screen = 'cashier';
  let cloudTab: CloudTab = 'dashboard';
  let cloudPortal: CloudPortalLabels = fallbackCloudPortal;
  let settingsSection = 'Kasse & Bedienung';
  let view: View = 'categories';
  let categoryId = catalogs.gastro[0].id;
  let cart: CartLine[] = [];
  let selectedId = '';
  let numericInput = '';
  let discountPct = 0;
  let openOrders = 0;
  let orderNumber = 42;
  let saleNumber = 1049;
  let receiptEnabled = true;
  let outside = true;
  let status = '';
  let panel: Panel = null;
  let payMethod: 'BAR' | 'KARTE' | 'GEMISCHT' = 'BAR';
  let cashGiven = 0;
  let mixedCash = 0;
  let searchQuery = '';
  let receipt: Receipt | null = null;
  let demoOrders: DemoOrder[] = [];
  let receiptHistory: HistoryReceipt[] = [
    {
      kind: 'sale',
      number: 1045,
      lines: [{ id: 'history-0', name: 'Pommes', price: 3.50, qty: 1 }],
      subtotal: 3.50,
      discountPct: 0,
      total: 3.50,
      payment: 'BAR',
      cashGiven: 5,
      change: 1.50,
      time: '10:05',
      status: 'STORNO'
    },
    {
      kind: 'sale',
      number: 1046,
      lines: [{ id: 'history-1', name: 'Coca-Cola 0,33 l', price: 2.50, qty: 2 }],
      subtotal: 5.00,
      discountPct: 0,
      total: 5.00,
      payment: 'BAR',
      cashGiven: 10,
      change: 5,
      time: '10:12',
      status: 'OK'
    },
    {
      kind: 'sale',
      number: 1047,
      lines: [{ id: 'history-2', name: 'Döner Kebab', price: 7.50, qty: 1 }],
      subtotal: 7.50,
      discountPct: 0,
      total: 7.50,
      payment: 'KARTE',
      time: '10:18',
      status: 'OK'
    },
    {
      kind: 'sale',
      number: 1048,
      lines: [{ id: 'history-3', name: 'Ayran', price: 2.00, qty: 1 }],
      subtotal: 2.00,
      discountPct: 0,
      total: 2.00,
      payment: 'BAR',
      cashGiven: 2,
      change: 0,
      time: '10:23',
      status: 'RETOURE'
    }
  ];
  let cashMovements: CashMovement[] = [
    { type: 'EINLAGE', amount: 100, note: 'Wechselgeld', time: '08:00' }
  ];

  function euro(value: number): string {
    return new Intl.NumberFormat(locale, { style: 'currency', currency: 'EUR' }).format(value);
  }

  function editionName(): string {
    return mode === 'gastro' ? text.editionGastro : text.editionRetail;
  }

  function applyContract(contract: SimulatorContract): void {
    if (validCloudPortal(contract.cloudPortal)) cloudPortal = contract.cloudPortal;
    catalogs = {
      gastro: contract.demoCatalogs.gastro,
      retail: contract.demoCatalogs.retail
    };

    text.editionRetail = lang === 'de'
      ? contract.publicEditions.retail.labelDe
      : contract.publicEditions.retail.labelTr;
    text.editionGastro = lang === 'de'
      ? contract.publicEditions.gastro.labelDe
      : contract.publicEditions.gastro.labelTr;

    if (lang === 'de') {
      const [goods, settings, cashDesk, reports] = contract.desktopUi.topMenu;
      if (goods) text.goods = goods;
      if (settings) text.settings = settings;
      if (cashDesk) text.cashDesk = cashDesk;
      if (reports) text.reports = reports;

      const badges = contract.desktopUi.statusBadges;
      text.tseOutage = badges.find((value) => value.includes('TSE')) ?? text.tseOutage;
      text.noLicense = badges.find((value) => value.includes('LIZENZ')) ?? text.noLicense;
      text.user = badges.find((value) => value.toLowerCase() === 'admin') ?? text.user;
      text.outside = badges.find((value) => value.includes('AUSSER')) ?? text.outside;
      text.mixed = badges.find((value) => value.includes('GEMISCHT')) ?? text.mixed;
      text.logout = badges.find((value) => value.includes('ABMELDEN')) ?? text.logout;

      Object.assign(text, {
        barcode: contract.desktopUi.quickActions.barcode,
        search: contract.desktopUi.quickActions.search,
        quickItem: contract.desktopUi.quickActions.quickItem,
        discount: contract.desktopUi.quickActions.discount,
        openOrders: contract.desktopUi.quickActions.openOrders,
        receiptOn: contract.desktopUi.quickActions.receiptOn,
        checkout: contract.desktopUi.quickActions.checkout,
        currentSale: contract.desktopUi.cashier.currentSale,
        cash: contract.desktopUi.cashier.cash,
        card: contract.desktopUi.cashier.card,
        extra: contract.desktopUi.cashier.extra,
        order: contract.desktopUi.cashier.order.replace(' ', '\n')
      });
    }

    root.style.setProperty('--posx-bg', contract.theme.background);
    root.style.setProperty('--posx-header', contract.theme.header);
    root.style.setProperty('--posx-panel', contract.theme.panel);
    root.style.setProperty('--posx-product', contract.theme.productTile);
    root.style.setProperty('--posx-product-border', contract.theme.productBorder);
    root.style.setProperty('--posx-teal', contract.theme.accent);
    root.style.setProperty('--posx-cash', contract.theme.cash);
    root.style.setProperty('--posx-card', contract.theme.card);

    categoryId = catalogs[mode][0]?.id ?? categoryId;
    view = 'categories';
  }

  function categories(): Category[] {
    return catalogs[mode];
  }

  function category(): Category {
    return categories().find((item) => item.id === categoryId) ?? categories()[0];
  }

  function allProducts(): Product[] {
    return categories().flatMap((item) => item.products);
  }

  function subtotal(): number {
    return cart.reduce((sum, line) => sum + line.price * line.qty, 0);
  }

  function total(): number {
    return subtotal() * (1 - discountPct / 100);
  }

  function itemCount(): number {
    return cart.reduce((sum, line) => sum + line.qty, 0);
  }

  function resetSale(message = ''): void {
    cart = [];
    selectedId = '';
    numericInput = '';
    discountPct = 0;
    panel = null;
    cashGiven = 0;
    mixedCash = 0;
    payMethod = 'BAR';
    outside = true;
    receipt = null;
    status = message;
  }

  function switchMode(): void {
    mode = mode === 'gastro' ? 'retail' : 'gastro';
    screen = 'cashier';
    view = 'categories';
    categoryId = categories()[0].id;
    resetSale(text.modeChanged);
    render();
  }

  function addProduct(product: Product): void {
    const existing = cart.find((line) => line.id === product.id);
    if (existing) existing.qty += 1;
    else cart.push({ ...product, qty: 1 });
    selectedId = product.id;
    numericInput = '';
    status = '';
    render();
  }

  function selectCategory(id: string): void {
    categoryId = id;
    view = 'products';
    status = '';
    render();
  }

  function selectedLine(): CartLine | undefined {
    return cart.find((line) => line.id === selectedId) ?? cart[cart.length - 1];
  }

  function adjustQty(delta: number): void {
    const line = selectedLine();
    if (!line) {
      status = text.emptyWarning;
      render();
      return;
    }
    line.qty += delta;
    if (line.qty <= 0) {
      cart = cart.filter((item) => item.id !== line.id);
      selectedId = cart[cart.length - 1]?.id ?? '';
    }
    render();
  }

  function removeSelected(): void {
    const line = selectedLine();
    if (!line) {
      status = text.emptyWarning;
      render();
      return;
    }
    cart = cart.filter((item) => item.id !== line.id);
    selectedId = cart[cart.length - 1]?.id ?? '';
    render();
  }

  function applyNumericQty(): void {
    const line = selectedLine();
    const value = Number(numericInput.replace(',', '.'));
    if (!line || !Number.isFinite(value) || value < 1) {
      status = text.emptyWarning;
      render();
      return;
    }
    line.qty = Math.max(1, Math.floor(value));
    numericInput = '';
    status = text.qtyApplied;
    render();
  }

  function requireCart(): boolean {
    if (cart.length > 0) return true;
    status = text.emptyWarning;
    render();
    return false;
  }

  function openPayChoice(): void {
    if (!requireCart()) return;
    // R166 parity: one payment page owns Verkaufsart, tender selection and
    // tender-specific input. Every new sale starts AUSSER HAUS + BAR.
    outside = true;
    payMethod = 'BAR';
    cashGiven = total();
    mixedCash = 0;
    panel = 'payChoice';
    render();
  }

  function openCash(): void {
    if (!requireCart()) return;
    payMethod = 'BAR';
    if (cashGiven < total()) cashGiven = total();
    panel = 'payChoice';
    render();
  }

  function openCard(): void {
    if (!requireCart()) return;
    payMethod = 'KARTE';
    panel = 'payChoice';
    render();
  }

  function openMixed(): void {
    if (!requireCart()) return;
    if (total() <= 0) {
      status = lang === 'de' ? 'GEMISCHT ist bei einer Auszahlung nicht möglich.' : 'Karma ödeme iade işleminde kullanılamaz.';
      render();
      return;
    }
    payMethod = 'GEMISCHT';
    if (mixedCash <= 0 || mixedCash >= total()) mixedCash = Math.round((total() / 2) * 100) / 100;
    panel = 'payChoice';
    render();
  }

  function snapshotReceipt(kind: 'sale' | 'order', payment?: 'BAR' | 'KARTE' | 'GEMISCHT'): Receipt {
    const currentSubtotal = subtotal();
    const currentTotal = total();
    return {
      kind,
      number: kind === 'order' ? orderNumber : saleNumber,
      lines: cart.map((line) => ({ ...line })),
      subtotal: currentSubtotal,
      discountPct,
      total: currentTotal,
      payment,
      imHaus: !outside,
      cashGiven: payment === 'BAR' ? cashGiven : undefined,
      change: payment === 'BAR' ? Math.max(0, cashGiven - currentTotal) : undefined
    };
  }

  function finishPayment(payment: 'BAR' | 'KARTE' | 'GEMISCHT'): void {
    if (payment === 'BAR' && cashGiven < total()) return;
    if (payment === 'GEMISCHT' && (mixedCash <= 0 || mixedCash >= total())) return;

    const completed = snapshotReceipt('sale', payment);
    if (payment === 'GEMISCHT') {
      completed.cashPortion = mixedCash;
      completed.cardPortion = Math.round((completed.total - mixedCash) * 100) / 100;
    }
    receipt = completed;
    receiptHistory.unshift({
      ...completed,
      time: new Date().toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }),
      status: 'OK'
    });
    saleNumber += 1;
    cart = [];
    selectedId = '';
    numericInput = '';
    discountPct = 0;
    mixedCash = 0;
    outside = true;
    panel = null;
    render();
  }

  function acceptOrder(): void {
    if (!requireCart()) return;
    const currentTotal = total();
    const currentItems = itemCount();
    receipt = snapshotReceipt('order');
    demoOrders.unshift({
      number: orderNumber,
      total: currentTotal,
      items: currentItems,
      status: 'preparing',
      time: new Date().toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
    });
    status = text.orderReady + ' ' + orderNumber;
    orderNumber += 1;
    openOrders = demoOrders.filter((order) => order.status === 'preparing').length;
    cart = [];
    selectedId = '';
    discountPct = 0;
    render();
  }

  function parkSale(): void {
    if (!requireCart()) return;
    resetSale(text.parked);
    render();
  }

  function addQuickItem(): void {
    addProduct({ id: 'quick-' + Date.now(), name: 'Schnellartikel', price: 1.00 });
    status = text.quickAdded;
  }

  function searchResults(): Product[] {
    const query = searchQuery.trim().toLocaleLowerCase(locale);
    if (!query) return [];
    return allProducts().filter((product) => product.name.toLocaleLowerCase(locale).includes(query)).slice(0, 10);
  }

  function categoryMarkup(): string {
    return categories().map((item) =>
      '<button type="button" class="posx-category" data-category="' + escapeHtml(item.id) + '" style="--tile:' + item.color + '">' +
        '<span class="posx-category-letter">' + escapeHtml(item.letter) + '</span>' +
        '<strong>' + escapeHtml(item.name) + '</strong>' +
      '</button>'
    ).join('');
  }

  function productMarkup(): string {
    return category().products.map((product) =>
      '<button type="button" class="posx-product" data-product="' + escapeHtml(product.id) + '">' +
        '<strong>' + escapeHtml(product.name) + '</strong>' +
        '<span>' + euro(product.price) + '</span>' +
      '</button>'
    ).join('');
  }

  function cartMarkup(): string {
    if (cart.length === 0) {
      return '<div class="posx-cart-empty"><strong>' + text.tapHint + '</strong><span>' + text.tapSub + '</span></div>';
    }

    return cart.map((line) => {
      const selected = line.id === (selectedLine()?.id ?? '') ? ' selected' : '';
      return '<button type="button" class="posx-cart-line' + selected + '" data-select-line="' + escapeHtml(line.id) + '">' +
        '<span class="posx-cart-qty">' + line.qty + '×</span>' +
        '<span class="posx-cart-name">' + escapeHtml(line.name) + '<small>' + euro(line.price) + '</small></span>' +
        '<strong>' + euro(line.price * line.qty) + '</strong>' +
      '</button>';
    }).join('');
  }

  function keypadMarkup(): string {
    const keys = [
      ['7', '8', '9', '×'],
      ['4', '5', '6', ','],
      ['1', '2', '3', 'C'],
      ['0', '+1', '−1', text.storno]
    ];

    return keys.flatMap((row, rowIndex) => row.map((key, colIndex) => {
      let action = 'digit';
      let value = key;
      let extraClass = '';

      if (key === '×') action = 'multiply';
      else if (key === ',') action = 'comma';
      else if (key === 'C') {
        action = 'clear';
        extraClass = ' danger';
      } else if (key === '+1') action = 'plus';
      else if (key === '−1') action = 'minus';
      else if (key === text.storno) {
        action = 'storno';
        extraClass = ' storno';
      }

      return '<button type="button" class="posx-key' + extraClass + '" data-key-action="' + action + '" data-key-value="' + escapeHtml(value) + '" style="grid-row:' + (rowIndex + 1) + ';grid-column:' + (colIndex + 1) + '">' +
        escapeHtml(key).replace('\n', '<br>') +
      '</button>';
    })).join('');
  }

  function topMarkup(): string {
    return '<div class="posx-top">' +
      '<div class="posx-brand"><div class="posx-tor">TOR</div><div><strong>' + text.company + '</strong><span>' + text.register + ' · ' + editionName() + '</span></div></div>' +
      '<div class="posx-main-nav">' +
        '<button type="button" class="posx-edition" data-switch-mode>' + editionName() + '</button>' +
        '<button type="button" data-open-waren>' + text.goods + '</button>' +
        '<button type="button" data-open-settings>' + text.settings + '</button>' +
        '<button type="button" data-open-kasse>' + text.cashDesk + '</button>' +
        '<button type="button" data-open-reports>' + text.reports + '</button>' +
      '</div>' +
      '<div class="posx-statuses">' +
        '<button type="button" class="posx-badge outage" data-menu>' + text.tseOutage + '</button>' +
        '<button type="button" class="posx-badge license" data-menu>' + text.noLicense + '</button>' +
        '<span class="posx-badge admin">' + text.user + '</span>' +
        '<button type="button" class="posx-badge logout" data-sim-logout>' + text.logout + '</button>' +
      '</div>' +
    '</div>';
  }

  function quickbarMarkup(): string {
    return '<div class="posx-quickbar">' +
      '<strong class="posx-barcode-label">' + text.barcode + '</strong>' +
      '<button type="button" data-open-search>' + text.search + '</button>' +
      '<button type="button" class="posx-quick-item" data-quick-item>' + text.quickItem + '</button>' +
      '<button type="button" data-discount>' + text.discount + (discountPct ? ' 10%' : '') + '</button>' +
      '<button type="button" class="posx-orders" data-menu>' + text.openOrders + ' (' + openOrders + ')</button>' +
      '<button type="button" class="posx-receipt-toggle" data-receipt-toggle>' + (receiptEnabled ? text.receiptOn : text.receiptOff) + '</button>' +
    '</div>';
  }

  function leftMarkup(): string {
    if (view === 'categories') {
      return '<section class="posx-left">' +
        '<div class="posx-left-head"><div><strong>' + text.groups + ' · ' + editionName() + '</strong><span>' + text.touchHint + '</span></div><div class="posx-page"><button type="button" disabled>◀</button><b>' + text.page + '</b><button type="button" disabled>▶</button></div></div>' +
        '<div class="posx-category-grid">' + categoryMarkup() + '</div>' +
      '</section>';
    }

    return '<section class="posx-left">' +
      '<div class="posx-left-head products"><button type="button" class="posx-back" data-back>' + text.back + '</button><div><strong>' + escapeHtml(category().name).toUpperCase() + '</strong><span>' + text.productHint + '</span></div><div class="posx-page"><button type="button" disabled>◀</button><b>' + text.page + '</b><button type="button" disabled>▶</button></div></div>' +
      '<div class="posx-product-grid">' + productMarkup() + '</div>' +
    '</section>';
  }

  function rightMarkup(): string {
    return '<aside class="posx-right">' +
      '<div class="posx-sale">' +
        '<div class="posx-sale-head"><strong>' + text.currentSale + '</strong><span>' + text.input + ': ' + (numericInput || '—') + '</span></div>' +
        '<div class="posx-cart-list">' + cartMarkup() + '</div>' +
        '<div class="posx-sale-summary">' +
          '<div><span>' + itemCount() + ' ' + text.positions + (discountPct ? ' · −' + discountPct + '%' : '') + '</span><b>' + text.total + '</b></div>' +
          '<strong>' + euro(total()) + '</strong>' +
        '</div>' +
      '</div>' +
      '<div class="posx-keypad">' +
        '<div class="posx-keys">' + keypadMarkup() + '</div>' +
        '<div class="posx-action-row">' +
          '<button type="button" class="extra" data-extra>' + text.extra + '</button>' +
          '<button type="button" class="order" data-order>' + text.order.replace('\n', '<br>') + '<small>F3 · AUFTRAG</small></button>' +
          '<button type="button" class="checkout" data-checkout>' + text.checkout + '<small>F5 · ' + text.pay + '</small></button>' +
        '</div>' +
      '</div>' +
    '</aside>';
  }

  function demoInventoryRows(): Array<{ id: string; name: string; ean: string; vk: number; ek: number; stock: number; min: number }> {
    const stockPattern = [18, 4, 31, 7, 2, 15, 24, 5, 11, 3, 19, 8];
    const minPattern = [5, 6, 8, 5, 5, 6, 10, 4, 6, 5, 7, 5];
    return allProducts().slice(0, 12).map((product, index) => ({
      id: product.id,
      name: product.name,
      ean: '400' + String(1000000000 + index).padStart(10, '0'),
      vk: product.price,
      ek: Math.max(.2, Math.round(product.price * .56 * 100) / 100),
      stock: stockPattern[index % stockPattern.length],
      min: minPattern[index % minPattern.length]
    }));
  }

  function managementBarMarkup(): string {
    const title = screen === 'settings' ? 'EINSTELLUNGEN'
      : screen === 'waren' ? 'WAREN'
      : screen === 'artikel' ? 'WAREN / ARTIKEL'
      : screen === 'inventur' ? 'WAREN / BESTAND · INVENTUR'
      : screen === 'exchange' ? 'WAREN / DATENAUSTAUSCH'
      : screen === 'kasse' ? 'KASSE'
      : screen === 'orders' ? 'KASSE / BESTELLÜBERSICHT'
      : screen === 'receiptHistory' ? 'KASSE / BON-HISTORIE'
      : screen === 'zreport' ? 'KASSE / Z-BERICHT'
      : screen === 'cashMovement' ? 'KASSE / EINLAGE · ENTNAHME'
      : screen === 'reports' ? 'BERICHTE'
      : screen === 'xreport' ? 'BERICHTE / X-BERICHT'
      : screen === 'cashCount' ? 'BERICHTE / KASSENSTURZ'
      : screen === 'cashJournal' ? 'BERICHTE / KASSENJOURNAL'
      : screen === 'turnover' ? 'BERICHTE / UMSATZ'
      : screen === 'monthly' ? 'BERICHTE / MONAT'
      : screen === 'salesStats' ? 'BERICHTE / VERKAUFSSTATISTIK'
      : screen === 'inventoryReport' ? 'BERICHTE / WARENBESTAND'
      : screen === 'operatorReport' ? 'BERICHTE / BEDIENER'
      : screen === 'cloud' ? 'TOR CLOUD / KUNDENPORTAL · VORSCHAU'
      : 'BERICHTE / STORNO';

    return '<div class="posx-management-bar">' +
      '<button type="button" data-back-cashier>← ZURÜCK ZUR KASSE</button>' +
      '<strong>' + title + '</strong>' +
      '<span>ONLINE-SIMULATOR · DEMODATEN</span>' +
    '</div>';
  }

  const settingsSections = [
    { title: 'Kasse & Bedienung', note: 'Alltagseinstellungen für Verkauf, Anzeige und Bedienung.', items: ['Start & Anzeige', 'Kassenfunktionen', 'Bedienung'] },
    { title: 'Firma & Bon', note: 'Geschäftsdaten und sichtbare Bon-Einstellungen.', items: ['Firmendaten', 'Bon & Rechnung', 'Logo / QR-Code'] },
    { title: 'Artikel & Steuern', note: 'Steuerliche Grundlagen; die Artikelpflege bleibt unter WAREN.', items: ['Steuersätze', 'Warengruppen', 'Pfand / Einheit'] },
    { title: 'Zahlung', note: 'Zahlarten, die an der Kasse verwendet werden dürfen.', items: ['Barzahlung', 'Kartenzahlung', 'SumUp / Terminal'] },
    { title: 'Geräte', note: 'Angeschlossene Hardware und TOR Cloud.', items: ['Bondrucker', 'Kassenschublade', 'Kundenanzeige', 'Scanner', 'TOR Cloud'] },
    { title: 'Personal', note: 'Benutzer, Bediener und Berechtigungen.', items: ['Benutzer', 'Rollen & Rechte', 'Bediener-PIN'] },
    { title: 'Berichte & E-Mail', note: 'Berichte und einfacher Versand über TOR Mail.', items: ['Berichte', 'TOR Mail', 'Monatsversand'] },
    { title: 'DATEV', note: 'Steuerberater-Export und DATEV-Anbindungen.', items: ['Kassenbuch Standard-ASCII', 'Kassenarchiv Online', 'Steuerberater-E-Mail'] },
    { title: 'Datensicherung', note: 'Automatische und manuelle Sicherungen.', items: ['Automatische Sicherung', 'Sicherungsordner', 'Wiederherstellung'] },
    { title: 'Software & Update', note: 'Programmversion und sichere Aktualisierung.', items: ['Version', 'Nach Updates suchen', 'Update-Kanal'] },
    { title: 'Erweitert / Techniker 🔒', note: 'Geschützter Servicebereich für technische und fiskalische Einstellungen.', items: ['TSE', 'Recht & Fiskal', 'Netzwerk / Ports', 'Lizenzierung', 'Systemdiagnose'] }
  ] as const;

  function settingsMarkup(): string {
    const selected = settingsSections.find((item) => item.title === settingsSection) ?? settingsSections[0];
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>EINSTELLUNGEN</span><h3>' + escapeHtml(selected.title) + '</h3><p>Die Struktur entspricht dem aktuellen TOR POS Einstellungsbereich. Änderungen bleiben im Online-Simulator ohne Wirkung.</p></div></div>' +
      '<div class="posx-settings-layout">' +
        '<nav class="posx-settings-nav" aria-label="Einstellungen">' +
          settingsSections.map((item) => '<button type="button" class="posx-settings-nav-button' + (item.title === selected.title ? ' active' : '') + '" data-settings-section="' + escapeHtml(item.title) + '">' + escapeHtml(item.title) + '</button>').join('') +
        '</nav>' +
        '<section class="posx-settings-detail">' +
          '<span>EINSTELLUNGEN · DEMO</span>' +
          '<h4>' + escapeHtml(selected.title) + '</h4>' +
          '<p>' + escapeHtml(selected.note) + '</p>' +
          '<div class="posx-settings-options">' + selected.items.map((item) => '<button type="button" data-demo-action><strong>' + escapeHtml(item) + '</strong><small>Öffnen · Demo</small></button>').join('') + '</div>' +
          '<div class="posx-exchange-note">TESTVERSION · Keine Einstellung wird gespeichert oder an eine echte Kasse übertragen.</div>' +
        '</section>' +
      '</div>' +
    '</div>';
  }

  function warenHomeMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>WAREN</span><h3>Warenverwaltung</h3><p>Die wichtigsten Verwaltungsbereiche der TOR POS Kasse – mit sicheren Demodaten.</p></div></div>' +
      '<div class="posx-management-cards">' +
        '<section><strong>ARTIKEL</strong><button type="button" data-management="artikel">Artikelverwaltung · Anlegen / Ändern / Scanner</button><button type="button" data-demo-action>ANGEBOTE / AKTIONEN</button><button type="button" data-demo-action>Duplikate anzeigen</button><button type="button" data-demo-action>EAN-Etiketten drucken</button></section>' +
        '<section><strong>BESTAND / INVENTUR</strong><button type="button" data-management="inventur">Inventur · Scanner & Bestand</button><button type="button" data-management="inventur">Warenbestand · Bericht</button></section>' +
        '<section><strong>DATENAUSTAUSCH</strong><button type="button" data-management="exchange">Artikel importieren · CSV</button><button type="button" data-management="exchange">Artikel exportieren · CSV</button><button type="button" data-management="exchange">Import aus TOR-Datenbank</button></section>' +
      '</div>' +
    '</div>';
  }

  function artikelMarkup(): string {
    const rows = demoInventoryRows();
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>ARTIKEL</span><h3>Artikelverwaltung</h3><p>Demoansicht mit Verkaufspreis, Einkaufspreis, Bestand, Mindestbestand und EAN.</p></div><button type="button" data-demo-action>+ ARTIKEL ANLEGEN</button></div>' +
      '<div class="posx-data-table posx-article-table">' +
        '<div class="head"><b>Artikel</b><b>EAN</b><b>VK</b><b>EK</b><b>Bestand</b><b>Min.</b></div>' +
        rows.map((row) => '<div class="' + (row.stock < row.min ? 'low' : '') + '"><span>' + escapeHtml(row.name) + '</span><code>' + row.ean + '</code><strong>' + euro(row.vk) + '</strong><span>' + euro(row.ek) + '</span><b>' + row.stock + '</b><span>' + row.min + '</span></div>').join('') +
      '</div>' +
    '</div>';
  }

  function inventurMarkup(): string {
    const rows = demoInventoryRows();
    const totalValue = rows.reduce((sum, row) => sum + row.stock * row.ek, 0);
    const lowCount = rows.filter((row) => row.stock < row.min).length;
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>BESTAND / INVENTUR</span><h3>Inventur · Scanner & Bestand</h3><p>Scanner-first Demo: Bestand prüfen, Zählmenge anpassen und niedrigen Bestand erkennen.</p></div><div class="posx-inventory-metrics"><b>' + euro(totalValue) + '<small>Warenwert EK</small></b><b class="warn">' + lowCount + '<small>Niedriger Bestand</small></b></div></div>' +
      '<div class="posx-data-table posx-inventory-table">' +
        '<div class="head"><b>Artikel</b><b>Bestand</b><b>Min.</b><b>Zählmenge</b><b>Status</b></div>' +
        rows.map((row) => '<div class="' + (row.stock < row.min ? 'low' : '') + '"><span>' + escapeHtml(row.name) + '</span><b>' + row.stock + '</b><span>' + row.min + '</span><div class="posx-count-control"><button type="button" data-count-minus>−</button><input value="' + row.stock + '" inputmode="numeric" aria-label="Zählmenge ' + escapeHtml(row.name) + '"><button type="button" data-count-plus>+</button></div><strong>' + (row.stock < row.min ? 'NIEDRIG' : 'OK') + '</strong></div>').join('') +
      '</div>' +
      '<div class="posx-management-actions"><button type="button" data-demo-action>INVENTUR SPEICHERN · DEMO</button><button type="button" data-demo-action>WARENBESTAND BERICHT</button></div>' +
    '</div>';
  }

  function exchangeMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>DATENAUSTAUSCH</span><h3>Import & Export</h3><p>Nur Simulation: Es werden keine echten Dateien hochgeladen oder Daten verändert.</p></div></div>' +
      '<div class="posx-exchange-grid">' +
        '<button type="button" data-demo-action><b>CSV IMPORT</b><span>Artikel importieren · Vorschau & Validierung</span></button>' +
        '<button type="button" data-demo-action><b>CSV EXPORT</b><span>Demo-Artikelliste als Exportablauf ansehen</span></button>' +
        '<button type="button" data-demo-action><b>TOR-DATENBANK</b><span>Import aus TOR-Datenbank simulieren</span></button>' +
      '</div>' +
      '<div class="posx-exchange-note">TESTVERSION · Keine echten Dateien, keine Speicherung und keine Änderung produktiver Daten.</div>' +
    '</div>';
  }

  function kasseHomeMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>KASSE</span><h3>Kassenfunktionen</h3><p>Bestellungen, Bons und Kassenabschluss mit sicheren Demodaten testen.</p></div></div>' +
      '<div class="posx-management-cards posx-kasse-cards">' +
        '<section><strong>BETRIEB</strong><button type="button" data-management="orders">Bestellübersicht · Vorbereitung / Abholung</button><button type="button" data-demo-action>Zahlung prüfen / fortsetzen</button><button type="button" data-demo-action>Druckwarteschlange prüfen</button><button type="button" data-demo-action>Systemstatus / Diagnose</button></section>' +
        '<section><strong>BON / ABRECHNUNG</strong><button type="button" data-management="receiptHistory">Bon-Historie</button><button type="button" data-management="cashMovement">Einlage / Entnahme</button><button type="button" data-management="zreport">Z-Bericht</button></section>' +
        '<section><strong>KORREKTUREN</strong><button type="button" data-demo-action>Bon stornieren</button><button type="button" data-demo-action>Teilretoure · einzelne Artikel</button></section>' +
      '</div>' +
    '</div>';
  }

  function ordersMarkup(): string {
    const active = demoOrders.filter((order) => order.status === 'preparing').length;
    const ready = demoOrders.filter((order) => order.status === 'ready').length;
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>BESTELLÜBERSICHT</span><h3>Vorbereitung / Abholung</h3><p>Im Verkauf angenommene Demo-Bestellungen erscheinen hier automatisch.</p></div><div class="posx-inventory-metrics"><b>' + active + '<small>In Vorbereitung</small></b><b class="ready">' + ready + '<small>Fertig</small></b></div></div>' +
      (demoOrders.length === 0
        ? '<div class="posx-empty-state"><strong>NOCH KEINE DEMO-BESTELLUNG</strong><span>Zur Kasse zurückkehren, Artikel hinzufügen und BESTELLUNG ANNEHMEN drücken.</span></div>'
        : '<div class="posx-order-board">' + demoOrders.map((order) =>
          '<article class="' + (order.status === 'ready' ? 'ready' : '') + '">' +
            '<div><span>ABHOLNUMMER</span><strong>' + order.number + '</strong></div>' +
            '<p><b>' + order.items + ' Artikel</b><span>' + euro(order.total) + ' · ' + order.time + '</span></p>' +
            '<button type="button" data-order-toggle="' + order.number + '">' + (order.status === 'ready' ? 'WIEDER IN VORBEREITUNG' : 'FERTIG · ABHOLBEREIT') + '</button>' +
          '</article>'
        ).join('') + '</div>') +
    '</div>';
  }

  function receiptHistoryMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>BON-HISTORIE</span><h3>Letzte Bons · Demo</h3><p>Neue Simulator-Verkäufe werden während dieser Browser-Sitzung automatisch ergänzt.</p></div><b class="posx-history-count">' + receiptHistory.length + ' Bons</b></div>' +
      '<div class="posx-data-table posx-receipt-table">' +
        '<div class="head"><b>Bon</b><b>Zeit</b><b>Zahlart</b><b>Summe</b><b>Status</b><b>Aktion</b></div>' +
        receiptHistory.map((item) => '<div class="' + item.status.toLowerCase() + '"><strong>#' + item.number + '</strong><span>' + item.time + '</span><span>' + (item.payment ?? '—') + '</span><b>' + euro(item.total) + '</b><span>' + item.status + '</span><button type="button" data-history-receipt="' + item.number + '">ANZEIGEN</button></div>').join('') +
      '</div>' +
    '</div>';
  }

  function zReportMarkup(): string {
    const okSales = receiptHistory.filter((item) => item.status === 'OK');
    const gross = okSales.reduce((sum, item) => sum + item.total, 0);
    const bar = okSales.reduce((sum, item) => sum + (item.payment === 'BAR' ? item.total : item.payment === 'GEMISCHT' ? (item.cashPortion ?? 0) : 0), 0);
    const card = okSales.reduce((sum, item) => sum + (item.payment === 'KARTE' ? item.total : item.payment === 'GEMISCHT' ? (item.cardPortion ?? 0) : 0), 0);
    const discounts = okSales.reduce((sum, item) => sum + Math.max(0, item.subtotal - item.total), 0);
    const storno = receiptHistory.filter((item) => item.status === 'STORNO').reduce((sum, item) => sum + item.total, 0);
    const returns = receiptHistory.filter((item) => item.status === 'RETOURE').reduce((sum, item) => sum + item.total, 0);

    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>Z-BERICHT · DEMO</span><h3>Tagesabschluss</h3><p>Nur Simulatorauswertung – kein fiskalischer Z-Abschluss und keine TSE-Transaktion.</p></div><button type="button" data-demo-action>Z-BERICHT DRUCKEN · DEMO</button></div>' +
      '<div class="posx-z-grid">' +
        '<article><span>BRUTTO UMSATZ</span><strong>' + euro(gross) + '</strong></article>' +
        '<article class="cash"><span>BAR</span><strong>' + euro(bar) + '</strong></article>' +
        '<article class="card"><span>KARTE</span><strong>' + euro(card) + '</strong></article>' +
        '<article><span>BONS</span><strong>' + okSales.length + '</strong></article>' +
        '<article class="discount"><span>RABATTE</span><strong>' + euro(discounts) + '</strong></article>' +
        '<article class="storno"><span>STORNOS</span><strong>' + euro(storno) + '</strong></article>' +
        '<article class="return"><span>RETOUREN</span><strong>' + euro(returns) + '</strong></article>' +
      '</div>' +
      '<div class="posx-exchange-note">DEMO · Dieser Bericht ersetzt keinen echten Z-Bericht, DSFinV-K Export oder TSE-Abschluss.</div>' +
    '</div>';
  }

  function cashMovementMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>EINLAGE / ENTNAHME</span><h3>Kassenbewegung · Demo</h3><p>Betrag und Grund können getestet werden; es wird kein realer Kassenbestand verändert.</p></div></div>' +
      '<form id="posx-movement-form" class="posx-movement-form">' +
        '<label>Art<select name="type"><option value="EINLAGE">EINLAGE</option><option value="ENTNAHME">ENTNAHME</option></select></label>' +
        '<label>Betrag<input name="amount" inputmode="decimal" placeholder="0,00"></label>' +
        '<label>Grund<input name="note" maxlength="60" placeholder="z. B. Wechselgeld"></label>' +
        '<button type="submit">DEMO BUCHEN</button>' +
      '</form>' +
      '<div class="posx-data-table posx-movement-table">' +
        '<div class="head"><b>Zeit</b><b>Art</b><b>Grund</b><b>Betrag</b></div>' +
        cashMovements.map((item) => '<div><span>' + item.time + '</span><strong class="' + item.type.toLowerCase() + '">' + item.type + '</strong><span>' + escapeHtml(item.note) + '</span><b>' + euro(item.amount) + '</b></div>').join('') +
      '</div>' +
    '</div>';
  }

  // ---------------------------------------------------------------- TOR Cloud portal preview
  // A receipt marked STORNO/RETOURE in the simulator is a sale that was later
  // reversed. TOR Cloud receives the sale and its counter-booking, so both
  // cancel out: turnover and cash/card only keep the sales that stand.
  function cloudTotals(): { gross: number; bar: number; card: number; sales: number; reversals: number } {
    const booked = receiptHistory.filter((item) => item.kind === 'sale');
    const standing = booked.filter((item) => item.status === 'OK');
    const portion = (item: HistoryReceipt, which: 'bar' | 'card'): number => item.payment === 'GEMISCHT'
      ? (which === 'bar' ? item.cashPortion ?? 0 : item.cardPortion ?? 0)
      : (item.payment === (which === 'bar' ? 'BAR' : 'KARTE') ? item.total : 0);
    return {
      gross: standing.reduce((sum, item) => sum + item.total, 0),
      bar: standing.reduce((sum, item) => sum + portion(item, 'bar'), 0),
      card: standing.reduce((sum, item) => sum + portion(item, 'card'), 0),
      sales: booked.length,
      reversals: booked.filter((item) => item.status !== 'OK').reduce((sum, item) => sum + item.total, 0)
    };
  }

  function cloudTableMarkup(className: string, head: string[], rows: string[][], empty: string): string {
    return '<div class="posx-data-table posx-cloud-table ' + className + '">' +
      '<div class="head">' + head.map((cell) => '<b>' + cell + '</b>').join('') + '</div>' +
      (rows.length ? rows.map((row) => '<div>' + row.join('') + '</div>').join('') : '<div class="posx-cloud-empty"><span>' + empty + '</span></div>') +
    '</div>';
  }

  function cloudDashboardMarkup(): string {
    const totals = cloudTotals();
    const recent = receiptHistory.filter((item) => item.kind === 'sale').slice(-8).reverse();
    return '<div class="posx-report-kpis">' +
        '<article><span>UMSATZ HEUTE</span><strong>' + euro(totals.gross) + '</strong></article>' +
        '<article><span>BONS</span><strong>' + totals.sales + '</strong></article>' +
        '<article><span>BAR</span><strong>' + euro(totals.bar) + '</strong></article>' +
        '<article><span>KARTE</span><strong>' + euro(totals.card) + '</strong></article>' +
      '</div>' +
      '<h4 class="posx-cloud-heading">Letzte Verkäufe</h4>' +
      cloudTableMarkup('posx-cloud-sales', ['Bon', 'Zeit', 'Zahlart', 'Art', 'Betrag'],
        recent.map((item) => ['<strong>#' + item.number + '</strong>', '<span>' + item.time + '</span>', '<span>' + (item.payment ?? '—') + '</span>',
          '<span>' + (item.status === 'OK' ? 'Verkauf' : item.status === 'STORNO' ? 'Verkauf · storniert' : 'Verkauf · retourniert') + '</span>', '<b>' + euro(item.total) + '</b>']),
        'Noch keine Verkäufe in dieser Sitzung.');
  }

  function cloudReportsMarkup(): string {
    const totals = cloudTotals();
    const today = new Date().toLocaleDateString(locale);
    return '<h4 class="posx-cloud-heading">' + escapeHtml(cloudPortal.reports.turnover) + '</h4>' +
      '<p class="posx-cloud-hint">Storno und Retoure sind abgezogen. In TOR Cloud frei wählbarer Zeitraum mit CSV-Download.</p>' +
      cloudTableMarkup('posx-cloud-turnover', ['Tag', 'Verkäufe', 'Storno / Retoure', 'Bar', 'Karte', 'Umsatz'],
        [['<strong>' + today + '</strong>', '<span>' + totals.sales + '</span>', '<span>' + euro(-totals.reversals) + '</span>', '<span>' + euro(totals.bar) + '</span>', '<span>' + euro(totals.card) + '</span>', '<b>' + euro(totals.gross) + '</b>']],
        '') +
      '<h4 class="posx-cloud-heading">' + escapeHtml(cloudPortal.reports.zArchive) + '</h4>' +
      cloudTableMarkup('posx-cloud-z', ['Z-Nr.', 'Zeitraum', 'Verkäufe', 'Bar', 'Karte', 'Brutto'],
        [['<strong>17</strong>', '<span>Vortag · Beispiel</span>', '<span>38</span>', '<span>' + euro(214.6) + '</span>', '<span>' + euro(187.9) + '</span>', '<b>' + euro(402.5) + '</b>']],
        '') +
      '<p class="posx-cloud-hint">Der Z-Bericht erscheint in TOR Cloud, sobald er an der Kasse abgeschlossen wird. Im Simulator ist nur ein Beispiel hinterlegt.</p>' +
      '<h4 class="posx-cloud-heading">' + escapeHtml(cloudPortal.reports.cashMovements) + '</h4>' +
      cloudTableMarkup('posx-cloud-cash', ['Zeit', 'Art', 'Grund', 'Betrag'],
        cashMovements.map((item) => ['<span>' + item.time + '</span>', '<strong class="' + item.type.toLowerCase() + '">' + (item.type === 'EINLAGE' ? 'Einlage' : 'Entnahme') + '</strong>',
          '<span>' + escapeHtml(item.note) + '</span>', '<b>' + (item.type === 'ENTNAHME' ? '−' : '') + euro(item.amount) + '</b>']),
        'Noch keine Einlage oder Entnahme.');
  }

  function cloudDevicesMarkup(): string {
    const until = new Date();
    until.setFullYear(until.getFullYear() + 4);
    const line = (label: string, value: string): string => '<div><span>' + escapeHtml(label) + '</span><b>' + escapeHtml(value) + '</b></div>';
    return '<div class="posx-cloud-device">' +
      '<div class="posx-cloud-device-head"><div><small>Filiale · Demo</small><h4>Kasse 1</h4><span>DEMO-KASSE-01 · TOR ' + escapeHtml(editionName()) + '</span></div><em>● Online</em></div>' +
      '<div class="posx-cloud-device-grid">' +
        line('Software', 'Online-Simulator') +
        line('TSE', 'Simuliert – keine TSE') +
        line(cloudPortal.deviceStatus.mode, 'Testbetrieb') +
        line(cloudPortal.deviceStatus.backlog, '0') +
        line(cloudPortal.deviceStatus.rejected, '0') +
        line(cloudPortal.deviceStatus.certificate, until.toLocaleDateString(locale) + ' · Beispiel') +
      '</div>' +
    '</div>';
  }

  function cloudMarkup(): string {
    const active: Record<CloudTab, string> = { dashboard: cloudPortal.menu[0] ?? 'Dashboard', reports: cloudPortal.menu.find((m) => /bericht/i.test(m)) ?? 'Berichte', devices: cloudPortal.menu.find((m) => /gerät/i.test(m)) ?? 'Gerätestatus' };
    const tabOf = (item: string): CloudTab | null => (Object.keys(active) as CloudTab[]).find((key) => active[key] === item) ?? null;
    return '<div class="posx-management-screen posx-cloud">' +
      '<div class="posx-management-title"><div><span>TOR CLOUD · VORSCHAU</span><h3>' + escapeHtml(cloudPortal.title) + '</h3><p>' + escapeHtml(cloudPortal.previewNote) + '</p></div></div>' +
      '<div class="posx-cloud-layout">' +
        '<nav class="posx-cloud-nav" aria-label="TOR Cloud">' +
          cloudPortal.menu.map((item) => {
            const tab = tabOf(item);
            return tab
              ? '<button type="button" data-cloud-tab="' + tab + '" class="' + (tab === cloudTab ? 'active' : '') + '">' + escapeHtml(item) + '</button>'
              : '<button type="button" disabled title="In der Vorschau nicht enthalten">' + escapeHtml(item) + '</button>';
          }).join('') +
        '</nav>' +
        '<section class="posx-cloud-main">' +
          (cloudTab === 'reports' ? cloudReportsMarkup() : cloudTab === 'devices' ? cloudDevicesMarkup() : cloudDashboardMarkup()) +
        '</section>' +
      '</div>' +
      '<div class="posx-exchange-note">DEMO · Die echte TOR Cloud zeigt diese Übersicht mit den Daten der eigenen Kassen – verschlüsselt, getrennt pro Betrieb und mit Zwei-Faktor-Anmeldung.</div>' +
    '</div>';
  }

  function reportSales(): HistoryReceipt[] {
    return receiptHistory.filter((item) => item.kind === 'sale' && item.status === 'OK');
  }

  function reportTotals(): { gross: number; bar: number; card: number; discounts: number; storno: number; returns: number } {
    const sales = reportSales();
    return {
      gross: sales.reduce((sum, item) => sum + item.total, 0),
      bar: sales.reduce((sum, item) => sum + (item.payment === 'BAR' ? item.total : item.payment === 'GEMISCHT' ? (item.cashPortion ?? 0) : 0), 0),
      card: sales.reduce((sum, item) => sum + (item.payment === 'KARTE' ? item.total : item.payment === 'GEMISCHT' ? (item.cardPortion ?? 0) : 0), 0),
      discounts: sales.reduce((sum, item) => sum + Math.max(0, item.subtotal - item.total), 0),
      storno: receiptHistory.filter((item) => item.status === 'STORNO').reduce((sum, item) => sum + item.total, 0),
      returns: receiptHistory.filter((item) => item.status === 'RETOURE').reduce((sum, item) => sum + item.total, 0)
    };
  }

  function reportsHomeMarkup(): string {
    return '<div class="posx-management-screen">' +
      '<div class="posx-management-title"><div><span>BERICHTE</span><h3>Berichtszentrum</h3><p>Alle Werte stammen ausschließlich aus dieser Demo-Sitzung und den eingebauten Beispieldaten.</p></div></div>' +
      '<div class="posx-management-cards posx-report-cards">' +
        '<section><strong>TAGESKONTROLLE</strong><button type="button" data-management="xreport">X-Bericht</button><button type="button" data-management="cashCount">Kassensturz</button><button type="button" data-management="cashJournal">Kassenjournal</button></section>' +
        '<section><strong>VERKAUF / BESTAND</strong><button type="button" data-management="turnover">Umsatzberichte</button><button type="button" data-management="monthly">Monatsbericht / Monatsumsatz</button><button type="button" data-management="salesStats">Verkaufsstatistik · Artikel</button><button type="button" data-management="inventoryReport">Warenbestand</button></section>' +
        '<section><strong>ARCHIV / PERSONAL</strong><button type="button" data-demo-action>Z-Abschluss-Journal</button><button type="button" data-management="operatorReport">Bedienerabrechnung</button><button type="button" data-management="stornoReport">Stornobericht</button><button type="button" data-demo-action>Personalüberwachung</button></section>' +
        '<section><strong>TOR CLOUD</strong><button type="button" data-management="cloud">Kundenportal · Vorschau</button></section>' +
        '<section><strong>E-MAIL / PDF</strong><button type="button" data-demo-action>Bericht als PDF · Demo</button><button type="button" data-demo-action>Bericht per E-Mail · Demo</button></section>' +
      '</div>' +
    '</div>';
  }

  function reportHero(title: string, subtitle: string, action = 'DRUCKEN · DEMO'): string {
    return '<div class="posx-management-title"><div><span>BERICHTE · DEMO</span><h3>' + title + '</h3><p>' + subtitle + '</p></div><button type="button" data-demo-action>' + action + '</button></div>';
  }

  function xReportMarkup(): string {
    const totals = reportTotals();
    const movementNet = cashMovements.reduce((sum, item) => sum + (item.type === 'EINLAGE' ? item.amount : -item.amount), 0);
    return '<div class="posx-management-screen">' +
      reportHero('X-Bericht', 'Zwischenstand ohne Tagesabschluss. Keine TSE- oder Z-Abschlusswirkung.') +
      '<div class="posx-z-grid">' +
        '<article><span>BRUTTO UMSATZ</span><strong>' + euro(totals.gross) + '</strong></article>' +
        '<article class="cash"><span>BAR</span><strong>' + euro(totals.bar) + '</strong></article>' +
        '<article class="card"><span>KARTE</span><strong>' + euro(totals.card) + '</strong></article>' +
        '<article><span>BONS</span><strong>' + reportSales().length + '</strong></article>' +
        '<article class="discount"><span>RABATTE</span><strong>' + euro(totals.discounts) + '</strong></article>' +
        '<article><span>KASSENBEWEGUNGEN</span><strong>' + euro(movementNet) + '</strong></article>' +
        '<article class="storno"><span>STORNOS</span><strong>' + euro(totals.storno) + '</strong></article>' +
        '<article class="return"><span>RETOUREN</span><strong>' + euro(totals.returns) + '</strong></article>' +
      '</div>' +
    '</div>';
  }

  function cashCountMarkup(): string {
    const totals = reportTotals();
    const deposits = cashMovements.filter((item) => item.type === 'EINLAGE').reduce((sum, item) => sum + item.amount, 0);
    const withdrawals = cashMovements.filter((item) => item.type === 'ENTNAHME').reduce((sum, item) => sum + item.amount, 0);
    const expected = totals.bar + deposits - withdrawals;
    return '<div class="posx-management-screen">' +
      reportHero('Kassensturz', 'Soll-Bestand aus Demo-Barumsatz sowie Einlagen und Entnahmen.') +
      '<div class="posx-cash-count">' +
        '<div><span>BARUMSATZ</span><strong>' + euro(totals.bar) + '</strong></div>' +
        '<div><span>+ EINLAGEN</span><strong>' + euro(deposits) + '</strong></div>' +
        '<div><span>− ENTNAHMEN</span><strong>' + euro(withdrawals) + '</strong></div>' +
        '<div class="total"><span>SOLL KASSENBESTAND</span><strong>' + euro(expected) + '</strong></div>' +
      '</div>' +
      '<form id="posx-cash-count-form" class="posx-cash-count-form"><label>Gezählter Ist-Bestand<input name="actual" inputmode="decimal" placeholder="' + expected.toFixed(2) + '"></label><button type="submit">DIFFERENZ BERECHNEN</button></form>' +
      '<div id="posx-cash-diff" class="posx-cash-diff">Noch keine Ist-Zählung eingegeben.</div>' +
    '</div>';
  }

  function cashJournalMarkup(): string {
    const salesRows = reportSales().map((item) => ({
      time: item.time,
      type: 'VERKAUF',
      note: 'Bon #' + item.number + ' · ' + (item.payment ?? '—'),
      amount: item.total
    }));
    const movementRows = cashMovements.map((item) => ({
      time: item.time,
      type: item.type,
      note: item.note,
      amount: item.type === 'EINLAGE' ? item.amount : -item.amount
    }));
    const rows = [...salesRows, ...movementRows].sort((a, b) => a.time.localeCompare(b.time));
    return '<div class="posx-management-screen">' +
      reportHero('Kassenjournal', 'Chronologische Demo-Übersicht aus Verkäufen und Kassenbewegungen.') +
      '<div class="posx-data-table posx-journal-table">' +
        '<div class="head"><b>Zeit</b><b>Vorgang</b><b>Details</b><b>Betrag</b></div>' +
        rows.map((row) => '<div><span>' + row.time + '</span><strong>' + row.type + '</strong><span>' + escapeHtml(row.note) + '</span><b class="' + (row.amount < 0 ? 'negative' : '') + '">' + euro(row.amount) + '</b></div>').join('') +
      '</div>' +
    '</div>';
  }

  function turnoverMarkup(): string {
    const totals = reportTotals();
    const avg = reportSales().length ? totals.gross / reportSales().length : 0;
    return '<div class="posx-management-screen">' +
      reportHero('Umsatzberichte', 'Demo-Umsatz nach Zahlart und durchschnittlichem Bonwert.') +
      '<div class="posx-report-kpis">' +
        '<article><span>UMSATZ GESAMT</span><strong>' + euro(totals.gross) + '</strong></article>' +
        '<article><span>BAR</span><strong>' + euro(totals.bar) + '</strong></article>' +
        '<article><span>KARTE</span><strong>' + euro(totals.card) + '</strong></article>' +
        '<article><span>Ø BON</span><strong>' + euro(avg) + '</strong></article>' +
      '</div>' +
      '<div class="posx-payment-bars"><div><span>BAR</span><i style="--share:' + (totals.gross ? (totals.bar / totals.gross * 100) : 0) + '%"></i><b>' + euro(totals.bar) + '</b></div><div><span>KARTE</span><i style="--share:' + (totals.gross ? (totals.card / totals.gross * 100) : 0) + '%"></i><b>' + euro(totals.card) + '</b></div></div>' +
    '</div>';
  }

  function monthlyMarkup(): string {
    const totals = reportTotals();
    const simulatedBase = 6840;
    const simulatedMonth = simulatedBase + totals.gross;
    return '<div class="posx-management-screen">' +
      reportHero('Monatsbericht / Monatsumsatz', 'Beispielmonat plus aktuelle Demo-Sitzung. Keine echten Geschäftsdaten.') +
      '<div class="posx-month-grid">' +
        '<article><span>01.–07.</span><strong>' + euro(1540 + totals.gross * .18) + '</strong></article>' +
        '<article><span>08.–14.</span><strong>' + euro(1710 + totals.gross * .22) + '</strong></article>' +
        '<article><span>15.–21.</span><strong>' + euro(1660 + totals.gross * .25) + '</strong></article>' +
        '<article><span>22.–Monatsende</span><strong>' + euro(1930 + totals.gross * .35) + '</strong></article>' +
      '</div>' +
      '<div class="posx-month-total"><span>MONATSUMSATZ · DEMO</span><strong>' + euro(simulatedMonth) + '</strong><small>Enthält ' + euro(totals.gross) + ' aus der aktuellen Simulator-Sitzung.</small></div>' +
    '</div>';
  }

  function salesStatsMarkup(): string {
    const map = new Map<string, { qty: number; revenue: number }>();
    for (const sale of reportSales()) {
      for (const line of sale.lines) {
        const current = map.get(line.name) ?? { qty: 0, revenue: 0 };
        current.qty += line.qty;
        current.revenue += line.qty * line.price * (1 - sale.discountPct / 100);
        map.set(line.name, current);
      }
    }
    const rows = [...map.entries()].map(([name, value]) => ({ name, ...value })).sort((a, b) => b.revenue - a.revenue);
    return '<div class="posx-management-screen">' +
      reportHero('Verkaufsstatistik · Artikel', 'Artikelranking aus gültigen Demo-Bons dieser Sitzung.') +
      '<div class="posx-data-table posx-sales-table">' +
        '<div class="head"><b>Rang</b><b>Artikel</b><b>Menge</b><b>Umsatz</b></div>' +
        (rows.length ? rows.map((row, index) => '<div><strong>#' + (index + 1) + '</strong><span>' + escapeHtml(row.name) + '</span><b>' + row.qty + '</b><strong>' + euro(row.revenue) + '</strong></div>').join('') : '<div class="empty-row"><span>Noch keine gültigen Demo-Verkäufe.</span></div>') +
      '</div>' +
    '</div>';
  }

  function inventoryReportMarkup(): string {
    const rows = demoInventoryRows();
    const value = rows.reduce((sum, item) => sum + item.stock * item.ek, 0);
    const low = rows.filter((item) => item.stock < item.min);
    return '<div class="posx-management-screen">' +
      reportHero('Warenbestand', 'Demo-Bestand mit Einkaufspreis-Warenwert und Mindestbestand.') +
      '<div class="posx-report-kpis"><article><span>ARTIKEL</span><strong>' + rows.length + '</strong></article><article><span>WARENWERT EK</span><strong>' + euro(value) + '</strong></article><article class="warning"><span>NIEDRIGER BESTAND</span><strong>' + low.length + '</strong></article></div>' +
      '<div class="posx-data-table posx-stock-report-table"><div class="head"><b>Artikel</b><b>Bestand</b><b>Min.</b><b>EK</b><b>Warenwert</b></div>' +
      rows.map((item) => '<div class="' + (item.stock < item.min ? 'low' : '') + '"><span>' + escapeHtml(item.name) + '</span><b>' + item.stock + '</b><span>' + item.min + '</span><span>' + euro(item.ek) + '</span><strong>' + euro(item.stock * item.ek) + '</strong></div>').join('') +
      '</div></div>';
  }

  function operatorReportMarkup(): string {
    const totals = reportTotals();
    return '<div class="posx-management-screen">' +
      reportHero('Bedienerabrechnung', 'Demo-Abrechnung für den angemeldeten Bediener admin.') +
      '<div class="posx-operator-card"><div><span>BEDIENER</span><strong>admin</strong></div><div><span>BONS</span><strong>' + reportSales().length + '</strong></div><div><span>UMSATZ</span><strong>' + euro(totals.gross) + '</strong></div><div><span>BAR</span><strong>' + euro(totals.bar) + '</strong></div><div><span>KARTE</span><strong>' + euro(totals.card) + '</strong></div></div>' +
    '</div>';
  }

  function stornoReportMarkup(): string {
    const rows = receiptHistory.filter((item) => item.status === 'STORNO' || item.status === 'RETOURE');
    return '<div class="posx-management-screen">' +
      reportHero('Stornobericht', 'Stornos und Retouren bleiben als getrennte Demo-Ereignisse sichtbar.') +
      '<div class="posx-data-table posx-storno-table"><div class="head"><b>Bon</b><b>Zeit</b><b>Art</b><b>Zahlart</b><b>Betrag</b></div>' +
      rows.map((item) => '<div class="' + item.status.toLowerCase() + '"><strong>#' + item.number + '</strong><span>' + item.time + '</span><b>' + item.status + '</b><span>' + (item.payment ?? '—') + '</span><strong>' + euro(item.total) + '</strong></div>').join('') +
      '</div><div class="posx-exchange-note">DEMO · Keine echten Stornos, Retouren oder Fiskaltransaktionen werden ausgelöst.</div></div>';
  }

  function managementMarkup(): string {
    if (screen === 'settings') return settingsMarkup();
    if (screen === 'artikel') return artikelMarkup();
    if (screen === 'inventur') return inventurMarkup();
    if (screen === 'exchange') return exchangeMarkup();
    if (screen === 'waren') return warenHomeMarkup();
    if (screen === 'orders') return ordersMarkup();
    if (screen === 'receiptHistory') return receiptHistoryMarkup();
    if (screen === 'zreport') return zReportMarkup();
    if (screen === 'cashMovement') return cashMovementMarkup();
    if (screen === 'reports') return reportsHomeMarkup();
    if (screen === 'xreport') return xReportMarkup();
    if (screen === 'cashCount') return cashCountMarkup();
    if (screen === 'cashJournal') return cashJournalMarkup();
    if (screen === 'turnover') return turnoverMarkup();
    if (screen === 'monthly') return monthlyMarkup();
    if (screen === 'salesStats') return salesStatsMarkup();
    if (screen === 'inventoryReport') return inventoryReportMarkup();
    if (screen === 'operatorReport') return operatorReportMarkup();
    if (screen === 'stornoReport') return stornoReportMarkup();
    if (screen === 'cloud') return cloudMarkup();
    return kasseHomeMarkup();
  }

  function payChoiceMarkup(): string {
    if (panel !== 'payChoice') return '';
    const currentTotal = total();
    const serviceChoice = mode === 'gastro'
      ? '<div class="posx-service-type"><span>VERKAUFSART</span><div><button type="button" class="' + (outside ? 'active' : '') + '" data-service-outside>AUSSER HAUS<small>STANDARD</small></button><button type="button" class="' + (!outside ? 'active inside' : 'inside') + '" data-service-inside>IM HAUS</button></div><p>AUSSER HAUS ist für jeden neuen Verkauf automatisch vorausgewählt.</p></div>'
      : '';
    const cashReady = cashGiven >= currentTotal;
    const cardPart = Math.max(0, Math.round((currentTotal - mixedCash) * 100) / 100);
    const mixedReady = mixedCash > 0 && mixedCash < currentTotal;
    const cashDetails = '<div class="posx-pay-detail">' +
      '<div class="posx-detail-label">BARZAHLUNG · GEGEBEN</div>' +
      '<div class="posx-cash-presets"><button type="button" data-cash-preset="' + currentTotal + '">PASSEND<br>' + euro(currentTotal) + '</button>' + [10, 20, 50].filter((value) => value >= currentTotal).map((value) => '<button type="button" data-cash-preset="' + value + '">' + euro(value) + '</button>').join('') + '</div>' +
      '<form id="posx-cash-form"><label>' + text.amountGiven + '<input name="amount" inputmode="decimal" value="' + cashGiven.toFixed(2) + '"></label><button type="submit">OK</button></form>' +
      '<div class="posx-change"><span>' + text.change + '</span><strong>' + euro(cashReady ? cashGiven - currentTotal : 0) + '</strong></div>' +
      '</div>';
    const cardDetails = '<div class="posx-pay-detail"><div class="posx-detail-label">KARTENZAHLUNG</div><div class="posx-terminal"><span>TOR POS</span><strong>' + euro(currentTotal) + '</strong><small>SUMUP / ZVT · DEMO</small></div></div>';
    const mixedDetails = '<div class="posx-pay-detail">' +
      '<div class="posx-detail-label">GEMISCHT · BAR + KARTE</div>' +
      '<form id="posx-mixed-form" class="posx-mixed-form"><label>BAR-ANTEIL<input name="cash" inputmode="decimal" value="' + mixedCash.toFixed(2) + '"></label><button type="submit">BERECHNEN</button></form>' +
      '<div class="posx-mixed-split"><div><span>BAR</span><strong>' + euro(mixedCash) + '</strong></div><div><span>KARTE</span><strong>' + euro(cardPart) + '</strong></div></div>' +
      '</div>';
    const detail = payMethod === 'BAR' ? cashDetails : payMethod === 'KARTE' ? cardDetails : mixedDetails;
    const canFinish = payMethod === 'BAR' ? cashReady : payMethod === 'KARTE' ? currentTotal >= 0 : mixedReady;
    const finishLabel = payMethod === 'BAR' ? text.finish : payMethod === 'KARTE' ? text.simulateCard : 'GEMISCHT ABSCHLIESSEN';
    const finishClass = payMethod === 'BAR' ? 'cash' : payMethod === 'KARTE' ? 'card' : 'mixed';
    const finishAction = payMethod === 'BAR' ? 'data-finish-cash' : payMethod === 'KARTE' ? 'data-finish-card' : 'data-finish-mixed';
    return '<div class="posx-modal-backdrop"><div class="posx-modal posx-pay-choice unified">' +
      '<div class="posx-modal-head"><strong>' + text.choosePay + '</strong><button type="button" data-close-panel>×</button></div>' +
      '<div class="posx-payment-total compact"><span>' + text.total + '</span><strong>' + euro(currentTotal) + '</strong></div>' +
      serviceChoice +
      '<div class="posx-payment-section-label">ZAHLART</div>' +
      '<div class="posx-pay-buttons"><button type="button" class="cash ' + (payMethod === 'BAR' ? 'active' : '') + '" data-pay-cash>' + text.cash + '<small>F1</small></button><button type="button" class="card ' + (payMethod === 'KARTE' ? 'active' : '') + '" data-pay-card>' + text.card + '<small>F2</small></button><button type="button" class="mixed ' + (payMethod === 'GEMISCHT' ? 'active' : '') + '" data-pay-mixed>GEMISCHT<small>BAR + KARTE</small></button></div>' +
      detail +
      '<div class="posx-modal-actions"><button type="button" class="posx-cancel" data-close-panel>' + text.cancel + '</button><button type="button" class="posx-finish ' + finishClass + '" ' + finishAction + (canFinish ? '' : ' disabled') + '>' + finishLabel + '</button></div>' +
    '</div></div>';
  }

  function cashMarkup(): string {
    if (panel !== 'cash') return '';
    const canFinish = cashGiven >= total();
    return '<div class="posx-modal-backdrop"><div class="posx-modal">' +
      '<div class="posx-modal-head"><strong>' + text.cashTitle + '</strong><button type="button" data-close-panel>×</button></div>' +
      '<div class="posx-payment-total"><span>' + text.total + '</span><strong>' + euro(total()) + '</strong></div>' +
      '<div class="posx-cash-presets">' + [10, 20, 50, 100].map((value) => '<button type="button" data-cash-preset="' + value + '">' + euro(value) + '</button>').join('') + '</div>' +
      '<form id="posx-cash-form"><label>' + text.amountGiven + '<input name="amount" inputmode="decimal" value="' + cashGiven.toFixed(2) + '"></label><button type="submit">OK</button></form>' +
      '<div class="posx-change"><span>' + text.change + '</span><strong>' + euro(canFinish ? cashGiven - total() : 0) + '</strong></div>' +
      '<div class="posx-modal-actions"><button type="button" class="posx-cancel" data-close-panel>' + text.cancel + '</button><button type="button" class="posx-finish cash" data-finish-cash' + (canFinish ? '' : ' disabled') + '>' + text.finish + '</button></div>' +
    '</div></div>';
  }

  function cardMarkup(): string {
    if (panel !== 'card') return '';
    return '<div class="posx-modal-backdrop"><div class="posx-modal">' +
      '<div class="posx-modal-head"><strong>' + text.cardTitle + '</strong><button type="button" data-close-panel>×</button></div>' +
      '<div class="posx-terminal"><span>TOR POS</span><strong>' + euro(total()) + '</strong><small>SUMUP / ZVT · DEMO</small></div>' +
      '<div class="posx-modal-actions"><button type="button" class="posx-cancel" data-close-panel>' + text.cancel + '</button><button type="button" class="posx-finish card" data-finish-card>' + text.simulateCard + '</button></div>' +
    '</div></div>';
  }

  function mixedMarkup(): string {
    if (panel !== 'mixed') return '';
    const cardPart = Math.max(0, Math.round((total() - mixedCash) * 100) / 100);
    const canFinish = mixedCash > 0 && mixedCash < total();
    return '<div class="posx-modal-backdrop"><div class="posx-modal">' +
      '<div class="posx-modal-head"><strong>GEMISCHTE ZAHLUNG</strong><button type="button" data-close-panel>×</button></div>' +
      '<div class="posx-payment-total"><span>' + text.total + '</span><strong>' + euro(total()) + '</strong></div>' +
      '<form id="posx-mixed-form" class="posx-mixed-form"><label>BAR-ANTEIL<input name="cash" inputmode="decimal" value="' + mixedCash.toFixed(2) + '"></label><button type="submit">BERECHNEN</button></form>' +
      '<div class="posx-mixed-split"><div><span>BAR</span><strong>' + euro(mixedCash) + '</strong></div><div><span>KARTE</span><strong>' + euro(cardPart) + '</strong></div></div>' +
      '<div class="posx-modal-actions"><button type="button" class="posx-cancel" data-close-panel>' + text.cancel + '</button><button type="button" class="posx-finish mixed" data-finish-mixed' + (canFinish ? '' : ' disabled') + '>GEMISCHT ABSCHLIESSEN</button></div>' +
    '</div></div>';
  }

  function searchMarkup(): string {
    if (panel !== 'search') return '';
    const results = searchResults();
    return '<div class="posx-modal-backdrop"><div class="posx-modal">' +
      '<div class="posx-modal-head"><strong>' + text.searchTitle + '</strong><button type="button" data-close-panel>×</button></div>' +
      '<form id="posx-search-form" class="posx-search-form"><input name="query" value="' + escapeHtml(searchQuery) + '" placeholder="' + text.searchPlaceholder + '" autofocus><button type="submit">' + text.searchButton + '</button></form>' +
      '<div class="posx-search-results">' +
        (searchQuery && results.length === 0 ? '<p>' + text.searchNone + '</p>' : results.map((product) => '<button type="button" data-search-product="' + escapeHtml(product.id) + '"><span>' + escapeHtml(product.name) + '</span><strong>' + euro(product.price) + '</strong></button>').join('')) +
      '</div>' +
    '</div></div>';
  }

  function receiptMarkup(): string {
    if (!receipt) return '';
    const current = receipt;
    const order = current.kind === 'order';
    return '<div class="posx-modal-backdrop"><div class="posx-modal posx-receipt">' +
      '<div class="posx-modal-head"><strong>' + (order ? text.orderTicket : text.receipt) + '</strong><button type="button" data-close-receipt>×</button></div>' +
      (order ? '<div class="posx-order-number"><span>' + text.orderNumber + '</span><strong>' + current.number + '</strong><b>' + text.preparing + '</b></div>' : '') +
      '<div class="posx-paper">' +
        '<div class="posx-paper-brand">TOR POS · DEMO</div>' +
        current.lines.map((line) => '<div><span>' + line.qty + ' × ' + escapeHtml(line.name) + '</span><strong>' + euro(line.qty * line.price) + '</strong></div>').join('') +
        '<hr>' +
        '<div><span>' + text.subtotal + '</span><strong>' + euro(current.subtotal) + '</strong></div>' +
        (current.discountPct ? '<div><span>' + text.discountLine + ' ' + current.discountPct + '%</span><strong>−' + euro(current.subtotal - current.total) + '</strong></div>' : '') +
        '<div class="total"><span>' + text.total + '</span><strong>' + euro(current.total) + '</strong></div>' +
        '<div><span>VERKAUFSART</span><strong>' + (current.imHaus ? 'IM HAUS' : 'AUSSER HAUS') + '</strong></div>' +
        (current.payment ? '<div><span>' + text.payment + '</span><strong>' + current.payment + '</strong></div>' : '') +
        (current.payment === 'BAR' && current.cashGiven !== undefined ? '<div><span>' + text.received + '</span><strong>' + euro(current.cashGiven) + '</strong></div><div><span>' + text.change + '</span><strong>' + euro(current.change ?? 0) + '</strong></div>' : '') +
        (current.payment === 'GEMISCHT' ? '<div><span>BAR</span><strong>' + euro(current.cashPortion ?? 0) + '</strong></div><div><span>KARTE</span><strong>' + euro(current.cardPortion ?? 0) + '</strong></div>' : '') +
        '<p>' + text.tseDemo + '</p>' +
      '</div>' +
      '<button type="button" class="posx-continue" data-close-receipt>' + text.continueSale + '</button>' +
    '</div></div>';
  }

  function render(): void {
    root.innerHTML = '<div class="posx-shell">' +
      '<div class="posx-window">' +
        topMarkup() +
        (screen === 'cashier' ? quickbarMarkup() : managementBarMarkup()) +
        (screen === 'cashier'
          ? '<div class="posx-body">' + leftMarkup() + rightMarkup() + '</div>'
          : managementMarkup()) +
        '<div class="posx-bottom"><strong>' + text.keyboard + '</strong><span>' + text.keyboardHint + '</span><em>' + escapeHtml(status) + '</em></div>' +
      '</div>' +
      '<p class="posx-disclaimer">' + text.tseDemo + ' · Browser-Simulator</p>' +
      payChoiceMarkup() + cashMarkup() + cardMarkup() + mixedMarkup() + searchMarkup() + receiptMarkup() +
    '</div>';

    bind();
  }

  function bind(): void {
    root.querySelector<HTMLButtonElement>('[data-switch-mode]')?.addEventListener('click', switchMode);

    root.querySelector<HTMLButtonElement>('[data-open-settings]')?.addEventListener('click', () => {
      screen = 'settings';
      panel = null;
      status = '';
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-open-waren]')?.addEventListener('click', () => {
      screen = 'waren';
      panel = null;
      status = '';
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-open-kasse]')?.addEventListener('click', () => {
      screen = 'kasse';
      panel = null;
      status = '';
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-open-reports]')?.addEventListener('click', () => {
      screen = 'reports';
      panel = null;
      status = '';
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-back-cashier]')?.addEventListener('click', () => {
      screen = 'cashier';
      status = '';
      render();
    });

    root.querySelectorAll<HTMLButtonElement>('[data-management]').forEach((button) => {
      button.addEventListener('click', () => {
        const next = button.dataset.management as Screen | undefined;
        if (
          next === 'artikel' ||
          next === 'inventur' ||
          next === 'exchange' ||
          next === 'orders' ||
          next === 'receiptHistory' ||
          next === 'zreport' ||
          next === 'cashMovement' ||
          next === 'xreport' ||
          next === 'cashCount' ||
          next === 'cashJournal' ||
          next === 'turnover' ||
          next === 'monthly' ||
          next === 'salesStats' ||
          next === 'inventoryReport' ||
          next === 'operatorReport' ||
          next === 'stornoReport' ||
          next === 'cloud'
        ) {
          screen = next;
          render();
        }
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-cloud-tab]').forEach((button) => {
      button.addEventListener('click', () => {
        const next = button.dataset.cloudTab;
        if (next !== 'dashboard' && next !== 'reports' && next !== 'devices') return;
        cloudTab = next;
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-settings-section]').forEach((button) => {
      button.addEventListener('click', () => {
        const next = button.dataset.settingsSection;
        if (!next || !settingsSections.some((item) => item.title === next)) return;
        settingsSection = next;
        status = '';
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-order-toggle]').forEach((button) => {
      button.addEventListener('click', () => {
        const number = Number(button.dataset.orderToggle);
        const order = demoOrders.find((item) => item.number === number);
        if (!order) return;
        order.status = order.status === 'preparing' ? 'ready' : 'preparing';
        openOrders = demoOrders.filter((item) => item.status === 'preparing').length;
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-history-receipt]').forEach((button) => {
      button.addEventListener('click', () => {
        const number = Number(button.dataset.historyReceipt);
        const item = receiptHistory.find((history) => history.number === number);
        if (!item) return;
        receipt = { ...item };
        render();
      });
    });

    root.querySelector<HTMLFormElement>('#posx-movement-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const form = new FormData(event.currentTarget);
      const type = form.get('type') === 'ENTNAHME' ? 'ENTNAHME' : 'EINLAGE';
      const amount = Number(String(form.get('amount') ?? '').replace(',', '.'));
      const note = String(form.get('note') ?? '').trim();
      if (!Number.isFinite(amount) || amount <= 0 || !note) {
        status = lang === 'de' ? 'Bitte Betrag und Grund eingeben.' : 'Lütfen tutar ve açıklama girin.';
        render();
        return;
      }
      cashMovements.unshift({
        type,
        amount,
        note,
        time: new Date().toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
      });
      status = lang === 'de'
        ? type + ' wurde nur als Demo gebucht.'
        : type + ' yalnızca demo olarak işlendi.';
      render();
    });

    root.querySelector<HTMLFormElement>('#posx-cash-count-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const form = new FormData(event.currentTarget);
      const actual = Number(String(form.get('actual') ?? '').replace(',', '.'));
      const totals = reportTotals();
      const deposits = cashMovements.filter((item) => item.type === 'EINLAGE').reduce((sum, item) => sum + item.amount, 0);
      const withdrawals = cashMovements.filter((item) => item.type === 'ENTNAHME').reduce((sum, item) => sum + item.amount, 0);
      const expected = totals.bar + deposits - withdrawals;
      const target = root.querySelector<HTMLElement>('#posx-cash-diff');
      if (!target || !Number.isFinite(actual)) return;
      const diff = actual - expected;
      target.innerHTML = '<span>DIFFERENZ</span><strong>' + euro(diff) + '</strong><small>' + (Math.abs(diff) < .005 ? 'Kasse stimmt.' : diff > 0 ? 'Überbestand · Demo' : 'Fehlbetrag · Demo') + '</small>';
      target.classList.toggle('ok', Math.abs(diff) < .005);
      target.classList.toggle('warn', Math.abs(diff) >= .005);
    });

    root.querySelectorAll<HTMLButtonElement>('[data-demo-action]').forEach((button) => {
      button.addEventListener('click', () => {
        status = lang === 'de'
          ? 'Demo-Funktion: keine produktiven Daten werden verändert.'
          : 'Demo işlevi: gerçek veriler değiştirilmez.';
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-count-minus]').forEach((button) => {
      button.addEventListener('click', () => {
        const input = button.parentElement?.querySelector<HTMLInputElement>('input');
        if (!input) return;
        input.value = String(Math.max(0, Number(input.value || 0) - 1));
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-count-plus]').forEach((button) => {
      button.addEventListener('click', () => {
        const input = button.parentElement?.querySelector<HTMLInputElement>('input');
        if (!input) return;
        input.value = String(Math.max(0, Number(input.value || 0) + 1));
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-menu]').forEach((button) => {
      button.addEventListener('click', () => {
        status = text.menuDemo;
        render();
      });
    });

    root.querySelector<HTMLButtonElement>('[data-sim-logout]')?.addEventListener('click', () => {
      view = 'categories';
      categoryId = categories()[0].id;
      openOrders = 0;
      resetSale('');
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-open-search]')?.addEventListener('click', () => {
      panel = 'search';
      searchQuery = '';
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-quick-item]')?.addEventListener('click', addQuickItem);

    root.querySelector<HTMLButtonElement>('[data-discount]')?.addEventListener('click', () => {
      if (!requireCart()) return;
      discountPct = discountPct ? 0 : 10;
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-receipt-toggle]')?.addEventListener('click', () => {
      receiptEnabled = !receiptEnabled;
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-checkout]')?.addEventListener('click', openPayChoice);

    root.querySelectorAll<HTMLButtonElement>('[data-category]').forEach((button) => {
      button.addEventListener('click', () => selectCategory(button.dataset.category ?? ''));
    });

    root.querySelector<HTMLButtonElement>('[data-back]')?.addEventListener('click', () => {
      view = 'categories';
      render();
    });

    root.querySelectorAll<HTMLButtonElement>('[data-product]').forEach((button) => {
      button.addEventListener('click', () => {
        const product = category().products.find((item) => item.id === button.dataset.product);
        if (product) addProduct(product);
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-select-line]').forEach((button) => {
      button.addEventListener('click', () => {
        selectedId = button.dataset.selectLine ?? '';
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-key-action]').forEach((button) => {
      button.addEventListener('click', () => {
        const action = button.dataset.keyAction;
        const value = button.dataset.keyValue ?? '';

        if (action === 'digit') numericInput += value;
        else if (action === 'comma' && !numericInput.includes(',')) numericInput += numericInput ? ',' : '0,';
        else if (action === 'clear') numericInput = '';
        else if (action === 'multiply') {
          applyNumericQty();
          return;
        } else if (action === 'plus') {
          adjustQty(1);
          return;
        } else if (action === 'minus') {
          adjustQty(-1);
          return;
        } else if (action === 'storno') {
          removeSelected();
          return;
        }
        render();
      });
    });

    root.querySelector<HTMLButtonElement>('[data-extra]')?.addEventListener('click', () => {
      status = text.extraDemo;
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-order]')?.addEventListener('click', acceptOrder);
    // R166: one visible KASSIEREN button opens one payment page. BAR/KARTE
    // only switch the detail area inside that same page.
    root.querySelector<HTMLButtonElement>('[data-pay-cash]')?.addEventListener('click', openCash);
    root.querySelector<HTMLButtonElement>('[data-pay-card]')?.addEventListener('click', openCard);
    root.querySelector<HTMLButtonElement>('[data-pay-mixed]')?.addEventListener('click', openMixed);
    root.querySelector<HTMLButtonElement>('[data-service-outside]')?.addEventListener('click', () => {
      outside = true;
      render();
    });
    root.querySelector<HTMLButtonElement>('[data-service-inside]')?.addEventListener('click', () => {
      outside = false;
      render();
    });

    root.querySelectorAll<HTMLButtonElement>('[data-close-panel]').forEach((button) => {
      button.addEventListener('click', () => {
        panel = null;
        render();
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-cash-preset]').forEach((button) => {
      button.addEventListener('click', () => {
        cashGiven = Number(button.dataset.cashPreset ?? 0);
        render();
      });
    });

    root.querySelector<HTMLFormElement>('#posx-cash-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const value = new FormData(event.currentTarget).get('amount');
      const parsed = Number(String(value ?? '').replace(',', '.'));
      cashGiven = Number.isFinite(parsed) && parsed >= 0 ? parsed : 0;
      render();
    });

    root.querySelector<HTMLButtonElement>('[data-finish-cash]')?.addEventListener('click', () => finishPayment('BAR'));
    root.querySelector<HTMLButtonElement>('[data-finish-card]')?.addEventListener('click', () => finishPayment('KARTE'));

    root.querySelector<HTMLFormElement>('#posx-mixed-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const value = new FormData(event.currentTarget).get('cash');
      const parsed = Number(String(value ?? '').replace(',', '.'));
      mixedCash = Number.isFinite(parsed) ? Math.max(0, Math.min(total(), parsed)) : 0;
      render();
    });
    root.querySelector<HTMLButtonElement>('[data-finish-mixed]')?.addEventListener('click', () => finishPayment('GEMISCHT'));

    root.querySelector<HTMLFormElement>('#posx-search-form')?.addEventListener('submit', (event) => {
      event.preventDefault();
      const value = new FormData(event.currentTarget).get('query');
      searchQuery = typeof value === 'string' ? value.trim() : '';
      render();
    });

    root.querySelectorAll<HTMLButtonElement>('[data-search-product]').forEach((button) => {
      button.addEventListener('click', () => {
        const product = allProducts().find((item) => item.id === button.dataset.searchProduct);
        if (!product) return;
        panel = null;
        searchQuery = '';
        addProduct(product);
      });
    });

    root.querySelectorAll<HTMLButtonElement>('[data-close-receipt]').forEach((button) => {
      button.addEventListener('click', () => {
        receipt = null;
        status = '';
        render();
      });
    });

    root.querySelector<HTMLButtonElement>('[data-park-sale]')?.addEventListener('click', parkSale);
  }

  render();

  void loadSimulatorContract().then((contract) => {
    if (!contract) return;
    applyContract(contract);
    render();
  });
}
