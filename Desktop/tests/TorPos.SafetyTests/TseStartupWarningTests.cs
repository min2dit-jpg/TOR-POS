using System.Text.RegularExpressions;

// The till told nobody when the TSE was missing.
//
// AutoProbeTseAsync has run at every start since R113 and already opens a
// documented outage through TseFailSafeService, so the fiscal side was right.
// What it did with the answer was not: only Ready and Connected wrote a status
// line. NotFound - no TSE plugged in, the single most likely case on a new
// installation - fell through the if/else and produced nothing at all. The
// cashier saw an ordinary till and sold all day; the outage existed only in a
// log nobody opens.
//
// These checks lock the behaviour a POS is expected to have: say it once, at
// the start, in the operator's language, without stopping the sale.
public static class TseStartupWarningTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        var safety = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.Safety.cs"));
        var translations = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/UiTranslations.cs"));

        var probe = Block(main, "private async Task AutoProbeTseAsync()", "private async void OnSettingsClick");

        // Every state that is not Ready reaches the operator. The switch is
        // written over the enum with a default arm, so a state added to
        // TseConnectionState later cannot fall through in silence the way
        // NotFound did.
        var states = new[]
        {
            "TseConnectionState.Connected",
            "TseConnectionState.NotFound",
            "TseConnectionState.SdkMissing",
            "TseConnectionState.NotConfigured"
        };
        assert(
            probe.Length > 0 &&
            states.All(state => probe.Contains(state, StringComparison.Ordinal)) &&
            Regex.IsMatch(probe, @"_ => \(\s*\r?\n\s*""TSE meldet einen Fehler""") &&
            Regex.Matches(probe, @"await WarnAboutTseOnceAsync\(").Count == 2,
            "TSE start-up warning: every TSE state that cannot sign is reported to the operator, including the timeout and any state added later");

        // A working TSE must never produce the warning. Ready leaves the method
        // before the warning is reached.
        var ready = probe.IndexOf("result.State == TseConnectionState.Ready", StringComparison.Ordinal);
        var firstWarningAfterReady = probe.IndexOf("await WarnAboutTseOnceAsync(", ready < 0 ? 0 : ready, StringComparison.Ordinal);
        var earlyReturn = ready >= 0 && firstWarningAfterReady > ready &&
            probe[ready..firstWarningAfterReady].Contains("return;", StringComparison.Ordinal);
        assert(
            ready >= 0 && earlyReturn,
            "TSE start-up warning: a TSE that is ready writes its serial number and never warns");

        // The probe runs again every time the settings window closes. Without a
        // guard an operator who opens the settings four times gets the same
        // start-up warning four times.
        var guard = Block(main, "private async Task WarnAboutTseOnceAsync", "private async Task AutoProbeTseAsync()");
        assert(
            main.Contains("private bool _tseStartupWarningShown;", StringComparison.Ordinal) &&
            guard.Contains("if (_tseStartupWarningShown || _currentUser.IsTraining) return;", StringComparison.Ordinal) &&
            guard.Contains("_tseStartupWarningShown = true;", StringComparison.Ordinal),
            "TSE start-up warning: the start-up warning is shown once per program run and not in training mode");

        // § 146a: a missing TSE is a documented outage, not a stop. The dialog
        // has a way out that is not "fix the TSE", and nothing in it returns a
        // value the caller could use to block selling.
        var dialog = Block(safety, "private async Task ShowTseUnavailableAsync", "private async Task ShowPrinterIssueAsync");
        assert(
            dialog.Contains("\"WEITER OHNE TSE\"", StringComparison.Ordinal) &&
            dialog.Contains("Die Kasse bleibt bedienbar und der Ausfall wird dokumentiert", StringComparison.Ordinal) &&
            !dialog.Contains("_saleAllowed", StringComparison.Ordinal) &&
            !dialog.Contains("Environment.Exit", StringComparison.Ordinal),
            "TSE start-up warning: the warning informs and does not lock the till, which would turn a documented outage into a stopped shop");

        // Only somebody who may change the settings is offered the button that
        // opens them; a cashier is told whom to tell instead.
        assert(
            Regex.IsMatch(dialog, @"Content = ""TSE-EINSTELLUNGEN \u00d6FFNEN"",\s*\r?\n\s*MinHeight = 48,\s*\r?\n\s*MinWidth = 220,\s*\r?\n\s*IsVisible = _currentUser\.IsAdmin") &&
            dialog.Contains("Bitte die Betreiberin oder den Betreiber informieren.", StringComparison.Ordinal) &&
            dialog.Contains("await OpenSettingsPageAsync(\"Erweitert / Techniker \U0001F512\")", StringComparison.Ordinal),
            "TSE start-up warning: the route into the TSE settings is offered to an admin and a cashier is told who to inform");

        // The worst moment to fall back to German is the one where the operator
        // is being told something is wrong. Every sentence this dialog and the
        // status lines around it can show is in both tables.
        var german = new[]
        {
            "TSE-Pr\u00fcfung beim Start",
            "TSE-EINSTELLUNGEN \u00d6FFNEN",
            "WEITER OHNE TSE",
            "TSE antwortet nicht",
            "TSE erkannt, aber noch nicht betriebsbereit",
            "Keine TSE gefunden",
            "Swissbit SDK nicht gefunden",
            "TSE ist noch nicht eingerichtet",
            "TSE meldet einen Fehler",
            "Keine TSE gefunden \u00b7 Kasse bleibt bedienbar \u00b7 Vorg\u00e4nge werden nicht signiert",
            "Swissbit SDK nicht gefunden \u00b7 Kasse bleibt bedienbar \u00b7 Vorg\u00e4nge werden nicht signiert",
            "TSE ist noch nicht eingerichtet \u00b7 Kasse bleibt bedienbar \u00b7 Vorg\u00e4nge werden nicht signiert",
            "TSE meldet einen Fehler \u00b7 Kasse bleibt bedienbar \u00b7 Vorg\u00e4nge werden nicht signiert",
            "Die TSE hat innerhalb von 10 Sekunden nicht geantwortet.",
            "Bis eine betriebsbereite TSE erkannt wird, wird kein Vorgang signiert. Die Kasse bleibt bedienbar und der Ausfall wird dokumentiert; die Vorg\u00e4nge sind dann aber nicht fiskal abgesichert.",
            "Pr\u00fcfen: steckt die TSE im USB-Anschluss, wird sie im Explorer als Laufwerk angezeigt, ist der Techniker-Bereich eingerichtet?",
            "Bitte die Betreiberin oder den Betreiber informieren. Der Verkauf kann weiterlaufen.",
            "Swissbit SDK ist geladen, aber keine unterst\u00fctzte Hardware-TSE wurde gefunden."
        };
        var turkish = Between(translations, "Turkish = new(StringComparer.Ordinal)", "private static readonly Dictionary<string, string> English");
        var english = Between(translations, "English = new(StringComparer.Ordinal)", null);
        assert(
            dialog.Contains("UiLanguage.Apply(window);", StringComparison.Ordinal) &&
            german.All(key =>
                turkish.Contains($"[\"{key}\"]", StringComparison.Ordinal) &&
                english.Contains($"[\"{key}\"]", StringComparison.Ordinal)),
            "TSE start-up warning: the TSE warning and its status lines are translated, so the one message that matters does not fall back to German");

        // TSE, Swissbit and SDK are the names of the device and its driver. A
        // technician on the phone and the Swissbit documentation use those
        // words; translating them would make the warning unanswerable.
        assert(
            german
                .Where(key => key.Contains("TSE", StringComparison.Ordinal))
                .All(key =>
                    Pair(turkish, key).Contains("TSE", StringComparison.Ordinal) &&
                    Pair(english, key).Contains("TSE", StringComparison.Ordinal)),
            "TSE start-up warning: the device name TSE survives translation in every language, so the operator can repeat it to a technician");

        // Plugging the TSE in while the till is running used to do nothing: the
        // probe only ran at start-up and after the settings window closed, so
        // the answer on screen stayed stale until a restart. The stick is a USB
        // volume, which is watchable without the SDK.
        var watch = Block(main, "private void StartTseWatch()", "private static string MountSignature()");
        assert(
            main.Contains("private DispatcherTimer? _tseWatch;", StringComparison.Ordinal) &&
            main.Contains("StartTseWatch();", StringComparison.Ordinal) &&
            main.Contains("_tseWatch?.Stop();", StringComparison.Ordinal) &&
            main.Contains("SwissbitDeviceScan.FindMountPoints()", StringComparison.Ordinal) &&
            watch.Contains("_settingsCache.GetBool(\"tse.auto_connect\", true)", StringComparison.Ordinal) &&
            watch.Contains("await AutoProbeTseAsync();", StringComparison.Ordinal),
            "TSE start-up warning: the till notices a TSE plugged in or pulled out while it runs, under the same setting, and stops watching when it closes");

        // TseFailSafeService closes the outage on a good probe, but the red
        // badge is only repainted when something asks. Without this the till
        // kept showing an outage that had ended - at the exact moment the
        // operator plugged a working TSE in.
        assert(
            Regex.Matches(probe, @"await RefreshTseOutageBadgeAsync\(\);").Count == 2,
            "TSE start-up warning: the outage badge is repainted after every probe, so a working TSE clears it and a failing one raises it");

        return Task.CompletedTask;
    }

    private static string Pair(string block, string key)
    {
        var match = Regex.Match(
            block,
            "\\[\"" + Regex.Escape(key) + "\"\\]\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return match.Success ? match.Groups[1].Value : "";
    }

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
