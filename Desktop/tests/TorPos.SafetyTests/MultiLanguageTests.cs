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
        // never reach the screen. Every window renders itself, except these six,
        // and each of them has a reason.
        //
        //   TextReportWindow, ZArchiveWindow  - they show a Z-Bericht or X-Bericht
        //                                       verbatim; that is the fiscal record.
        //   CustomerDisplayWindow,            - they face the customer, who is
        //   OrderCustomerDisplayWindow          served in German.
        //   StartupLoadingWindow,             - they run before the stored language
        //   StartupErrorWindow                  has been read.
        string[] germanOnlyWindows =
        [
            "TextReportWindow", "ZArchiveWindow",
            "CustomerDisplayWindow", "OrderCustomerDisplayWindow",
            "StartupLoadingWindow", "StartupErrorWindow"
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
                var rest = source[declaration.Index..];
                var shown = Regex.Match(rest, $"\\b{Regex.Escape(name)}\\.(ShowDialog|Show)\\s*[<(]");
                var built = shown.Success ? rest[..shown.Index] : rest;
                if (!built.Contains("UiLanguage.Apply", StringComparison.Ordinal))
                    unrenderedDialogs.Add($"{Path.GetFileName(file)}:{name}");
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
