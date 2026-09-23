using System.Text.RegularExpressions;

// The outage record existed but nobody could read it.
//
// tse_outage_log has carried every TSE failure since the beginning - start,
// end, reason - and a trigger forbids deleting a row. It also travels into the
// DSFinV-K export. What it never had was a way to look at it: asked "when was
// the TSE down and why", the operator had to produce an export or open the
// database with a tool. For a § 146a check that is the first question.
//
// These checks lock the list that answers it, and the one thing that must not
// happen to it: the recorded reason being rewritten by the language layer.
public static class TseOutageHistoryTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var core = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/FiscalCompliance.cs"));
        var repository = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/FiscalComplianceServices.cs"));
        var schema = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        var translations = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UiTranslations.cs"));

        assert(
            core.Contains("Task<IReadOnlyList<TseOutage>> ListRecentAsync(", StringComparison.Ordinal) &&
            schema.Contains("CREATE TRIGGER IF NOT EXISTS trg_tse_outage_no_delete", StringComparison.Ordinal) &&
            schema.Contains("RAISE(ABORT,'TSE outage history cannot be deleted')", StringComparison.Ordinal),
            "TSE outage history: the outage log can be read back and still cannot be deleted");

        // A history screen that runs an unbounded SELECT over a table nobody
        // prunes is a till that freezes after a bad year.
        var list = Block(repository, "public async Task<IReadOnlyList<TseOutage>> ListRecentAsync", "public sealed class CashMovementRepository");
        assert(
            list.Contains("if (limit > 500) limit = 500;", StringComparison.Ordinal) &&
            list.Contains("ORDER BY id DESC", StringComparison.Ordinal) &&
            list.Contains("LIMIT $limit", StringComparison.Ordinal) &&
            !Regex.IsMatch(WithoutComments(list), @"\b(INSERT|UPDATE|DELETE)\b"),
            "TSE outage history: the list is bounded, newest first, and reads without writing");

        // The whole point of the boundary: UiLanguage translates TextBlock.Text
        // but never TextBox.Text. The reason recorded at the time of a fiscal
        // outage is evidence, not interface text.
        var loader = Block(settings, "private async Task LoadTseOutagesAsync", "private Control ReadOnlyRow");
        assert(
            settings.Contains("private async Task LoadTseOutagesAsync(TextBox target)", StringComparison.Ordinal) &&
            !settings.Contains("LoadTseOutagesAsync(TextBlock", StringComparison.Ordinal) &&
            loader.Contains("+ outage.Reason", StringComparison.Ordinal) &&
            !loader.Contains("UiLanguage.T(outage.Reason)", StringComparison.Ordinal),
            "TSE outage history: the recorded reason reaches the screen exactly as the till wrote it, in every interface language");

        var section = Block(settings, "var outages = Section(\"TSE-Ausfälle\")", "var identity = Section(");
        assert(
            section.Contains("IsReadOnly = true", StringComparison.Ordinal) &&
            section.Contains("AcceptsReturn = true", StringComparison.Ordinal) &&
            section.Contains("\"AUSFALLLISTE AKTUALISIEREN\"", StringComparison.Ordinal) &&
            section.Contains("_ = LoadTseOutagesAsync(outageList);", StringComparison.Ordinal),
            "TSE outage history: the technician page shows the list on opening and can refresh it, and nobody can edit it there");

        var german = new[]
        {
            "TSE-Ausfälle",
            "Jeder Ausfall wird mit Beginn, Ende und Grund protokolliert und kann nicht gelöscht werden. Dieselben Daten gehen in den DSFinV-K-Export.",
            "AUSFALLLISTE AKTUALISIEREN",
            "Ausfallliste wird gelesen ...",
            "Kein TSE-Ausfall protokolliert.",
            "Ausfallliste konnte nicht gelesen werden.",
            "läuft noch",
            "Tag",
            "Tage",
            "Std.",
            "Min."
        };
        var turkish = Between(translations, "Turkish = new(StringComparer.Ordinal)", "private static readonly Dictionary<string, string> English");
        var english = Between(translations, "English = new(StringComparer.Ordinal)", null);
        assert(
            german.All(key =>
                turkish.Contains($"[\"{key}\"]", StringComparison.Ordinal) &&
                english.Contains($"[\"{key}\"]", StringComparison.Ordinal)),
            "TSE outage history: the words around the record are translated, so a Turkish or English till reads the same evidence");

        return Task.CompletedTask;
    }

    // The write-keyword check looks at code, not prose: a comment explaining
    // that the table is protected against DELETE is not a DELETE.
    private static string WithoutComments(string source) =>
        string.Join(
            "\n",
            source
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Block(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return "";
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? source[from..] : source[from..to];
    }

    private static string Between(string text, string start, string? end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return "";
        from += start.Length;
        if (end is null) return text[from..];
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..to];
    }

    private static string FindRepoFile(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
