using System.Text.RegularExpressions;
using TorPos.App;

// Operator-interface language DE/TR/EN.
//
// R54 had reduced this to a German-only facade and purged the stored preference
// on every start. These checks lock the two rules the feature has to keep:
// the German source text is the key and survives an incomplete translation, and
// fiscal documents are never translated.
public static class MultiLanguageTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var previous = UiLanguage.Current;
        try
        {
            UiLanguage.Set("TR");
            var turkish = UiLanguage.T("ANMELDEN");
            var unknownTurkish = UiLanguage.T("Ein Text, den niemand übersetzt hat");
            UiLanguage.Set("EN");
            var english = UiLanguage.T("ANMELDEN");
            UiLanguage.Set("DE");
            var german = UiLanguage.T("ANMELDEN");

            assert(
                turkish == "GİRİŞ" && english == "SIGN IN" && german == "ANMELDEN",
                "the operator interface answers in the selected language and German is the source text");

            assert(
                unknownTurkish == "Ein Text, den niemand übersetzt hat",
                "an untranslated string stays German instead of showing a placeholder, so a half-finished translation is harmless at a till");

            // A till label is usually a label glued to an amount, and the amount
            // changes with every sale, so the whole string can never be a table
            // entry. The label has to be translated and the amount left exactly
            // as the window formatted it - including the comma and the euro sign.
            UiLanguage.Set("TR");
            var total = UiLanguage.T("GESAMT: 12,50 €");
            var card = UiLanguage.T("KARTENZAHLUNG · 12,50 €");
            var twoLines = UiLanguage.T("BAR\nF1");
            var unknownCompound = UiLanguage.T("Unbekannter Posten: 12,50 €");
            UiLanguage.Set("DE");
            assert(
                total == "TOPLAM: 12,50 €" && card == "KART ÖDEMESİ · 12,50 €" &&
                twoLines == "NAKİT\nF1" && unknownCompound == "Unbekannter Posten: 12,50 €",
                "a label glued to an amount is translated without touching the amount, and an untranslated label still stays German");

            // The operator's own words are not the program's to translate. A
            // category they named, a product, a payment-button label they set -
            // these carry the same separator as a till label and must survive a
            // language switch unchanged, because the receipt and the product list
            // keep the name they typed.
            UiLanguage.Set("TR");
            var category = UiLanguage.T("ARTIKEL · GETRÄNKE");
            var product = UiLanguage.T("EXTRA · BAR");
            var vatButton = UiLanguage.T("GETRÄNKE · 19 %");
            UiLanguage.Set("DE");
            assert(
                category == "ARTIKEL · GETRÄNKE" && product == "EXTRA · BAR" &&
                vatButton == "İÇECEKLER · 19 %",
                "a language switch does not rewrite the operator's own data, while a label glued to an amount is still translated");

            UiLanguage.Set("KLINGONISCH");
            var unsupported = UiLanguage.Current;
            UiLanguage.Set(null);
            var missing = UiLanguage.Current;
            assert(
                unsupported == "DE" && missing == "DE" &&
                UiLanguage.IsSupported("tr") && !UiLanguage.IsSupported("FR"),
                "an unknown or empty language code falls back to German rather than leaving the interface blank");
        }
        finally
        {
            UiLanguage.Set(previous);
        }

        // Both tables must describe the same strings. A key that exists in one
        // language only is a half-done translation that would show a German
        // interface to one customer and a translated one to the next.
        var translations = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UiTranslations.cs"));
        var turkishBlock = Between(translations, "Turkish = new(StringComparer.Ordinal)", "private static readonly Dictionary<string, string> English");
        var englishBlock = Between(translations, "English = new(StringComparer.Ordinal)", null);
        var turkishKeys = Keys(turkishBlock);
        var englishKeys = Keys(englishBlock);
        assert(
            turkishKeys.Count > 0 && turkishKeys.SetEquals(englishKeys),
            "every translated string exists in Turkish and English, so no language is left half-finished");

        // German fiscal terms of art are the names of legal records and stay
        // German in every interface language - a Turkish till still prints and
        // announces a Z-Bericht. R49 and R54 rely on this, so it is checked
        // instead of trusted: a label may be translated around the term, but the
        // term itself has to survive into the translated text.
        string[] fiscalTerms =
            ["Z-Bericht", "X-Bericht", "Z-Abschluss", "DSFinV-K", "TSE", "DATEV", "GoBD", "§ 146a"];
        var mistranslatedTerm = Pairs(turkishBlock).Concat(Pairs(englishBlock))
            .SelectMany(pair => fiscalTerms
                .Where(term => pair.Key.Contains(term, StringComparison.Ordinal)
                            && !pair.Value.Contains(term, StringComparison.Ordinal))
                .Select(term => $"{pair.Key} -> {pair.Value} ({term})"))
            .FirstOrDefault();
        assert(
            mistranslatedTerm is null && fiscalTerms.All(term => !turkishKeys.Contains(term)),
            "German fiscal terms of art keep their name in Turkish and English, so the legal record is called the same thing in every language");

        // A window changes some labels while it runs - PFAND becomes EXTRA on the
        // Gastro till. Apply() must treat that new text as the source instead of
        // writing the previous label back over it on the next pass.
        var language = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UiLanguage.cs"));
        assert(
            language.Contains("!string.Equals(current, entry.Text, StringComparison.Ordinal)", StringComparison.Ordinal) &&
            language.Contains("entry.Text = T(entry.Source);", StringComparison.Ordinal),
            "a label the window changes while running is not overwritten again with the text it had before");

        // The fiscal record is German. Bon, DSFinV-K, Z-Bericht, TSE process data
        // and the audit log are produced outside the UI assembly; that boundary is
        // what keeps them German, so it is asserted rather than remembered.
        var fiscalSources = new[] { "Desktop/src/TorPos.Core", "Desktop/src/TorPos.Infrastructure" }
            .SelectMany(dir => Directory.EnumerateFiles(FindRepoDirectory(dir), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                var source = File.ReadAllText(f);
                return source.Contains("UiLanguage", StringComparison.Ordinal)
                    || source.Contains("UiTranslations", StringComparison.Ordinal);
            })
            .ToArray();
        assert(
            fiscalSources.Length == 0,
            "no fiscal or infrastructure code reaches into the interface language, so receipts, DSFinV-K, Z-Bericht and the audit log stay German");

        // The payment window writes its own German into the accept button and the
        // validation line on every keystroke, long after Opened. Without a second
        // render pass the operator would watch the screen fall back to German
        // while typing the amount given.
        var payment = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/PaymentChoiceWindow.cs"));
        assert(
            payment.Contains("UpdateAcceptState();", StringComparison.Ordinal) &&
            payment.Contains("UiLanguage.Apply(this);", StringComparison.Ordinal) &&
            payment.IndexOf("UpdateAcceptState();", StringComparison.Ordinal)
                < payment.IndexOf("private void UpdateAcceptState()", StringComparison.Ordinal),
            "the payment screen re-renders after it rewrites its own labels, so it does not fall back to German while the operator types");

        // A window that never calls Apply() looks exactly like a missing
        // translation and is not one: the strings are in the table and simply
        // never reach the screen. Every shared Einzelhandel/Gastro window renders
        // itself. The explicitly named windows below stay German for a reviewed
        // product or document reason.
        //
        //   TextReportWindow, ZArchiveWindow  - they show a Z-Bericht or X-Bericht
        //                                       verbatim; that is the fiscal record.
        //   CustomerDisplayWindow,            - they face the customer, who is
        //   OrderCustomerDisplayWindow          served in German.
        //   StartupLoadingWindow,             - they run before the stored language
        //   StartupErrorWindow                  has been read.
        //   Restaurant*Window                  - TOR Restaurant / Restaurant Plus
        //                                       are intentionally German-only products.
        string[] germanOnlyWindows =
        [
            "TextReportWindow", "ZArchiveWindow",
            "CustomerDisplayWindow", "OrderCustomerDisplayWindow",
            "StartupLoadingWindow", "StartupErrorWindow",
            "RestaurantHandheldSetupWindow",
            "RestaurantKdsWindow",
            "RestaurantReservationsWindow",
            "RestaurantTablePlanWindow"
        ];

        string[] germanOnlyWindowFiles =
        [
            "RestaurantHandheldSetupWindow.cs",
            "RestaurantKdsWindow.cs",
            "RestaurantReservationsWindow.cs",
            "RestaurantTablePlanWindow.cs"
        ];
        var windows = 0;
        var unrendered = new List<string>();
        foreach (var file in AppSources())
        {
            var source = File.ReadAllText(file);
            var declarations = Regex.Matches(source, "class\\s+(\\w+)\\s*:\\s*Window\\b").ToArray();
            windows += declarations.Length;
            for (var i = 0; i < declarations.Length; i++)
            {
                var name = declarations[i].Groups[1].Value;
                if (germanOnlyWindows.Contains(name)) continue;

                var start = declarations[i].Index;
                var end = i + 1 < declarations.Length ? declarations[i + 1].Index : source.Length;
                if (!source[start..end].Contains("UiLanguage.Apply", StringComparison.Ordinal))
                    unrendered.Add(name);
            }
        }
        assert(
            windows > 40 && unrendered.Count == 0,
            "every operator window renders itself in the chosen language, and the windows that stay German are named and justified");

        // Not every dialog is a class. The "something went wrong" and "are you
        // sure" windows are built inline, shown once and dropped - and they are
        // the text a cashier reads at the worst moment. They are invisible to the
        // check above, so they are counted here: every inline window renders
        // itself before it is shown, and every one of them is assigned to a
        // variable, so none can hide from this count.
        var inlineWindows = 0;
        var assignedWindows = 0;
        var unrenderedDialogs = new List<string>();
        foreach (var file in AppSources())
        {
            var source = File.ReadAllText(file);
            inlineWindows += Regex.Matches(source, "new Window\\b").Count;
            foreach (Match declaration in Regex.Matches(source, "(\\w+)\\s*=\\s*new Window\\b"))
            {
                assignedWindows++;
                var name = declaration.Groups[1].Value;
                var fileName = Path.GetFileName(file);
                if (germanOnlyWindowFiles.Contains(fileName))
                    continue;

                var rest = source[declaration.Index..];
                var shown = Regex.Match(rest, $"\\b{Regex.Escape(name)}\\.(ShowDialog|Show)\\s*[<(]");
                var built = shown.Success ? rest[..shown.Index] : rest;
                if (!built.Contains("UiLanguage.Apply", StringComparison.Ordinal))
                    unrenderedDialogs.Add($"{fileName}:{name}");
            }
        }
        assert(
            inlineWindows >= 10 && assignedWindows == inlineWindows && unrenderedDialogs.Count == 0,
            "the message and confirmation windows that are built inline also render in the chosen language before they are shown");

        // Devices, Cloud, backup and staff are also built dynamically.
        // Keep this reviewed program vocabulary symmetric in TR/EN; actual
        // printer names, paths, tokens and staff-entered values remain data.
        string[] operationalSettingsVocabulary =
        [
            "Drucker & Geräte",
            "Windows-Drucker auswählen, testen und unten SPEICHERN drücken. Das Kartenterminal wird über den Marken-Assistenten verbunden.",
            "WAAGEN-EINSTELLUNGEN · MANUELL / COM / LAN / BARCODE",
            "Gewichtsverkauf",
            "Gewichtsartikel werden intern in kg geführt. Eine separate Waage ohne Kassenanschluss ist vollständig nutzbar: Gewicht ablesen und beim Verkauf in Gramm oder Kilogramm eingeben. COM/LAN/Waagenbarcode können zusätzlich vorbereitet werden.",
            "DRUCKER-ZENTRALE · EPSON / STAR AUTOMATISCH ERKENNEN",
            "Automatische Druckererkennung",
            "TOR liest die in Windows installierten Drucker, Treiber und Ports und erkennt verbreitete Epson-TM- sowie Star-mC/TSP-Bondrucker. Ein konkretes Modell wird nur bei eindeutiger Kennung übernommen; bei unklarem Modell muss der Benutzer die Auswahl bestätigen.",
            "KARTENTERMINAL VERBINDEN · MARKE AUSWÄHLEN",
            "Terminal-Assistent",
            "Bondrucker",
            "Küchendrucker / 2. Drucker",
            "Bestellung automatisch an Küchendrucker senden",
            "Küchenbon",
            "Küchenbons bleiben immer auf Deutsch und sind klar als KEIN STEUERBELEG gekennzeichnet. Im ORDER-Modus wird die Küche direkt bei Bestellannahme informiert; OFF/SALE drucken erst beim Kassieren.",
            "Küchenrouting nach Station",
            "Küchendrucker · Grill",
            "Küchendrucker · Fritteuse",
            "Küchendrucker · Getränke",
            "A4-Drucker",
            "GERÄTESTATUS AKTUALISIEREN",
            "Terminal & TSE",
            "Kassenlade verwenden",
            "Kassenschublade · Verbindungstest",
            "Noch nicht getestet. Der Test erzeugt keinen Verkauf und keinen Bon.",
            "KASSENSCHUBLADE TESTEN",
            "Der Test sendet genau einen ESC-p/StarPRNT-Impuls über den ausgewählten Bondrucker. Kein Verkauf, keine TSE-Transaktion, kein Bon.",
            "Kundendisplay verwenden",
            "Kundendisplay · eigener Bildschirm",
            "Bildschirm",
            "0 = automatisch den zweiten Bildschirm verwenden; 1–4 = feste Bildschirmnummer.",
            "Getrennt vom Bestellmonitor",
            "Separaten Bestellmonitor verwenden",
            "Bestellmonitor · eigener Bildschirm",
            "Aktualisierung (Sek.)",
            "1–10 Sekunden · Standard 2.",
            "Nur Bestellungen",
            "TOR POS Cloud · Synchronisierung",
            "Verbindung und Bestand direkt mit dem Kundenportal prüfen. Testbons werden nicht als Umsatz übertragen.",
            "Leer lassen: gespeichertes Token behalten",
            "Synchronisierung aktivieren",
            "Cloud wird geladen …",
            "Cloud-Verbindung",
            "Server-URL",
            "Gleicher PC: http://127.0.0.1:8787. Andere Server benötigen HTTPS.",
            "Gerätecode",
            "Das Ziel bleibt für wartende Daten fest zugeordnet.",
            "Gerätetoken",
            "Verschlüsselt für dieses Windows-Benutzerkonto gespeichert. Nicht im Chat teilen.",
            "CLOUD SPEICHERN",
            "VERBINDUNG PRÜFEN",
            "JETZT VOLLSTÄNDIG ABGLEICHEN",
            "SYNCHRONISIEREN / STATUS",
            "Erst CLOUD SPEICHERN. Verbindung, Bestand und Status verwenden die gespeicherten Einstellungen.",
            "Übertragung",
            "Cloud-Dienst nicht verfügbar",
            "Cloud:",
            "Verbindung bestätigt · ",
            "Ausgewählter Windows-Drucker",
            "Liste laden und Drucker wählen",
            "LISTE AKTUALISIEREN",
            "TESTDRUCK",
            "Noch nicht geprüft. Auswahl und Test bestätigen keinen Papierausdruck.",
            "Barcode-Scanner",
            "Verhalten",
            "Akustische Meldung bei unbekanntem Barcode vorbereiten",
            "Unbekannte EAN",
            "Performance",
            "Datensicherung",
            "Backups sind vom Verkaufspfad getrennt und können auf ein anderes Laufwerk oder einen Serverordner geschrieben werden.",
            "Backup",
            "Beim Programmschluss Sicherung erstellen",
            "Sicherungsverzeichnis",
            "Sicherungen behalten",
            "Tägliche automatische Sicherung",
            "Tägliche automatische Sicherung aktiv",
            "Uhrzeit",
            "HH:mm · Standard 00:00. Erzeugt keinen Z-Bericht und keinen Kassenabschluss.",
            "Letzte erfolgreiche Sicherung",
            "Wenn TOR POS zur Uhrzeit geschlossen war, wird die verpasste Sicherung beim nächsten Start nachgeholt.",
            "Bei nicht erreichbarem Laufwerk bleibt der Erfolgszeitpunkt unverändert; TOR versucht nach fünf Minuten erneut.",
            "Letzte Datei",
            "Prüfen & sichern",
            "VERZEICHNIS TESTEN",
            "SICHERUNG JETZT ERSTELLEN",
            "DATEN + BILDER SICHERN / PRÜFEN",
            "Sicherung wird erstellt ...",
            "Sicherung erstellt:",
            "Sicherung fehlgeschlagen:",
            "Sicherung und Wiederherstellungsprüfung laufen ...",
            "Geprüft:",
            "Dateien",
            "Artikel",
            "Verkäufe",
            "Prüfung fehlgeschlagen:",
            "Paket:",
            "Sicherung verschlüsseln",
            "VERSCHLÜSSELUNG AKTIVIEREN",
            "WIEDERHERSTELLUNGSCODE NEU ERSTELLEN",
            "VERSCHLÜSSELUNG DEAKTIVIEREN",
            "Wiederherstellungscode",
            "Ich habe diesen Code sicher gespeichert.",
            "FERTIG",
            "Personal & Rechte",
            "Drei Mitarbeiterkonten mit eigenen Zugangsdaten und einzeln einstellbaren Funktionsrechten.",
            "Admin + 3 Mitarbeiter",
            "Admin",
            "Vollzugriff · Benutzerverwaltung nur durch Admin",
            "Mitarbeiter",
            "Genau 3 Konten · einzeln aktivierbar",
            "3 BENUTZER & RECHTE VERWALTEN",
            "Sichere Rechte",
            "Trainingszugang",
            "4 Ziffern",
            "TRAINING-CODE SPEICHERN",
            "Training-Code",
            "Gilt nur für die Trainingsanmeldung ohne Benutzerpasswort. Trainingsverkäufe werden nie gebucht, signiert oder an die Cloud gemeldet.",
            "Der Training-Code muss aus genau 4 Ziffern bestehen. Nicht gespeichert.",
            "Gespeichert."
        ];
        assert(
            operationalSettingsVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "devices cloud backup and staff settings vocabulary stays complete while machine and operator data stay untouched");

        // Core customer-facing settings are dynamically built too. Keep their
        // fixed program vocabulary translated, while the actual TextBox values
        // (company name, payment labels, addresses, etc.) remain operator data
        // and are never translated by UiLanguage.Apply.
        string[] customerSettingsVocabulary =
        [
            "Alltag",
            "Die Kasse soll nach der Einrichtung ohne technische Entscheidungen bedienbar sein.",
            "Start & Anzeige",
            "Bildschirmtastatur bei Berührung öffnen",
            "Kassenname",
            "Zum Beispiel Kasse 1, Theke oder Eingang.",
            "Betriebsart",
            "Startansicht",
            "Für den normalen Betrieb wird KASSE empfohlen.",
            "Darstellung",
            "AUTO ist für die meisten Touchscreens die beste Wahl.",
            "Sprache",
            "Gilt nur für die Bedienoberfläche. Bon, DSFinV-K, Z-Bericht und Protokolle bleiben deutsch.",
            "Einfach gehalten",
            "Rastergrößen, Schrift-Feinabstimmung, Kassennummer und technische Anzeigeparameter liegen im geschützten Technikerbereich.",
            "Firmendaten",
            "Geschäftsdaten werden zentral gepflegt und später für Bon, Berichte und fiskale Dokumente verwendet.",
            "Geschäftsinformationen",
            "Firma",
            "Inhaber / Betreiber",
            "Straße",
            "PLZ",
            "Ort",
            "Telefon",
            "E-Mail",
            "Steuernummer",
            "USt-IdNr.",
            "Verwendung",
            "Firmendaten auf dem Bon verwenden",
            "Firmendaten auf Berichten verwenden",
            "Bedienfunktionen",
            "Nur Funktionen einschalten, die der Betrieb tatsächlich benutzt.",
            "Verkauf",
            "Sonstige Artikel / freie Preiseingabe anzeigen",
            "Pfand / Leergut anzeigen",
            "Gastronomie · Extras",
            "Verwaltung direkt im Artikel / unter STAMMDATEN → EXTRAS",
            "Bei kritischem Warenbestand warnen",
            "Mindestbestand",
            "Warnschwelle, z.B. 5 Stück.",
            "Abholnummer / Bestellablauf",
            "Schnellauswahl",
            "OFF = aus · SALE = Nummer beim Kassieren · ORDER = Nummer sofort bei Bestellannahme",
            "Empfohlen für Gastronomie mit Bestell-/Abholablauf (z. B. Döner, Imbiss, Café, Restaurant): ORDER. Dann erscheint in der Kasse BESTELLUNG ANNEHMEN · F3 · ABHOLNR.; der eigentliche Bon entsteht erst später bei BAR/KARTE.",
            "Bei Bestellannahme eine Abholnummer vergeben",
            "Bei ORDER einen Abholschein für den Kunden drucken",
            "Training",
            "ORDER kann jetzt auch mit dem TRAINING-Benutzer getestet werden. Trainingsbestellungen und Trainings-Abholnummern bleiben getrennt von echten offenen Bestellungen.",
            "Wichtig",
            "Abholnummer ist nur eine Betriebs-/Wartenummer und ersetzt niemals die Bonnummer.",
            "Tagesabschluss",
            "Z-Bericht automatisch drucken",
            "Z-Abschluss bleibt bei offenen geparkten Bons gesperrt.",
            "Zahlarten",
            "Zahlarten werden separat konfiguriert. Die Karten-Zahlart wird später direkt mit ZVT verbunden.",
            "Aktive Zahlarten",
            "Bar aktiv",
            "Karte aktiv",
            "Auf Rechnung aktiv",
            "Bezeichnung Bar",
            "Bezeichnung Karte",
            "Bezeichnung Rechnung",
            "Schnellkassieren · F5",
            "Standard-Zahlart",
            "AUS = F5 öffnet die Zahlart-Auswahl. BAR/KARTE = F5 startet die gewählte Zahlart direkt.",
            "Bei F5 + BAR sofort als PASSEND kassieren (kein Rückgeld-Dialog)",
            "F1 BAR bleibt immer die normale Barzahlung mit Gegeben/Rückgeld. F2 KARTE bleibt direkte Kartenzahlung. Die gespeicherte Zahlart ist auch beim Schnellkassieren immer eindeutig BAR oder KARTE.",
            "Angebote / Rabatte",
            "Manuellen Rabatt zusätzlich zu einem aktiven Angebot erlauben",
            "Standard",
            "AUS empfohlen: Angebot und manueller Rabatt werden nicht automatisch gestapelt. Pfand wird niemals durch Angebot rabattiert.",
            "Kartenzahlung",
            "Bei aktivierter Terminalintegration wird ein Kartenverkauf erst nach erfolgreicher Terminalbestätigung als Verkauf gespeichert."
        ];
        assert(
            customerSettingsVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "core customer-facing settings vocabulary stays complete in Turkish and English while operator-entered values remain data");

        // The settings/report page is built dynamically, so its labels do not
        // live in XAML resource keys. Keep the reviewed navigation and monthly
        // report/email vocabulary symmetric in both languages; fiscal record
        // names inside those strings deliberately stay German.
        string[] reportsSettingsVocabulary =
        [
            "Kasse & Bedienung",
            "Firma & Bon",
            "Artikel & Steuern",
            "Zahlung",
            "Geräte",
            "Personal",
            "Berichte & E-Mail",
            "Datensicherung",
            "Software & Update",
            "Erweitert / Techniker 🔒",
            "Erweitert / Techniker",
            "Alle Standardberichte können als PDF gespeichert werden. Optional verschickt TOR POS einmal pro Monat automatisch ein PDF-Paket per E-Mail.",
            "Monatlicher PDF-Versand",
            "Monatliche Berichte automatisch per E-Mail senden",
            "Empfänger-E-Mail",
            "Diese Adresse erhält das monatliche PDF-Paket.",
            "Versandtag",
            "1–28 · Standard 1. Versendet den abgeschlossenen Vormonat.",
            "Versandzeit",
            "HH:mm · Standard 00:15. Bei ausgeschaltetem TOR POS wird der verpasste Versand nach dem nächsten Start nachgeholt.",
            "Sicherheitsregel",
            "Der E-Mail-Versand erzeugt KEINEN Z-Bericht, keinen Tagesabschluss und keinen Kassenabschluss.",
            "TOR Mail · empfohlen · keine Google-/SMTP-Einrichtung",
            "Der Kunde trägt nur die Empfänger-E-Mail ein. Der Absender und die SMTP-Zugangsdaten liegen ausschließlich auf dem TOR POS Cloud-Server und werden niemals auf der Kasse gespeichert.",
            "TOR MAIL ALS VERSANDWEG VERWENDEN",
            "TOR MAIL TEST-E-MAIL SENDEN",
            "Voraussetzung: TOR POS Cloud muss unter Geräte eingerichtet sein. Der zentrale Mail-Absender wird einmalig von TOR auf dem Cloud-Server konfiguriert; der Kunde benötigt dafür kein Google-Konto, App-Passwort oder SMTP-Wissen.",
            "Google-Konto / Gmail API (Alternative)",
            "Kein Gmail-Passwort und kein App-Passwort in TOR POS: MIT GOOGLE ANMELDEN erzeugt einen einmaligen QR-Code. Der Inhaber scannt ihn mit dem Handy und bestätigt die Berechtigung direkt bei Google.",
            "MIT GOOGLE ANMELDEN (QR)",
            "GOOGLE TEST-E-MAIL SENDEN",
            "GOOGLE-VERBINDUNG TRENNEN",
            "Voraussetzung für QR-Anmeldung: TOR POS Cloud muss unter Geräte eingerichtet sein und der Cloud-Server benötigt eine freigegebene Google-OAuth-Konfiguration. Die Monatsberichte selbst gehen direkt von diesem PC an die Gmail API; die PDF-Dateien werden nicht über TOR POS Cloud geleitet.",
            "Eigener E-Mail-Ausgang / SMTP (Alternative)",
            "Absender-E-Mail",
            "Für Gmail: smtp.gmail.com",
            "Für Gmail: 587 (STARTTLS).",
            "TLS/SSL verwenden",
            "SMTP-Benutzer",
            "Bei Gmail die vollständige Gmail-/Google-Workspace-Adresse.",
            "SMTP App-Passwort",
            "Leer lassen, um ein bereits gespeichertes App-Passwort zu behalten. Google-App-Passwort-Leerzeichen werden automatisch entfernt.",
            "GMAIL-STANDARD ÜBERNEHMEN",
            "SMTP ALS VERSANDWEG VERWENDEN",
            "Noch kein E-Mail-Test ausgeführt.",
            "TEST-E-MAIL SENDEN",
            "SMTP-Diagnose",
            "App-Passwort wird niemals in diesem Feld oder im Audit-Log ausgegeben.",
            "Letzter automatischer Versand",
            "Letzter Berichtsmonat",
            "Letzter erfolgreicher Versand",
            "Letzter Fehler",
            "Lokales PDF-Paket",
            "Die versendeten PDFs bleiben zusätzlich lokal im Reports-Ordner gespeichert.",
            "Welche PDFs werden monatlich versendet?",
            "Monatsübersicht, Kassenjournal, Verkaufsstatistik, Bedienerabrechnung, Stornobericht, vorhandenes Z-Archiv und ein aktueller Warenbestands-Snapshot. Es wird niemals automatisch ein neuer Z-Abschluss erzeugt.",
            "Google App-Passwort eingeben",
            "TOR-Mail-Status wird geladen …",
            "Google-Verbindung wird geladen …",
            "TOR POS Cloud-Dienst ist nicht verfügbar.",
            "TOR POS Cloud zuerst unter Geräte einrichten und aktivieren.",
            "TOR Mail ist jetzt der aktive Versandweg.",
            "TOR Mail Test-E-Mail wird gesendet …",
            "TOR Mail bereit ✓\nTest-E-Mail wurde über den TOR POS Cloud-Versanddienst gesendet.",
            "TOR Mail Test-E-Mail erfolgreich gesendet.",
            "TOR Mail Test fehlgeschlagen:",
            "TOR Mail Test-E-Mail fehlgeschlagen.",
            "Sicherer QR-Code wird erstellt …",
            "Google-Konto erfolgreich verbunden.",
            "Google-Anmeldung beendet.",
            "Google-Anmeldung konnte nicht gestartet werden:",
            "Google-Anmeldung fehlgeschlagen.",
            "Google Test-E-Mail wird gesendet …",
            "Google-Verbindung aktiv ✓\nTest-E-Mail wurde über Gmail API gesendet.",
            "Google Test-E-Mail erfolgreich gesendet.",
            "Google Test-E-Mail fehlgeschlagen:",
            "Google Test-E-Mail fehlgeschlagen.",
            "Google-Verbindung getrennt.",
            "Google-Verbindung konnte nicht getrennt werden:",
            "Google-Verbindung trennen fehlgeschlagen.",
            "Gmail-Standard gesetzt: smtp.gmail.com · Port 587 · STARTTLS",
            "SMTP ist jetzt der aktive Versandweg.",
            "Test-E-Mail wird gesendet ...",
            "SMTP-Verbindung wird geprüft ...",
            "Test-E-Mail erfolgreich gesendet.",
            "ERFOLG: Test-E-Mail wurde gesendet.\nSTARTTLS, TLS-Handshake und SMTP-Anmeldung funktionieren.",
            "E-Mail-Test fehlgeschlagen. Details im SMTP-Diagnosefeld."
        ];
        assert(
            reportsSettingsVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "reports/email settings vocabulary stays complete in Turkish and English without renaming fiscal records");

        // MainWindow rewrites its status line throughout a shift. Those writes
        // must go through StatusLine so a TR/EN screen cannot silently fall back
        // to German after startup. Static program messages must exist in both
        // translation tables; interpolated values may contain operator/business
        // data and are deliberately not rewritten by this check.
        var mainSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var safetySource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.Safety.cs"));
        var statusSources = mainSource + "\n" + safetySource;
        var directStatusWrites = Regex.Matches(statusSources, "ScannerStatus\\.Text\\s*(?:=|\\+=)").Count;
        var staticStatusMessages = Regex.Matches(
                statusSources,
                "StatusLine\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var missingStatusTranslations = staticStatusMessages
            .Where(message => !turkishKeys.Contains(message) || !englishKeys.Contains(message))
            .ToArray();
        assert(
            directStatusWrites == 1 && staticStatusMessages.Length >= 75 && missingStatusTranslations.Length == 0,
            "every static MainWindow status message is translated and runtime writes pass through the language-aware StatusLine boundary");

        // A key written twice is not a compile error: these are object
        // initializers, so the second assignment silently wins. Someone
        // correcting the first one would see nothing change. The tables carried
        // six such pairs; they are gone and may not come back.
        var turkishOrder = Pairs(turkishBlock).Select(pair => pair.Key).ToArray();
        var englishOrder = Pairs(englishBlock).Select(pair => pair.Key).ToArray();
        assert(
            turkishOrder.Length == turkishKeys.Count && englishOrder.Length == englishKeys.Count,
            "no string is translated twice in the same table, where the second entry would silently win over a correction to the first");

        // The technician pages: Software & Update, System, the till's technical
        // fine tuning, the device setup and the DATEV mapping. Version numbers,
        // paths, printer names, account numbers and server addresses stay as they
        // are - only the program's own words are here.
        string[] technicianSettingsVocabulary =
        [
            "Version, Lizenzlaufzeit und Updates ohne technische Server-Einstellungen prüfen.",
            "Installierte TOR-POS-Version",
            "Version",
            "Build",
            "Edition",
            "Gültig bis",
            "Hinweis",
            "TOR Update",
            "Automatisch alle 6 Stunden nach Updates suchen",
            "Update-Status wird geladen …",
            "AUTOMATISCHE PRÜFUNG SPEICHERN",
            "JETZT NACH UPDATE SUCHEN",
            "Automatische Update-Prüfung ist aktiv.",
            "Automatische Update-Prüfung ist deaktiviert. Manuelle Prüfung bleibt möglich.",
            "Noch keine Update-Prüfung.",
            "Letzte Prüfung",
            "Neue Version verfügbar",
            "WICHTIGES UPDATE",
            "Zur Kasse zurückkehren: oben erscheint für Admin der UPDATE-Button. Installation startet erst nach Sicherheitsprüfung und Backup.",
            "Update-Einstellung",
            "Update-Prüfung",
            "Update-Status",
            "Sicher aktualisieren",
            "TOR POS installiert niemals mitten in einem Verkauf oder einer ungeklärten Zahlung. Vor der Installation werden Setup-Prüfsumme/Signatur geprüft und eine Datenbanksicherung erstellt. Die technische Update-Server-Adresse bleibt im geschützten Technikerbereich.",
            "System",
            "Technische Informationen für Installation, Service und Diagnose.",
            "Installation",
            "Datenordner",
            "Datenbank",
            "Schema-Migration",
            "Produktbilder",
            "Backups",
            "TOR Update · Technische Quelle",
            "Leer = TOR Cloud Server verwenden",
            "Update-Server wird geladen …",
            "Update-Server",
            "Nur für Installation/Service. Produktiv ausschließlich HTTPS; localhost darf für Entwicklung HTTP verwenden.",
            "UPDATE-SERVER SPEICHERN",
            "Gespeichert: TOR Cloud Server wird als Update-Quelle verwendet.",
            "Technische Update-Quelle gespeichert.",
            "Keine separate Update-Quelle: TOR Cloud Server wird verwendet.",
            "Separate technische Update-Quelle ist konfiguriert.",
            "Performance & Diagnose",
            "Bewertung",
            "Noch keine Messwerte vorhanden.",
            "SYSTEMSTATUS / DIAGNOSE ÖFFNEN",
            "Fiskalstatus",
            "Swissbit-Bridge, TSE-Ausfallbehandlung und ZVT-Anbindung sind vorbereitet. Der vollständige DSFinV-K-Export und die Realhardware-Abnahme sind noch nicht freigegeben; TOR POS bleibt deshalb im TESTBETRIEB.",
            "Kasse · Technische Feinabstimmung",
            "Diese Werte werden bei Installation gesetzt und gehören nicht in den täglichen Betrieb.",
            "Kassenidentität & Darstellung",
            "Kassennummer",
            "Eindeutige Nummer des Kassensystems. Standard: 1.",
            "Theme",
            "Touch-Raster",
            "Artikeltasten · Spalten",
            "Artikeltasten · Zeilen",
            "Warengruppen · Spalten",
            "Warengruppen · Zeilen",
            "Schriftgröße · Tasten",
            "2–8 · Gastronomie Standard 4 · Einzelhandel 5",
            "2–12 · Gastronomie Standard 3 · Einzelhandel 8",
            "2–8 · Standard 4",
            "1–10 · Standard 6",
            "11–28 · Standard 18",
            "Artikelbilder anzeigen",
            "Weitere Kassenlogik",
            "Währung",
            "Abkürzung",
            "Anfangsbestand in Cent (bis zum ersten Kassensturz)",
            "Bediener auf Bon anzeigen",
            "Varianten / Optionen auf Bon anzeigen",
            "Zahlart vor Abschluss zusätzlich bestätigen",
            "Stornogründe",
            "Pflichtgrund bei SOFORT STORNO (vor der Zahlung) · mit Zeilenumbruch eingeben.",
            "Bon-Storno-Gründe",
            "Pflichtgrund bei BON STORNO (Gegenbuchung eines abgeschlossenen Bons) · mit Zeilenumbruch eingeben.",
            "Rabattgründe",
            "Pflichtgrund bei RABATT · mit Zeilenumbruch eingeben.",
            "Abbruchgründe",
            "Pflichtgrund bei C / Verkauf abbrechen · mit Zeilenumbruch eingeben.",
            "Geräte · Technische Einrichtung",
            "Treiber, Ports und Protokolle werden einmalig vom Techniker eingerichtet.",
            "Bondrucker / Windows",
            "Auswahl unter Geräte → Drucker.",
            "Letzter Test",
            "Auto-Cut im Windows-Treiber verwenden",
            "Anschlüsse",
            "Die Kassenschublade wird zentral unter Geräte aktiviert und über den in der Drucker-Zentrale gewählten DK-Ausgang gesteuert.",
            "Barcode-Scanner · Protokoll",
            "Scanner-Modus",
            "HID = USB-/Bluetooth-Scanner verhält sich wie eine Tastatur. COM ist in diesem Build nicht als produktiver Scannerpfad implementiert.",
            "ENTER-Suffix verwenden (empfohlen)",
            "Wartezeit ohne ENTER (ms)",
            "Buchhaltung · Technische Zuordnung",
            "DATEV-Felder sind Vorbereitung und gehören nicht in die normale Bedienoberfläche.",
            "DATEV – Vorbereitung",
            "19% Konto",
            "19% Gegenkonto",
            "19% Kennzeichen",
            "7% Konto",
            "7% Gegenkonto",
            "7% Kennzeichen",
            "Status",
            "Lizenz"
        ];
        assert(
            technicianSettingsVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the technician settings vocabulary stays complete in Turkish and English while versions, paths and accounts stay untouched");

        // The settings window has its own status line, written from ~65 places
        // while the window is open - saving, printer tests, TSE activation,
        // DSFinV-K export, licence deactivation. Rendering at Opened cannot reach
        // any of it, so every write goes through the SettingsStatus property, and
        // the messages it shows are translated. Service answers, paths, printer
        // names and exception text pass through as data.
        var settingsSource = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var directSettingsStatusWrites = Regex.Matches(settingsSource, "StatusText\\.Text\\s*(?:=|\\+=)").Count;
        string[] settingsStatusVocabulary =
        [
            "Einstellungen geladen",
            "Bonlogo gespeichert",
            "Bonlogo entfernt",
            "Kartenterminal wird gesucht ...",
            "ZVT-Anmeldung wird geprüft ...",
            "Terminal-Tagesabschluss wird angestoßen ...",
            "Swissbit SDK und TSE werden geprüft ...",
            "Swissbit TSE-Aktivierung läuft. TSE nicht entfernen ...",
            "Windows wird nach einer kompatiblen Swissbit WormAPI.dll durchsucht ...",
            "Keine kompatible WormAPI.dll auf diesem PC gefunden. ",
            "Swissbit Download-Center wurde im Browser geöffnet. ",
            "Browser konnte nicht geöffnet werden: ",
            "Aktivierung gesperrt: Bestätigung für Credential-Seed / PIN / PUK fehlt.",
            "Aktivierungsanfrage fehlgeschlagen: ",
            "fiskaltrust Queue / Swissbit-SCU werden ohne TSE-Schreiboperation geprüft ...",
            "TSE TAR-Export läuft ...",
            "Audit-Export fehlgeschlagen: ",
            "DSFinV-K Export abgebrochen · kein Zielordner ausgewählt.",
            "DSFinV-K Export fehlgeschlagen: ",
            "DSFinV-K Export gesperrt: ",
            "DSFinV-K Prüfung fehlgeschlagen: ",
            "Keine aktive Lizenz vorhanden, die deaktiviert werden kann.",
            "Lizenz-Deaktivierung fehlgeschlagen: ",
            "Bonlogo aktiv · wird automatisch oben auf neue Bon-Ausdrucke gesetzt.",
            "Kein Bonlogo aktiv.",
            "Logo konnte nicht übernommen werden",
            "Logo konnte nicht entfernt werden",
            "Zuerst einen Bondrucker auswählen bzw. über die DRUCKER-ZENTRALE übernehmen.",
            "✓ Schubladenbefehl an Windows übergeben. Bitte physisch prüfen, ob die Kassenschublade geöffnet hat. TOR kann über die Windows-Druckwarteschlange keine mechanische Öffnung zurücklesen.",
            "⚠ Kassenschubladen-Test fehlgeschlagen",
            "Keine Windows-Drucker gefunden. Drucker zuerst in Windows installieren.",
            "Drucker gefunden. Gewünschten Drucker auswählen, testen und SPEICHERN drücken.",
            "Suche fehlgeschlagen",
            "Zuerst einen Drucker aus der Liste auswählen.",
            "Drucker nicht bereit",
            "Verbindung, Strom, Papier und Windows-Druckerstatus prüfen.",
            "An Windows übergeben. Papierausdruck am Gerät kontrollieren.",
            "Druckstatus unklar. Nicht blind erneut drucken; zuerst Windows-Druckwarteschlange und Papierbeleg prüfen.",
            "Drucker nicht bereit. Verbindung, Strom, Papier und Windows-Druckerstatus prüfen.",
            "DATEV-Status konnte nicht gelesen werden",
            "Nicht aktiv · Sicherungen werden unverschlüsselt geschrieben. Bei Aktivierung wird ein Wiederherstellungscode einmalig angezeigt - ohne diesen Code kann eine Sicherung nach einem Totalausfall dieses Computers nicht wiederhergestellt werden.",
            "Aktuell gilt der Auslieferungscode 0000. Solange er gilt, steht er auch auf der Anmeldeseite.",
            "Ein eigener Code ist gesetzt. Die Anmeldeseite nennt ihn nicht mehr.",
            "Mindestens 6 Zeichen.",
            "Die beiden Eingaben stimmen nicht überein.",
            "TOR Mail: Cloud-Dienst nicht verfügbar.",
            "TOR Mail: TOR POS Cloud ist noch nicht eingerichtet/aktiv.",
            "TOR-Mail-Status konnte nicht gelesen werden",
            "Google: TOR POS Cloud-Dienst nicht verfügbar.",
            "Google-Status konnte nicht gelesen werden",
            "Bereit",
            "Gespeicherte / zuletzt geprüfte Angaben – kein Live-Verbindungstest",
            "Kartenterminal",
            "Terminalhinweis",
            "TSE-Hinweis",
            "Verbindung prüfen / konfigurieren: Erweitert / Techniker → Zahlung bzw. TSE.",
            "Produktivfreigabe ist separat erforderlich; ein erreichbares Gerät genügt nicht.",
            "STANDARD-DATEI",
            "Export(e)",
            "bereit",
            "per E-Mail gesendet",
            "Versandfehler",
            "CSV-Ordner",
            "KASSENARCHIV ONLINE (optional/später)",
            "vorbereitete Paket(e) · Online-API noch nicht freigeschaltet.",
            "Aktiv · Wiederherstellungscode-Kennung",
            "Neue Sicherungen (manuell und täglich automatisch) werden verschlüsselt. Auf diesem Computer wird automatisch entschlüsselt; auf einem anderen Computer wird der Wiederherstellungscode benötigt.",
            "HINWEIS: Dieser Wiederherstellungscode stammt aus einer älteren Version und verwendet die frühere Schlüsselableitung. Vorhandene Sicherungen bleiben uneingeschränkt wiederherstellbar. Für das aktuelle Verfahren einmal WIEDERHERSTELLUNGSCODE NEU ERSTELLEN wählen und den neuen Code sicher notieren.",
            "TOR POS Cloud verbunden ✓",
            "Aktiver Versandweg",
            "eigener SMTP",
            "Beim Testversand wird zusätzlich geprüft, ob der zentrale TOR-Mail-Absender auf dem Server aktiv ist.",
            "Google verbunden ✓",
            "Noch kein Google-Konto verbunden."
        ];
        // The window has more status fields than the one line: the receipt logo,
        // the printer list, the drawer test, TOR Mail, Google, DATEV, the backup
        // encryption state. Several of them build their text from labels and
        // stored values on the same line, so the rule is per write: a status
        // write may not hand a bare German literal to the screen.
        var unwrappedStatusWrites = 0;
        var settingsLines = settingsSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var line = 0; line < settingsLines.Length; line++)
        {
            if (!Regex.IsMatch(settingsLines[line], "\\w*[Ss]tatus\\w*\\.Text\\s*\\+?=")) continue;

            var statement = string.Join("\n", settingsLines.Skip(line).Take(3));
            if (!statement.Contains("UiLanguage.T(", StringComparison.Ordinal))
                unwrappedStatusWrites++;
        }
        assert(
            directSettingsStatusWrites == 1 && unwrappedStatusWrites == 0 &&
            settingsStatusVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the settings status line is written through the language layer and the messages it shows are translated");

        // Every window that talks back while it is open: sign-in, the order
        // board, the dialogs, user management, diagnostics, the printer, scale,
        // terminal and SumUp setup, the first-run wizard, the DSFinV-K delivery,
        // inventory, the Z archive and the offers. Not one of them hands a bare
        // German literal to a status, message or hint line.
        //
        // Two are deliberately absent. MainWindow.axaml.cs sets its four mode
        // hints and then calls Apply() in the same method, so they are rendered
        // rather than written; its status line has its own check above.
        // OrderCustomerDisplayWindow faces the customer and stays German, like
        // the other customer display.
        string[] renderedStatusWindows =
        [
            "LoginWindow.axaml.cs", "OrderBoardWindow.cs", "WeightEntryWindow.cs", "TouchKeyboard.cs",
            "CheckoutReviewWindow.cs", "DigitalReceiptWindow.cs", "ProductEditorWindow.cs",
            "UserManagementWindow.cs", "Dialogs.cs", "DiagnosticsWindow.cs",
            "ManagementWindows.cs", "FirstRunSetupWindow.cs", "DsfinvkDeliveryWindow.cs",
            "PrinterSetupWindow.cs", "PaymentTerminalSetupWindow.cs", "ScaleSetupWindow.cs",
            "SumUpConnectionWindow.cs", "PromotionManagementWindow.cs", "GooglePairingWindow.cs",
            "MainWindow.Safety.cs", "SettingsWindow.axaml.cs"
        ];
        var bareStatusLiterals = new List<string>();
        foreach (var window in renderedStatusWindows)
        {
            var lines = File.ReadAllText(FindRepoFile($"Desktop/src/TorPos.App/{window}"))
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                var write = Regex.Match(lines[line], "\\w*(?:[Ss]tatus|[Mm]essage|[Hh]int)\\w*\\.Text\\s*\\+?=");
                if (!write.Success) continue;

                var chunk = string.Join("\n", lines.Skip(line).Take(3))[write.Index..];
                var end = chunk.IndexOf(';', StringComparison.Ordinal);
                var statement = end >= 0 ? chunk[..(end + 1)] : chunk;
                if (statement.Contains("UiLanguage.T(", StringComparison.Ordinal)) continue;

                // An escape sequence carries a letter and an interpolation hole
                // carries a variable name; neither is text on the screen.
                var visible = Regex.Replace(statement, "\\\\.", "");
                visible = Regex.Replace(visible, "\\{[^{}]*\\}", "");
                if (Regex.IsMatch(visible, "\"[^\"]*[A-Za-zÄÖÜäöüß][^\"]*\""))
                    bareStatusLiterals.Add($"{window}:{line + 1}");
            }
        }
        string[] windowStatusVocabulary =
        [
            "Anmeldung fehlgeschlagen",
            "Nur unbezahlte Bestellungen können aufgerufen werden.",
            "Status ändern erzeugt keine Zahlung. Hinweise werden beim nächsten Küchenbon mitgedruckt. Maximal 500 Bestellungen.",
            "Laden fehlgeschlagen",
            "Bitte Bestellung auswählen.",
            "Gespeichert. Zahlungsstatus bleibt unverändert.",
            "Bitte ein Gewicht größer als 0 eingeben.",
            "Geschützte Eingabe",
            "Bildschirmtastatur · DE",
            "Zuerst in ein Eingabefeld tippen.",
            "Mit dem Handy scannen: Bon ansehen, als PDF herunterladen, teilen oder drucken.",
            "Abrufbar bis",
            "Gewichtsartikel: Preis = €/kg. Verkauf kann ohne angeschlossene Waage manuell in g oder kg eingegeben werden. Bestand und Mindestbestand werden intern in kg geführt.",
            "Benutzer werden geladen ...",
            "3 Mitarbeiterkonten geladen.",
            "Achtung",
            "Mitarbeiterkonten gefunden.",
            "Admin-Passwort und PIN wurden geändert.",
            "Admin-Zugang konnte nicht geändert werden",
            "Alle drei Mitarbeiterkonten wurden gespeichert.",
            "Speichern fehlgeschlagen",
            "Bar-Anteil muss größer als 0 und kleiner als der Gesamtbetrag sein. Für eine reine Bar- oder Kartenzahlung BAR bzw. KARTE verwenden.",
            "Der Bar-Anteil wird sofort kassiert, der Karten-Anteil wird anschließend am Kartenterminal belastet.",
            "Archiv wird geladen ...",
            "Keine gespeicherten Bons für diesen Archivfilter gefunden.",
            "Bon(s) im Archiv gefunden. Alte Bons: nur Anzeigen / Kopie.",
            "ARCHIV",
            "Heutige Bons werden geladen ...",
            "Heute",
            "wurden noch keine echten Bons gespeichert.",
            "HEUTE",
            "Bon(s) · neueste zuerst",
            "BON-HISTORIE",
            "Bitte mindestens eine Menge größer als 0 eingeben.",
            "Performance-Messwerte zurückgesetzt.",
            "Log-Ordner konnte nicht geöffnet werden",
            "Geräteprüfung läuft …",
            "Geräteprüfung abgeschlossen",
            "gesamt.",
            "Diagnose fehlgeschlagen",
            "Bearbeiter angeben.",
            "Fehler",
            "1,00 € TESTANFORDERUNG wurde von der SumUp API angenommen.",
            "ABBRUCHANFORDERUNG an SumUp gesendet. SumUp liefert dafür keine synchrone Abbruchbestätigung. Solo-Anzeige kontrollieren. Der Abbruch funktioniert nur, solange das Gerät auf eine Karten-/PIN-Aktion wartet.",
            "ANGEBOT",
            "Abgebrochen / Zeitlimit erreicht.",
            "Angebot",
            "Angebot(e) geladen · Betriebstag",
            "Artikel angezeigt",
            "Automatische Suche startet …",
            "Automatische Zahlung ist für dieses Profil noch nicht freigegeben.",
            "BARCODE SCANNEN",
            "Barcode",
            "Bericht an",
            "Bestand",
            "Bestand wurde seit dem Öffnen an anderer Stelle verändert. TOR hat den neueren Bestand NICHT überschrieben. Bitte aktuellen Wert prüfen und erneut speichern.",
            "Bestätigung am Solo kontrollieren; danach Geräteliste laden und Status prüfen. Keine Zahlung gestartet.",
            "Bitte Auswahl kontrollieren.",
            "Bitte Papierformat auswählen.",
            "Bitte das Fenster aus einer ausgewählten Warengruppe öffnen.",
            "Bitte einen Artikel auswählen.",
            "Bitte einen Windows-Drucker auswählen.",
            "Bitte einen archivierten Z-Bericht auswählen.",
            "Bitte gültige Cent-Beträge eingeben.",
            "Bitte mindestens den Firmennamen eintragen.",
            "Bitte physisch prüfen, ob die Kassenschublade geöffnet hat. Falls nicht: den anderen Kassenschubladen-Ausgang wählen und erneut testen.",
            "Bitte zuerst ein Angebot auswählen.",
            "Bitte zuerst einen Artikel auswählen.",
            "Bitte zuerst einen Drucker wählen.",
            "Browser konnte nicht geöffnet werden",
            "Büro-/PDF-/Faxdrucker bleiben sichtbar, werden aber nicht als Bondrucker freigegeben. Auswahl prüfen, TESTBON DRUCKEN und danach DIESEN DRUCKER VERWENDEN.",
            "DSFinV-K ZIP-Paket wird erstellt …",
            "DSFinV-K vollständig kopiert",
            "DSFinV-K wird kopiert …",
            "DSFinV-K wurde per E-Mail an",
            "Der lokale Export bleibt unverändert erhalten; USB-Kopie ist weiterhin möglich.",
            "Dieses Angebot ist bereits deaktiviert.",
            "Druck-/PDF-Fenster geöffnet. Archivdaten bleiben unverändert.",
            "Druckereinstellungen konnten nicht geladen werden",
            "Druckersuche fehlgeschlagen",
            "Druckersuche hat länger als 12 Sekunden gedauert. Netzwerk-/Offline-Windows-Drucker prüfen und erneut suchen.",
            "Druckfehler",
            "Druckfunktion ist in diesem Fenster nicht verbunden.",
            "Druckziel auswählen. TOR merkt sich Drucker und Papierformat für diesen Berichtstyp.",
            "E-Mail wird an",
            "Einrichtung",
            "Epson/Star-Bondrucker erkannt",
            "Epson/Star-Bondrucker gefunden. Vorauswahl",
            "Export lokal gespeichert. USB kopieren oder E-Mail senden ist möglich.",
            "FEHLER",
            "Gerät(e) gefunden. Gerät auswählen und Status prüfen. 'paired' allein bestätigt keine Online-Verbindung.",
            "Geräteparameter eingeben und KONFIGURATION PRÜFEN wählen.",
            "Gespeicherter Bondrucker",
            "Google-Anmeldung fehlgeschlagen",
            "JETZT SOLO ANSEHEN: Wenn dort 1,00 € erscheint, ist TOR POS → SumUp → Solo erfolgreich. KEINE KARTE VORHALTEN. Danach sofort TEST ABBRECHEN drücken oder am Solo abbrechen.",
            "Kein Artikel gefunden",
            "Kein USB-Laufwerk ausgewählt. Alternativ ANDEREN ORDNER WÄHLEN benutzen.",
            "Kein Wechselmedium automatisch erkannt. USB einstecken und aktualisieren oder ANDEREN USB-/ORDNER WÄHLEN benutzen.",
            "Keine Windows-Drucker gefunden. Epson-/Star-Treiber zuerst in Windows installieren.",
            "Keine Windows-Drucker gefunden. Unter Einstellungen → Geräte prüfen.",
            "Kopie fehlgeschlagen",
            "Kopiervorgang abgebrochen.",
            "Kopplung kann erfolgt sein: zuerst Geräteliste laden.",
            "Kopplungsantwort",
            "Mindestbestand-Warnung(en)",
            "Modell eindeutig.",
            "Netzwerkfehler. Internetverbindung prüfen.",
            "Noch kein Bondrucker gespeichert. Automatische Suche startet …",
            "OPTIONAL · Scanner ist der Hauptweg",
            "PDF gespeichert",
            "PDF-Fehler",
            "PDF-Speichern abgebrochen.",
            "Papierausdruck prüfen.",
            "Pfand-/Leergutwerte gespeichert.",
            "Profil auswählen, Angaben eintragen und SPEICHERN. ZVT-Profile können danach ohne Zahlung getestet werden.",
            "Profil gespeichert.",
            "Profil ist vorbereitet, aber automatische Belastung ist noch nicht freigegeben. TOR lässt dieses Profil deshalb absichtlich deaktiviert.",
            "Profil vorgemerkt. Automatische Zahlung bleibt bis zur Adapter-/Partnerfreigabe AUS.",
            "QR-Code abgelaufen. Fenster schließen und einen neuen QR-Code erzeugen.",
            "SCANNEN → F5 KASSIEREN",
            "Scanner-Treffer",
            "Schnellwahl optional · Scanner bleibt aktiv",
            "Schubladenbefehl",
            "Start- und Enddatum sind erforderlich.",
            "SumUp wird abgefragt ...",
            "SumUp-Antwort konnte nicht verarbeitet werden. Geräteliste prüfen.",
            "TOUCH · Artikel → direkt im Bon",
            "TOUCH · Warengruppe → Artikel",
            "TOUCH → ARTIKEL → F5 KASSIEREN",
            "Training-Anmeldung nur mit Code 0000 · Benutzer-Passwort ist dann nicht erforderlich.",
            "Training-Anmeldung nur mit dem vom Betreiber gesetzten Training-Code · Benutzer-Passwort ist dann nicht erforderlich.",
            "Training-Code ist falsch.",
            "Training-Code ist falsch. Standard-Code: 0000.",
            "Treffer für",
            "Unerwartete SumUp-Antwort. Geräteliste erneut prüfen.",
            "Ungültiger Bestand.",
            "Unter Berichte & E-Mail ist noch keine Empfänger-Adresse gespeichert.",
            "Unter DATEV ist noch keine Steuerberater-E-Mail gespeichert.",
            "Verbindung wird erneut geprüft …",
            "Verbindung wird geprüft …",
            "WARENGRUPPE ODER ARTIKEL ANTIPPEN",
            "Warenwert EK",
            "Warte auf Bestätigung am Handy …",
            "Windows-Drucker gefunden",
            "Windows-Drucker werden automatisch geprüft …",
            "Windows-Drucker werden geprüft …",
            "ZIP-Paket ist",
            "ZVT-Anmeldung wird geprüft …",
            "ZVT-Profil kann produktiv verwendet werden, sobald Provider/Terminal ZVT freigeschaltet hat und der Verbindungstest erfolgreich ist.",
            "Zugang erfolgreich. Noch kein API-Reader gekoppelt. Unten einen Solo koppeln.",
            "aktiv",
            "als",
            "an Windows übergeben.",
            "angezeigt.",
            "bitte Artikel auswählen.",
            "deaktiviert.",
            "erkannt",
            "gesendet",
            "gesendet …",
            "gesendet.",
            "gespeichert",
            "gespeichert · Cloud-Abgleich vorgemerkt · nächsten Barcode scannen.",
            "gespeichert.",
            "gespeichert. Modell blieb absichtlich 'nicht eindeutig'; TOR hat kein Modell geraten.",
            "groß. E-Mail-Versand ist auf 15 MB begrenzt; bitte USB/Datenträger verwenden.",
            "neuen Bestand eingeben und ENTER drücken.",
            "⚠ Automatische Druckersuche fehlgeschlagen",
            "⚠ Bei MANUELL muss die manuelle Gewichtseingabe aktiviert bleiben.",
            "⚠ Bitte eine gültige Empfänger-E-Mail eingeben.",
            "⚠ COM-Port und gültige Baudrate angeben.",
            "⚠ DSFinV-K E-Mail-Versand fehlgeschlagen",
            "⚠ Druckersuche dauert zu lange. Offline-/Netzwerkdrucker in Windows prüfen.",
            "⚠ IP-Adresse und gültigen TCP-Port angeben.",
            "⚠ Kein Epson-/Star-Bondrucker eindeutig erkannt. Vorhandene Windows-Drucker wurden geladen; bitte manuell auswählen.",
            "⚠ Keine Windows-Drucker gefunden. Epson-/Star-Treiber zuerst in Windows installieren.",
            "⚠ TSE antwortet nicht innerhalb von 10 Sekunden. USB/SDK prüfen; TOR POS bleibt bedienbar.",
            "⚠ Testdruck fehlgeschlagen",
            "⚠ Waagenbarcode-Präfix muss 1–4 Ziffern enthalten.",
            "✓ Gespeichert. Separate Waage ablesen → Gewicht am Kassenartikel manuell eingeben.",
            "✓ Konfiguration formal gültig. Ein echter Live-Gerätetest wird erst mit dem freigegebenen Protokoll/Adapter durchgeführt.",
            "✓ Manuelle Gewichtseingabe ist betriebsbereit.",
            "✓ Manuelle Waage: keine Verbindung erforderlich. Gewichtsartikel können sofort in g oder kg erfasst werden.",
            "✓ Testbon an Windows übergeben. Papierausdruck am Gerät kontrollieren.",
            "✓ Waagenparameter gespeichert. Manuelle Eingabe bleibt als sichere Rückfallebene verfügbar."
        ];
        assert(
            bareStatusLiterals.Count == 0 &&
            windowStatusVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the windows beyond the till and the settings write their running messages through the language layer too");

        // The protected technician area: TSE activation, law and fiscal status,
        // and licensing. Serial numbers, device paths, certificate numbers, the
        // customer number and the product names - Swissbit, fiskaltrust, the SDK
        // generations - are data or names and have no entry; what the operator
        // reads around them does.
        string[] fiscalSettingsVocabulary =
        [
            "TSE-Aktivierung",
            "TOR POS unterstützt die direkte Swissbit-WORM-API und prüft ab R175 zusätzlich eine vorhandene ",
            "Recht & Fiskal",
            "Deutschland-Status nach AO § 146a, KassenSichV und DSFinV-K. Produktivbetrieb wird nicht per Benutzer-Schalter freigegeben.",
            "Lizenzierung",
            "Kunden-Nr. und genau einem PC zugeordnete, kryptografisch signierte TOR-POS-Lizenz. Die kommerzielle Lizenz ersetzt keine TSE-, KassenSichV- oder DSFinV-K-Prüfung.",
            "Alternative: fiskaltrust + Swissbit",
            "TSE-Status",
            "Automatisch aus der TSE lesen",
            "Kassen-Zuordnung",
            "Erst-Aktivierung – Zugangsdaten nur temporär",
            "Produktivfreigabe",
            "Elektronisches Aufzeichnungssystem",
            "Pflichtmodule",
            "Mitteilung nach § 146a Abs. 4 AO",
            "Prüfungsdaten / Export",
            "Lizenzstatus",
            "Aktivierung",
            "Swissbit TSE beim Programmstart automatisch prüfen",
            "Konfiguration",
            "Swissbit-SCU Version",
            "TSE-Seriennummer",
            "BSI-Zertifizierungsnummer",
            "Gerätepfad",
            "Aktiviert am",
            "Zertifikatsablauf",
            "Client-ID / Kassen-ID",
            "Admin-PIN",
            "TimeAdmin-PIN",
            "Admin-PUK",
            "Credential-Seed",
            "Meldedatum",
            "Datum der Anschaffung",
            "Datum der Außerbetriebnahme",
            "DSFinV-K · Von",
            "DSFinV-K · Bis",
            "Kunden-Nr.",
            "Kunde",
            "Hersteller",
            "Standardprodukt",
            "Anschluss",
            "USB / Windows-Laufwerk",
            "Kompatibilitätsprinzip",
            "Sicherheitsmodus",
            "Nur Lese-/Verbindungstest · keine Registrierung, Aktivierung oder Transaktion",
            "Regel",
            "Produktivfreigabe nur nach realer TSE-Signierung, DSFinV-K-Export und Belegprüfung.",
            "Modell",
            "DSFinV-K Zielversion",
            "TSE-Aktivierung → TSE TAR EXPORT. BMF verlangt das TAR-Format für TSE-Daten bei Prüfung.",
            "Start- und Enddatum sind frei wählbar. Vor dem Export prüft TOR den gewählten Zeitraum; fehlerhafte oder nicht abgeschlossene Daten werden nicht als fertiger Prüfdatensatz ausgegeben.",
            "TOR ruft bei einer teilweise veränderten, aber noch nicht initialisierten TSE nicht automatisch erneut Setup auf. ",
            "WormAPI.dll kann jetzt real geladen, die TSE erkannt, initialisiert und per Start/Update/FinishTransaction angesprochen werden. ",
            "Zeitraum-Regel",
            "Von/Bis wählt den Prüfungszeitraum. DSFinV-K bleibt Z-Bericht-basiert: TOR exportiert nur vollständig abgeschlossene Kassenabschluss-Zeiträume, deren Z-Abschluss im gewählten Zeitraum liegt. Vorgänge nach dem letzten Z-Bericht werden nicht als abgeschlossen ausgegeben.",
            "Solange hier TESTBETRIEB angezeigt wird, darf TOR POS nicht als produktive finanzamtkonforme Kasse eingesetzt oder entsprechend beworben werden. Die Freigabe ist absichtlich nicht manuell überschreibbar.",
            "Lizenz-Deaktivierung",
            "Die lokale Deaktivierung sperrt die installierte Lizenz-ID auf diesem PC und erzeugt zusätzlich einen Deaktivierungsbeleg auf dem Desktop. Eine neue Aktivierung benötigt danach eine neu ausgestellte Lizenz mit neuer Lizenz-ID.",
            "Wichtige Trennung",
            "Eine aktive TOR-POS-Lizenz erlaubt die vertragliche Softwarenutzung. Der steuerliche Produktivbetrieb bleibt weiterhin gesperrt, bis reale TSE-, Beleg- und DSFinV-K-Abnahmetests erfolgreich abgeschlossen sind.",
            "FISKALTRUST PRÜFEN (NUR LESEN)",
            "Ich bestätige, dass Credential-Seed, PIN und PUK zur angeschlossenen TSE gehören.",
            "SDK AUF PC SUCHEN",
            "WORMAPI.DLL AUSWÄHLEN",
            "SWISSBIT DOWNLOAD-CENTER",
            "TSE SUCHEN",
            "TSE AKTIVIEREN",
            "TSE TAR EXPORT",
            "AUDIT-LOG EXPORTIEREN",
            "DSFINV-K 2.4 PRÜFEN",
            "DSFINV-K 2.4 EXPORTIEREN",
            "AKTIVIERUNGSANFRAGE ERSTELLEN",
            "LIZENZDATEI IMPORTIEREN",
            "LIZENZ DEAKTIVIEREN",
            "noch nicht geprüft",
            "TSE-Dateien: ",
            "Prüfung nach 8 Sekunden beendet. Es wurde keine TSE-Schreiboperation ausgeführt.",
            "5 Zeichen · wird nicht gespeichert",
            "6 Zeichen · wird nicht gespeichert",
            "Nur Seed des TSE-Lieferanten verwenden",
            "AKTIV",
            "z. B. TOR-KD-000123",
            "Kundenname / Firma",
            "TOR liest nur Configuration-*.json unter ProgramData\\fiskaltrust\\service. AccessToken wird weder gelesen noch angezeigt.",
            "Beispiel D:. TOR prüft nur, ob TSE_INFO.DAT an diesem Pfad vorhanden ist.",
            "Geprüft wird ausschließlich /json/v1/Echo.",
            "Es wird nur geprüft, ob der konfigurierte TCP/gRPC-Port erreichbar ist.",
            "Wird direkt über die Swissbit WORM API ausgelesen.",
            "Wird nicht erfunden. Falls die verwendete SDK-Version sie nicht direkt liefert, bleibt das Feld leer und wird später aus zertifizierter Produkt-/Zertifikatszuordnung ergänzt.",
            "Neue/aktuelle Admin-PIN. Nicht in TOR-Einstellungen gespeichert.",
            "Für Zeit-Synchronisation der TSE. In v0.6.8 nur für diesen Vorgang im Arbeitsspeicher.",
            "Nicht speichern. Falsche PUK/Seed-Angaben können eine Produktiv-TSE dauerhaft sperren.",
            "Nicht pauschal annehmen: Der Seed kann vom TSE-Lieferanten abhängen.",
            "TOR übermittelt in dieser Version NICHT an Mein ELSTER / ERiC.",
            "Nur Dokumentationsfeld. Eine Eingabe löst keine Finanzamt-Übermittlung aus.",
            "Bei Leasing oder Leihe: Beginn. Die Mitteilungsdaten stehen im Menü unter KASSENMELDUNG.",
            "Leer lassen, solange die Kasse in Betrieb ist.",
            "Erster gewünschter Kalendertag.",
            "Letzter gewünschter Kalendertag · einschließlich.",
            "Pflichtfeld. Diese Nummer wird vom TOR-Händler vergeben und in der signierten Lizenz gespeichert.",
            "Wird zusammen mit Kunden-Nr., PC-Gerätecode, Installations-ID und Version in die Aktivierungsanfrage geschrieben."
        ];
        assert(
            fiscalSettingsVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the TSE, legal and licensing pages are readable in Turkish and English while the fiscal records keep their German names");

        // The windows an owner works in rather than sells from: the product
        // editor with its menus, variants and group colours, the receipt and
        // payment dialogs, the till's own mode labels and file pickers, and
        // inventory, the Z journal and the deposit keys. Colour values, the
        // fixed-width stock listing and the product names stay as they are.
        string[] masterDataVocabulary =
        [
            "Im Haus/Außer Haus MwSt.-Regel anwenden",
            "Verkauf nach Gewicht (Gramm / Kilogramm) · Preis pro kg",
            "ÜBERNEHMEN",
            "Bitte Warengruppe auswählen",
            "wird automatisch vergeben",
            "Name, EAN oder Artikel-Nr. · Scanner möglich",
            "optional, z. B. GETRÄNK",
            "STAMMDATEN",
            "Reihenfolge: 1. Gruppe → 2. Warengruppe mit MwSt. → 3. Artikel. ",
            "MwSt.-Regel: Der Artikel besitzt keine eigene MwSt.-Auswahl. ",
            "MwSt. automatisch",
            "VARIANTEN / GRÖSSEN",
            "Direkt mit diesem Artikel speichern, z. B. Klein / Mittel / Groß. Kein separates Varianten-Menü mehr nötig.",
            "MENÜ / COMBO",
            "Bestandteile werden direkt aus dem normalen Artikelstamm gewählt. Der Menü-Verkaufspreis oben bleibt eigenständig; die normalen Artikelpreise dienen nur als Marktwert für Bestand und MwSt.-Aufteilung.",
            "Auswahl: Feld leer lassen = fester Bestandteil. Gleichen Gruppennamen verwenden (z. B. GETRÄNK) = Kunde/Kassierer wählt beim Verkauf genau einen Artikel aus dieser Gruppe. Kein Aufpreis-Feld.",
            "Auswahlgruppe (optional)",
            "Dieser Artikel ist bereits im Menü vorhanden. Bitte denselben Artikel nicht gleichzeitig fest und als Auswahloption verwenden.",
            "Kein Menü definiert. Der Artikel wird normal mit seiner Warengruppen-MwSt. verkauft.",
            "FEHLER: ",
            "Bitte gültigen Menü-Verkaufspreis eingeben.",
            "Farbe der Warengruppen-Taste",
            "Jede Warengruppe kann eine eigene Farbe erhalten. Schnellfarbe wählen oder einen beliebigen HEX-Wert eingeben.",
            "SCHNELLFARBEN",
            "48 Schnellfarben + freie Farbauswahl: z. B. #C0392B, #00AEEF oder #F4C542. Damit stehen praktisch alle RGB-Farben zur Verfügung.",
            "Kein Bild",
            "FEHLER: Bitte zuerst eine Warengruppe auswählen.",
            "FEHLER: Bitte zuerst eine Gruppe auswählen.",
            "FEHLER: Löschen ist nur für Administratoren erlaubt.",
            "ANGEBOT: Bitte zuerst eine Warengruppe auswählen.",
            "ANGEBOT: Bitte zuerst einen Artikel auswählen.",
            "ANGEBOT: Gewichtsartikel sind in R170 von Artikel-/Warengruppen-Angeboten ausgenommen, damit Teil-kg-Verkäufe centgenau bleiben. Normaler Bon-Rabatt bleibt möglich.",
            "FEHLER: Bitte zuerst einen Artikel auswählen.",
            "Löschen = deaktivieren. Fiskal-/Verkaufs-Historie wird nicht gelöscht.",
            "ARTIKEL FÜR MENÜ WÄHLEN",
            "Bestandteil",
            "Menge",
            "Menge im Menü",
            "TOR POS – Stammdaten",
            "Produktbild auswählen",
            "Menü-Bestandteil",
            "Menü-Menge",
            "ANDERER BETRAG",
            "WEITER · ZUR KARTENZAHLUNG",
            "SPEICHERN",
            "KARTE BESTÄTIGEN",
            "ARCHIV SUCHEN",
            "HEUTE AKTUALISIEREN",
            "ARCHIV / SUCHE",
            "RETOURE BUCHEN",
            "NEU ZÄHLEN",
            "Fest im Menü: ",
            "ZAHLBETRAG WÄHLEN",
            "GEMISCHTE ZAHLUNG",
            "BAR-ANTEIL EINGEBEN",
            "Der Kunde gibt Leergut zurück. Der Pfandbetrag wird abgezogen; ist er höher als der Einkauf, wird die Differenz bar ausgezahlt. Menge vorher über die Zifferntasten eingeben.",
            "Name, z.B. Klein 26 cm",
            "KARTENZAHLUNG · TEST",
            "Testsimulation – keine echte Terminalbuchung. ",
            "TOR-Regel: Offene geparkte Bons sperren den Z-Abschluss.",
            "Bonnummer (optional)",
            "ARCHIV · Ältere Bons können angesehen oder als Kopie gedruckt werden. STORNO / TEILRETOURE ist ausschließlich am Verkaufstag möglich.",
            "Von: ",
            " Bis: ",
            "TEILRETOURE",
            "Menge je Position eingeben, die zurückgenommen werden soll. Bereits zurückgenommene Mengen werden serverseitig erneut geprüft.",
            "Bemerkung (optional), z. B. Zählfehler Wechselgeld",
            "Kommerzielle Lizenz deaktivieren",
            "Nach der Deaktivierung wird der kommerzielle Verkauf auf diesem PC gesperrt. ",
            "Für Artikel ohne Stammdatensatz oder eine einmalige freie Preiseingabe. ",
            "Barzahlung",
            "Gemischte Zahlung",
            "Variante",
            "Kassensturz bestätigen",
            "Lizenz deaktivieren",
            "ALLE BERICHTE ALS PDF SPEICHERN",
            "◀ SCHNELLWAHL",
            "EINGABE: ",
            "EINGABE: —",
            "Alle Berichte gespeichert: ",
            "PDF-Export fehlgeschlagen: ",
            "BAR · TRAINING",
            "KARTE · TRAINING",
            "BAR · DEMO",
            "KARTE · DEMO",
            "BAR · TEST",
            "KARTE · TEST",
            "SCHNELLWAHL · EINZELHANDEL",
            "WARENGRUPPEN · GASTRONOMIE",
            "F5 · ZAHLART",
            "TOR Artikel-CSV importieren",
            "TOR Artikel exportieren",
            "TOR POS Datenbank auswählen",
            "Buchungsdaten exportieren",
            "Zielordner für alle Berichte wählen",
            "Zielordner für GDPdU/GoBD Prüf-Unterlagen",
            "DSFinV-K Zielordner",
            "TSE TAR-Export speichern",
            "PDF SPEICHERN",
            "BERICHT DRUCKEN",
            "SUCHEN / SCANNER",
            "ALLE ARTIKEL",
            "BESTAND SPEICHERN",
            "WARENBESTAND · DRUCKEN / PDF",
            "ANZEIGEN",
            "Z-BERICHT PDF / DRUCKEN",
            "Papier:",
            "Drucker:",
            "Name / EAN / Artikel-Nr. · Scanner hier lesen",
            "Gezählter Bestand:",
            "INVENTUR / WARENBESTAND",
            "Gesamte aktive Artikelliste · Scanner-EAN lesen → Bestand eingeben → ENTER. Mindestbestand, EK/VK und Warenwert sind direkt sichtbar.",
            "Z-ABSCHLUSS-JOURNAL",
            "PFAND / LEERGUT",
            "Direkte Pfand-/Leergut-Tasten der Einzelhandel-Version. Werte in Cent.",
            "Bericht als PDF speichern",
            "Inventur / Warenbestand",
            "Z-Abschluss-Journal",
            "Pfand / Leergut"
        ];
        assert(
            masterDataVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the product, receipt and management windows read in the chosen language down to their buttons and hints");

        // What is left of the interface: the setup wizards for the printer, the
        // scale, the card terminal, SumUp and Google, the first-run wizard, the
        // diagnostics window, the payment review, the receipt choice, the order
        // board, the reason picker, the weight dialog and the demo notice.
        // Example values, IP addresses and account names are examples, not text.
        string[] setupWindowVocabulary =
        [
            "VERBINDUNG TESTEN",
            "ZVT ANMELDUNG TESTEN",
            "TERMINAL-TAGESABSCHLUSS",
            "BONLOGO AUSWÄHLEN",
            "LOGO ENTFERNEN",
            "STATUS AKTUALISIEREN",
            "ÖFFNEN",
            "JA, TRENNEN",
            "DATEV-Status wird geladen …",
            "Kein Logo ausgewählt",
            "Einmal auswählen, speichern – TOR setzt das Logo danach automatisch oben auf den Bon.",
            "Kassenmeldung automatisch jede Minute. TOR POS gleicht Artikel/Bestand nach Verkauf, Artikelpflege und Inventur automatisch im Hintergrund ab; zusätzlich erfolgt beim Start und stündlich ein Reparatur-Snapshot. ",
            "Berichte werden als PDF geöffnet; den A4-Drucker im PDF-Druckdialog auswählen.",
            "FISKALSTATUS FEHLER",
            "Gespeichertes App-Passwort vorhanden · leer lassen zum Behalten",
            "Gespeichertes Passwort nicht lesbar · neu eingeben",
            "Techniker-Passwort eingeben.",
            "Techniker-Passwort",
            "Neues Passwort (mind. 6 Zeichen)",
            "Wiederholen",
            "Bonlogo auswählen",
            "Offizielle Swissbit WormAPI.dll auswählen",
            "Zielordner für DSFinV-K Export auswählen",
            "Signierte TOR-POS-Lizenz auswählen",
            "Techniker-Zugang",
            "Techniker-Passwort einrichten",
            "ALLE GERÄTE PRÜFEN",
            "PERFORMANCE AKTUALISIEREN",
            "MESSWERTE ZURÜCKSETZEN",
            "LOG-ORDNER ÖFFNEN",
            "BAR TESTBON VORBEREITEN",
            "80 MM TESTBON DRUCKEN",
            "ALS GEKLÄRT MARKIEREN",
            "ERNEUT DRUCKEN",
            "Noch nicht geprüft.",
            "SYSTEMSTATUS / DIAGNOSE",
            "Geräte werden nur auf Anforderung geprüft. Jeder externe Test ist zeitlich begrenzt und darf die Kassenoberfläche nicht lange blockieren.",
            "z. B. 20260914-153000-AB12",
            "Fehler-ID eingeben, die einem Kassierer/Kunden angezeigt wurde.",
            "Noch nicht vorbereitet. Dieser Bereich startet keine TSE-Transaktion.",
            "Konnte nicht geladen werden: ",
            "Keine blockierten Kartenerstattungen.",
            "Bearbeiter",
            "Ergebnis der Prüfung (z. B. 'Terminal/Bank bestätigt: keine Belastung')",
            "Keine blockierten Küchenbons.",
            "Noch keine Messungen.",
            "TOR POS · Systemstatus / Diagnose",
            "Zahlung bestätigt / Buchung abschließen",
            "Keine Belastung nachweislich bestätigt",
            "Später prüfen · gesperrt lassen",
            "Zugang sicher speichern",
            "Administrator",
            "Admin-Passwort",
            "Terminalbeleg / Trace / Prüfnachweis und Begründung",
            "Zuerst Terminal / Netzbetreiber prüfen. Diese Auswahl sendet weder Zahlung noch Storno an das Terminal.",
            "Aktuelles Passwort",
            "Neues Passwort (mindestens 4 Zeichen)",
            "Neues Passwort wiederholen",
            "Neue PIN (4 Ziffern)",
            "Vor dem ersten Kassenstart müssen Standard-Zugangsdaten geändert werden.",
            "ZAHLUNG PRÜFEN",
            "ADMIN-ZUGANG EINRICHTEN",
            "1 · GERÄTELISTE LADEN",
            "2 · GERÄTESTATUS PRÜFEN",
            "3 · 1,00 € TEST AN SOLO SENDEN",
            "4 · TEST ABBRECHEN",
            "SOLO MIT DIESEM KONTO KOPPELN",
            "Händlercode (Merchant Code)",
            "SumUp API-Key",
            "Kopplungscode vom Solo (nur bei neuer Kopplung)",
            "Geräteliste laden, danach Solo auswählen",
            "Verbindungstest. Der 1,00-€-Test sendet eine ECHTE Zahlungsanforderung an das Solo. KEINE KARTE vorhalten; danach ABBRECHEN.",
            "WICHTIG: 1,00 € SENDEN dient nur dazu zu prüfen, ob der Betrag auf dem Solo erscheint. Wenn eine Karte vorgehalten wird, kann eine echte Zahlung entstehen. Sobald 1,00 € am Solo sichtbar ist, TEST ABBRECHEN drücken oder direkt am Solo abbrechen.",
            "Neues Gerät: Solo abmelden → Verbindungen / Connections → API → Verbinden / Connect. Code innerhalb von 5 Minuten verwenden.",
            "SUMUP SOLO · VERBINDUNG + 1,00 € TEST",
            "KARTE TEST im normalen Kassenbild bleibt Simulation. Dieser separate Admin-Test sendet nur eine feste 1,00-€-Anforderung an das ausgewählte Solo; Zugangsdaten werden nicht gespeichert.",
            "SumUp Solo · Verbindung + 1,00 € Gerätetest",
            "ALLE BENUTZER SPEICHERN",
            "ADMIN-ZUGANG ÄNDERN",
            "Benutzer aktiv",
            "MITARBEITER & RECHTE",
            "Neben dem Admin stehen genau drei Mitarbeiterkonten bereit. ",
            "ADMIN-ZUGANG",
            "Standard bei einer neuen Installation: Benutzer admin · Passwort admin · PIN 1234. ",
            "Aktuelles Admin-Passwort",
            "Neues Admin-Passwort",
            "Neue 4-stellige Admin-PIN",
            "Berechtigungen",
            "Einstellungen und Benutzerverwaltung bleiben immer ausschließlich beim Admin.",
            "TOR POS – Mitarbeiter & Rechte",
            "Automatische Kartenterminal-Anbindung aktivieren",
            "PROFIL SPEICHERN",
            "SUMUP GERÄT / PAIRING ÖFFNEN",
            "Modell / eigene Notiz (optional)",
            "z. B. 192.168.1.50",
            "IP und Port stehen je nach Anbieter im Terminalmenü. Häufig ist der ZVT-Port 20007; maßgeblich ist immer die tatsächliche Terminalkonfiguration.",
            "KARTENTERMINAL VERBINDEN",
            "Marke bzw. Terminalfamilie auswählen. TOR zeigt nur die Angaben, die für diesen Integrationsweg benötigt werden. Ein gelisteter Hersteller bedeutet nicht automatisch, dass dessen proprietäre Schnittstelle ohne Providerfreigabe verwendet werden darf.",
            "Sicherheitsregel: TOR POS speichert keine vollständige Kartennummer, keine PIN und keinen CVV/CVC. Nicht freigegebene Providerprofile bleiben automatisch deaktiviert; TOR startet darüber keine vermeintliche Zahlung.",
            "TOR POS · Kartenterminal-Assistent",
            "Manuelle Gewichtseingabe immer erlauben",
            "KONFIGURATION PRÜFEN",
            "WAAGEN-EINSTELLUNGEN SPEICHERN",
            "z. B. Bizerba, Mettler Toledo, CAS",
            "Modellbezeichnung",
            "z. B. COM3",
            "z. B. 192.168.1.60",
            "z. B. 21",
            "WAAGE / GEWICHTSVERKAUF",
            "Manueller Gewichtsverkauf funktioniert immer ohne Kassenanschluss: Gewicht an einer separaten Waage ablesen und beim Artikel in g oder kg eingeben. Diese Einstellungen bereiten zusätzlich angeschlossene Waagen bzw. Waagen-Barcodes vor.",
            "Hinweis: SERIELL/LAN speichert die Geräteparameter, aktiviert aber ohne freigegebenes Herstellerprotokoll keinen automatischen Gewichtsempfang. TOR gibt deshalb keinen erfolgreichen Gerätetest vor, wenn nur IP/COM konfiguriert ist.",
            "TOR POS · Waagen-Einstellungen",
            "ZURÜCK",
            "WEITER",
            "Bondrucker verwenden",
            "Automatische Kartenterminal-Anbindung verwenden",
            "Firma & Kassenart",
            "Kontrolle",
            "Fertig",
            "✓ Testbon gesendet.",
            "TOR POS – Ersteinrichtung",
            "PAPIERBELEG",
            "DIGITALBELEG (QR)",
            "Wie möchte der Kunde den Beleg erhalten?",
            "Digitalbeleg: Der Kunde scannt den QR-Code und öffnet den Bon im Browser - lesen, als PDF herunterladen, teilen oder drucken. Nur mit seiner Zustimmung.",
            "DIGITALER KASSENBON",
            "Wird bei TOR Cloud erstellt …",
            "IHR DIGITALER KASSENBON",
            "DIGITALBELEG NICHT MÖGLICH",
            "Beleg",
            "Digitaler Kassenbon",
            "z. B. steuerberater@kanzlei.de",
            "DSFINV-K EXPORT IST FERTIG",
            "Wie möchten Sie die Prüfungsdaten weitergeben? Der bereits erzeugte Original-Export bleibt unverändert erhalten.",
            "TOR kopiert den vollständigen DSFinV-K-Ordner mit allen CSV-, XML-, DTD- und Protokolldateien. Es wird nicht nur eine einzelne CSV kopiert.",
            "Für E-Mail packt TOR den vollständigen Export in eine ZIP-Datei und verwendet den unter Berichte & E-Mail aktiven Versandweg (TOR Mail, Google oder SMTP). Die Empfänger-Adresse kann für diesen Versand frei geändert werden.",
            "E-Mail-Hinweis: Große Prüfdatensätze können Mail-Größenlimits überschreiten. TOR versendet deshalb keine ZIP-Datei über 15 MB; in diesem Fall USB/Datenträger verwenden.",
            "TOR POS · DSFinV-K weitergeben",
            "USB-Laufwerk oder Zielordner auswählen",
            "LINK AUF DIESEM PC ÖFFNEN",
            "Warte auf Google-Anmeldung …",
            "MIT GOOGLE ANMELDEN",
            "QR-Code mit dem Handy scannen. Google-Passwort und 2-Faktor-Code werden ausschließlich bei Google eingegeben – niemals in TOR POS.",
            "TOR POS fordert nur die Berechtigung „E-Mails senden“ (gmail.send). Der QR-Code ist einmalig und läuft nach wenigen Minuten ab.",
            "TOR POS · Mit Google anmelden",
            "Bei Barzahlung automatisch öffnen",
            "Drucker automatisch suchen",
            "DRUCKER-ZENTRALE",
            "TOR liest Windows-Druckername, Treiber und Port. Epson- und Star-Bondrucker werden nur dann mit einem konkreten Modell bezeichnet, wenn der Modellname eindeutig erkannt wird. Bei unklarem Modell bleibt die Auswahl bewusst manuell.",
            "Kassenschubladen-Test: TOR verwendet je nach Drucker das passende Protokoll: Epson ESC/POS oder Star StarPRNT. Falls Ausgang 1 nicht öffnet, Ausgang 2 wählen und erneut testen. Der Test erzeugt keinen Verkauf und keinen Bon; die mechanische Öffnung muss am Gerät kontrolliert werden.",
            "TOR POS · Drucker-Zentrale",
            "ANGEBOT AKTIVIEREN",
            "AUSGEWÄHLTES ANGEBOT DEAKTIVIEREN",
            "AKTUALISIEREN",
            "z. B. DÖNER ANGEBOT",
            "Pfand wird nicht rabattiert. ",
            "TOR POS · ANGEBOTE / AKTIONEN",
            "ERSTEINRICHTUNG",
            "Welche Kassenart möchten Sie verwenden?",
            "Die Auswahl wird für diese Installation gespeichert und kann später nicht versehentlich geändert werden.",
            "AUSWÄHLEN",
            "Ausgegebene Bestellungen anzeigen",
            "Abholnummer / Parknummer suchen",
            "Bestellhinweis (Deutsch)",
            "BESTELLÜBERSICHT",
            "Bestellübersicht",
            "BESTÄTIGEN",
            "Grund auswählen",
            "Optionaler Zusatz / Notiz",
            "Grund ist Pflicht und wird unveränderbar protokolliert.",
            "Bitte einen Grund auswählen.",
            "BEENDEN",
            "TOR POS · 7-TAGE-DEMO",
            "Eine erneute Installation startet auf demselben PC keine neue Demo. ",
            "TOR POS Demo",
            "GEWICHTSARTIKEL",
            "Funktioniert auch ohne angeschlossene Waage: Gewicht ablesen, hier in Gramm oder Kilogramm eingeben und übernehmen.",
            "Gewicht eingeben",
            "TOR POS · Gewicht eingeben",
            "Geprüft · Sperre aufheben (kein automatischer Nachdruck)",
            "Prüfnachweis: Windows-Warteschlange / bereits gedruckte Belege",
            "Eingabefeld auswählen"
        ];
        assert(
            setupWindowVocabulary.All(key => turkishKeys.Contains(key) && englishKeys.Contains(key)),
            "the setup, diagnostics and order windows read in the chosen language as well");

        // A translation can break a layout. Turkish and English words are not
        // German words, and a button sized for KASSIEREN can be cut in half by
        // ÖDEME AL - which the layout gate would never see while it only ever
        // renders German. The snapshot tool takes the language from the stored
        // setting, the way a real till does, and CI measures all three.
        var snapshotTool = File.ReadAllText(FindRepoFile("Desktop/tools/TorPos.UiSnapshot/Program.cs"));
        var workflow = File.ReadAllText(FindRepoFile(".github/workflows/tor-pos-ci.yml"));
        assert(
            snapshotTool.Contains("[\"ui.language\"] = language", StringComparison.Ordinal) &&
            snapshotTool.Contains("UiLanguage.IsSupported(language)", StringComparison.Ordinal) &&
            workflow.Contains("--check --language TR", StringComparison.Ordinal) &&
            workflow.Contains("--check --language EN", StringComparison.Ordinal),
            "the layout gate measures the till in Turkish and English too, so a longer translation cannot quietly cut a button in half");

        // R54 deleted ui.language from app_settings inside InitializeAsync, with no
        // schema guard - it ran at every start and wiped the operator's choice.
        var infrastructure = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        assert(
            !infrastructure.Contains("DELETE FROM app_settings WHERE key='ui.language'", StringComparison.Ordinal) &&
            settings.Contains("Combo(\"ui.language\", \"DE\", \"TR\", \"EN\")", StringComparison.Ordinal),
            "the language can be chosen in the settings and the stored choice is no longer purged at every start");

        return Task.CompletedTask;
    }

    private static IEnumerable<string> AppSources() =>
        Directory.EnumerateFiles(FindRepoDirectory("Desktop/src/TorPos.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<KeyValuePair<string, string>> Pairs(string block) =>
        Regex.Matches(block, "\\[\"((?:[^\"\\\\]|\\\\.)*)\"\\]\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => new KeyValuePair<string, string>(Unescape(m.Groups[1].Value), Unescape(m.Groups[2].Value)));

    // The tables are read as source text, so a key written "a\\nb" arrives here as
    // a backslash followed by an n. Every list in this file is real C# strings
    // with a real newline, and the two would never compare equal - a vocabulary
    // entry with a line break would silently look missing. That is exactly what
    // happened to the three reports/email messages.
    private static string Unescape(string literal) =>
        literal
            .Replace("\\\\", "\u0001", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\u0001", "\\", StringComparison.Ordinal);

    private static HashSet<string> Keys(string block) =>
        Regex.Matches(block, "\\[\"((?:[^\"\\\\]|\\\\.)*)\"\\]")
            .Select(m => Unescape(m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

    private static string Between(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return "";
        from += start.Length;
        if (end is null) return text[from..];
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..to];
    }

    private static string FindRepoDirectory(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException(relative);
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
