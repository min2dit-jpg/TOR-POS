namespace TorPos.App;

/// <summary>
/// Operator-interface translations, keyed by the German source string that
/// stays in the windows.
///
/// Deliberately NOT here: anything that belongs on a fiscal document. Bon,
/// DSFinV-K, Z-Bericht, TSE process data and the audit log are German records
/// and are produced in Core/Infrastructure, which does not reference this
/// assembly's language code at all.
///
/// A missing entry is not an error: the operator sees the German original.
/// That keeps a half-finished translation harmless at a real till.
///
/// German fiscal terms of art stay German on purpose - Z-Bericht, X-Bericht,
/// DSFinV-K, TSE, DATEV, GoBD, "Kassenmeldung § 146a Abs. 4 AO". The operator
/// discusses those words with their Steuerberater and a Finanzamt auditor;
/// translating them would make the till harder to use, not easier.
/// </summary>
internal static class UiTranslations
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> For(string code) => code switch
    {
        "TR" => Turkish,
        "EN" => English,
        _ => Empty
    };

    private static readonly Dictionary<string, string> Turkish = new(StringComparer.Ordinal)
    {
        // Anmeldung
        ["Anmeldung erforderlich"] = "Giriş gerekli",
        ["Kassenart"] = "Kasa türü",
        ["EINZELHANDEL"] = "PERAKENDE",
        ["GASTRONOMIE"] = "GASTRONOMİ",
        ["Bitte Einzelhandel oder Gastronomie auswählen."] = "Lütfen Perakende veya Gastronomi seçin.",
        ["Bitte zuerst Einzelhandel oder Gastronomie auswählen."] = "Lütfen önce Perakende veya Gastronomi seçin.",
        ["TEST · Einzelhandel oder Gastronomie auswählen. Betriebsdaten bleiben getrennt."] =
            "TEST · Perakende veya Gastronomi seçin. İşletme verileri ayrı kalır.",
        ["Kassenart: EINZELHANDEL · Lizenz/Installation fest gebunden."] =
            "Kasa türü: PERAKENDE · lisans/kurulum kalıcı olarak bağlı.",
        ["Kassenart: GASTRONOMIE · Lizenz/Installation fest gebunden."] =
            "Kasa türü: GASTRONOMİ · lisans/kurulum kalıcı olarak bağlı.",
        ["Benutzername"] = "Kullanıcı adı",
        ["Passwort"] = "Şifre",
        ["TRAININGSMODUS · keine echte Buchung / keine Kartenzahlung"] =
            "EĞİTİM MODU · gerçek kayıt yok / kart ödemesi yok",
        ["Training-Code"] = "Eğitim kodu",
        ["Training-Anmeldung nur mit dem Training-Code · Benutzer-Passwort ist dann nicht erforderlich."] =
            "Eğitim girişi yalnızca eğitim koduyla · kullanıcı şifresi gerekmez.",
        ["Ohne Anmeldung ist kein Zugriff möglich."] = "Giriş yapılmadan erişim mümkün değildir.",
        ["ANMELDEN"] = "GİRİŞ",
        ["PROGRAMM BEENDEN"] = "PROGRAMI KAPAT",
        ["Standard: admin / admin · Training-Code 0000 · Mitarbeiter zuerst in der Benutzerverwaltung aktivieren"] =
            "Varsayılan: admin / admin · Eğitim kodu 0000 · personeli önce Kullanıcı Yönetimi'nde etkinleştirin",
        ["Anmeldung wird geprüft ..."] = "Giriş kontrol ediliyor ...",
        ["Programm wird beendet · Datensicherung wird erstellt ..."] =
            "Program kapatılıyor · yedekleme oluşturuluyor ...",
        ["TRAININGSMODUS wird geöffnet ..."] = "EĞİTİM MODU açılıyor ...",

        // Kassieroberfläche
        ["KASSE"] = "KASA",
        ["WAREN"] = "ÜRÜNLER",
        ["BERICHTE"] = "RAPORLAR",
        ["EINSTELLUNGEN"] = "AYARLAR",
        ["ABMELDEN"] = "ÇIKIŞ",
        ["KASSIEREN"] = "ÖDEME AL",
        ["KASSIEREN → ZAHLART"] = "ÖDEME AL → ÖDEME TÜRÜ",
        ["BAR"] = "NAKİT",
        ["KARTE"] = "KART",
        ["PARKEN"] = "BEKLET",
        ["RABATT"] = "İNDİRİM",
        ["PFAND"] = "DEPOZİTO",
        ["BON EIN/AUS"] = "FİŞ AÇ/KAPAT",
        ["SOFORT STORNO"] = "ANINDA İPTAL",
        ["ARTIKEL"] = "ÜRÜN",
        ["BESTAND"] = "STOK",
        ["WARENGRUPPEN"] = "ÜRÜN GRUPLARI",
        ["SCHNELLARTIKEL"] = "HIZLI ÜRÜN",
        ["AKTUELLER VERKAUF"] = "GÜNCEL SATIŞ",
        ["GESAMT"] = "TOPLAM",
        ["Zahlung"] = "Ödeme",
        ["EAN / BARCODE"] = "EAN / BARKOD",
        ["EAN SUCHEN"] = "EAN ARA",
        ["Scanner bereit"] = "Okuyucu hazır",
        ["Scannen oder EAN eingeben"] = "Okutun veya EAN girin",
        ["Artikel scannen oder antippen"] = "Ürünü okutun veya dokunun",
        ["Artikel antippen → direkt im Bon"] = "Ürüne dokunun → doğrudan fişe",
        ["Antippen → Artikel"] = "Dokunun → ürün",
        ["← ZURÜCK ZUR KASSE"] = "← KASAYA DÖN",
        ["◀ WARENGRUPPEN"] = "◀ ÜRÜN GRUPLARI",
        ["TESTBETRIEB · FISKAL GESPERRT"] = "TEST MODU · FİSKAL KİLİTLİ",
        ["TSE-AUSFALL"] = "TSE ARIZASI",
        ["Bon stornieren"] = "Fişi iptal et",
        ["Teilretoure · einzelne Artikel"] = "Kısmi iade · tek tek ürün",
        ["Einlage / Entnahme"] = "Kasa giriş / çıkış",
        ["Kassensturz"] = "Kasa sayımı",
        ["Korrekturen"] = "Düzeltmeler",
        ["Betrieb"] = "İşletme",
        ["Bon / Abrechnung"] = "Fiş / hesap",
        ["Personal"] = "Personel",
        ["Geräte"] = "Cihazlar",
        ["Programm beenden"] = "Programı kapat",
        ["Einstellungen öffnen"] = "Ayarları aç",
        ["AUSGEWÄHLTEN BEREICH ÖFFNEN"] = "SEÇİLİ BÖLÜMÜ AÇ",
        ["Schnellzugriff"] = "Hızlı erişim",
        ["Kassenfunktionen"] = "Kasa işlevleri",
        ["Warenverwaltung"] = "Ürün yönetimi",
        ["Warenbestand"] = "Ürün stoğu",
        ["Datensicherung"] = "Yedekleme",
        ["Datenaustausch"] = "Veri alışverişi",
        ["Bedienerabrechnung"] = "Kasiyer hesabı",
        ["Duplikate anzeigen"] = "Kopyaları göster",
        ["Druckwarteschlange prüfen"] = "Yazdırma kuyruğunu kontrol et",
        ["Zahlung prüfen / fortsetzen"] = "Ödemeyi kontrol et / sürdür",
        ["Bon-Historie · Heute"] = "Fiş geçmişi · bugün",
        ["ADMIN · Vollzugriff"] = "ADMIN · tam yetki",
        ["LIZENZ"] = "LİSANS",
        ["UPDATE"] = "GÜNCELLEME",
        ["LOKALE KASSE"] = "YEREL KASA",
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        // Sign-in
        ["Anmeldung erforderlich"] = "Sign-in required",
        ["Kassenart"] = "Till type",
        ["EINZELHANDEL"] = "RETAIL",
        ["GASTRONOMIE"] = "HOSPITALITY",
        ["Bitte Einzelhandel oder Gastronomie auswählen."] = "Please select Retail or Hospitality.",
        ["Bitte zuerst Einzelhandel oder Gastronomie auswählen."] = "Please select Retail or Hospitality first.",
        ["TEST · Einzelhandel oder Gastronomie auswählen. Betriebsdaten bleiben getrennt."] =
            "TEST · select Retail or Hospitality. Business data stays separate.",
        ["Kassenart: EINZELHANDEL · Lizenz/Installation fest gebunden."] =
            "Till type: RETAIL · permanently bound to licence/installation.",
        ["Kassenart: GASTRONOMIE · Lizenz/Installation fest gebunden."] =
            "Till type: HOSPITALITY · permanently bound to licence/installation.",
        ["Benutzername"] = "User name",
        ["Passwort"] = "Password",
        ["TRAININGSMODUS · keine echte Buchung / keine Kartenzahlung"] =
            "TRAINING MODE · no real booking / no card payment",
        ["Training-Code"] = "Training code",
        ["Training-Anmeldung nur mit dem Training-Code · Benutzer-Passwort ist dann nicht erforderlich."] =
            "Training sign-in uses the training code only · no user password required.",
        ["Ohne Anmeldung ist kein Zugriff möglich."] = "No access without signing in.",
        ["ANMELDEN"] = "SIGN IN",
        ["PROGRAMM BEENDEN"] = "EXIT PROGRAM",
        ["Standard: admin / admin · Training-Code 0000 · Mitarbeiter zuerst in der Benutzerverwaltung aktivieren"] =
            "Default: admin / admin · training code 0000 · activate staff in User Management first",
        ["Anmeldung wird geprüft ..."] = "Checking sign-in ...",
        ["Programm wird beendet · Datensicherung wird erstellt ..."] =
            "Shutting down · creating backup ...",
        ["TRAININGSMODUS wird geöffnet ..."] = "Opening TRAINING MODE ...",

        // Cashier interface
        ["KASSE"] = "TILL",
        ["WAREN"] = "PRODUCTS",
        ["BERICHTE"] = "REPORTS",
        ["EINSTELLUNGEN"] = "SETTINGS",
        ["ABMELDEN"] = "SIGN OUT",
        ["KASSIEREN"] = "CHECKOUT",
        ["KASSIEREN → ZAHLART"] = "CHECKOUT → PAYMENT TYPE",
        ["BAR"] = "CASH",
        ["KARTE"] = "CARD",
        ["PARKEN"] = "PARK",
        ["RABATT"] = "DISCOUNT",
        ["PFAND"] = "DEPOSIT",
        ["BON EIN/AUS"] = "RECEIPT ON/OFF",
        ["SOFORT STORNO"] = "INSTANT VOID",
        ["ARTIKEL"] = "ITEM",
        ["BESTAND"] = "STOCK",
        ["WARENGRUPPEN"] = "PRODUCT GROUPS",
        ["SCHNELLARTIKEL"] = "QUICK ITEM",
        ["AKTUELLER VERKAUF"] = "CURRENT SALE",
        ["GESAMT"] = "TOTAL",
        ["Zahlung"] = "Payment",
        ["EAN / BARCODE"] = "EAN / BARCODE",
        ["EAN SUCHEN"] = "FIND EAN",
        ["Scanner bereit"] = "Scanner ready",
        ["Scannen oder EAN eingeben"] = "Scan or enter EAN",
        ["Artikel scannen oder antippen"] = "Scan or tap an item",
        ["Artikel antippen → direkt im Bon"] = "Tap an item → straight onto the receipt",
        ["Antippen → Artikel"] = "Tap → item",
        ["← ZURÜCK ZUR KASSE"] = "← BACK TO TILL",
        ["◀ WARENGRUPPEN"] = "◀ PRODUCT GROUPS",
        ["TESTBETRIEB · FISKAL GESPERRT"] = "TEST MODE · FISCAL LOCKED",
        ["TSE-AUSFALL"] = "TSE FAILURE",
        ["Bon stornieren"] = "Void receipt",
        ["Teilretoure · einzelne Artikel"] = "Partial return · individual items",
        ["Einlage / Entnahme"] = "Cash in / out",
        ["Kassensturz"] = "Cash count",
        ["Korrekturen"] = "Corrections",
        ["Betrieb"] = "Operations",
        ["Bon / Abrechnung"] = "Receipt / settlement",
        ["Personal"] = "Staff",
        ["Geräte"] = "Devices",
        ["Programm beenden"] = "Exit program",
        ["Einstellungen öffnen"] = "Open settings",
        ["AUSGEWÄHLTEN BEREICH ÖFFNEN"] = "OPEN SELECTED AREA",
        ["Schnellzugriff"] = "Quick access",
        ["Kassenfunktionen"] = "Till functions",
        ["Warenverwaltung"] = "Product management",
        ["Warenbestand"] = "Stock",
        ["Datensicherung"] = "Backup",
        ["Datenaustausch"] = "Data exchange",
        ["Bedienerabrechnung"] = "Operator settlement",
        ["Duplikate anzeigen"] = "Show duplicates",
        ["Druckwarteschlange prüfen"] = "Check print queue",
        ["Zahlung prüfen / fortsetzen"] = "Check / resume payment",
        ["Bon-Historie · Heute"] = "Receipt history · today",
        ["ADMIN · Vollzugriff"] = "ADMIN · full access",
        ["LIZENZ"] = "LICENCE",
        ["UPDATE"] = "UPDATE",
        ["LOKALE KASSE"] = "LOCAL TILL",
    };
}
