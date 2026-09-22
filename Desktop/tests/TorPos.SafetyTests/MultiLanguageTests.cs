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
