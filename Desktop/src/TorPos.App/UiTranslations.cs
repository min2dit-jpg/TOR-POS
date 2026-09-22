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
    };
}
