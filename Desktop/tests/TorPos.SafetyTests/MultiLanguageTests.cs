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
        foreach (var file in Directory.EnumerateFiles(FindRepoDirectory("Desktop/src/TorPos.App"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
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

    private static IEnumerable<KeyValuePair<string, string>> Pairs(string block) =>
        Regex.Matches(block, "\\[\"((?:[^\"\\\\]|\\\\.)*)\"\\]\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")
            .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value));

    private static HashSet<string> Keys(string block) =>
        Regex.Matches(block, "\\[\"((?:[^\"\\\\]|\\\\.)*)\"\\]")
            .Select(m => m.Groups[1].Value)
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
