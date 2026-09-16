using TorPos.Infrastructure;

// R94: ZVT End-of-Day (06 50) - the terminal's own daily batch settlement,
// asking it to transfer its stored turnover to the acquirer. Portalum.Zvt
// 3.4.0 already exposed ZvtClient.EndOfDayAsync unused; wired it up as
// IPaymentTerminalService.EndOfDayAsync, mirroring RegisterAsync's
// connect/timeout/audit shape exactly (not a payment - no checkout
// journal, no fiscal gate). Settings/audit are kept entirely separate from
// the connection Probe/Register ("payment.terminal.end_of_day.*" keys,
// "PAYMENT_TERMINAL_END_OF_DAY" audit event) so an end-of-day run can
// never be confused with - or silently overwrite - the connection-test
// status shown elsewhere in Einstellungen.
public static class R94ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert,
        Func<Func<Task>, string, Task> reject)
    {
        var dir = Path.Combine(root, "r94-zvt-end-of-day");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r94.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var journal = new CheckoutJournal(db);
        var terminal = new ZvtPaymentTerminalService(settings, audit, journal);

        var disabled = await terminal.EndOfDayAsync();
        assert(
            !disabled.Success && disabled.State == "DEAKTIVIERT",
            "R94 End-of-Day refuses to run while the terminal integration itself is disabled");

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["payment.terminal.enabled"] = "true"
        });

        var missingIp = await terminal.EndOfDayAsync();
        assert(
            !missingIp.Success && missingIp.State == "KONFIGURATION_FEHLER",
            "R94 End-of-Day is refused before any network attempt when the terminal IP is not configured");

        // A validated-but-unreachable config drives EndOfDayAsync into its
        // actual connect attempt, which is where a result gets persisted
        // (matching ProbeAsync/RegisterAsync: early config-validation
        // failures above are never saved, only an actual attempted result is).
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["payment.terminal.ip"] = "127.0.0.1",
            ["payment.terminal.port"] = "1",
            ["payment.terminal.connect_timeout_seconds"] = "1"
        });

        var unreachable = await terminal.EndOfDayAsync();
        assert(
            !unreachable.Success && unreachable.State is "NICHT_ERREICHBAR" or "TIMEOUT",
            "R94 End-of-Day on an unreachable terminal fails cleanly instead of hanging or throwing");

        var values = await settings.LoadAllAsync();
        assert(
            values.GetValueOrDefault("payment.terminal.end_of_day.last_status") == unreachable.State &&
            !string.IsNullOrWhiteSpace(values.GetValueOrDefault("payment.terminal.end_of_day.last_at")),
            "R94 End-of-Day's actual attempt result is durably recorded under its own settings keys");

        assert(
            values.GetValueOrDefault("payment.terminal.last_status") == "NICHT_KONFIGURIERT",
            "R94 End-of-Day never touches the connection-test status key used by VERBINDUNG TESTEN/ZVT ANMELDUNG - it stays at its untouched default, not overwritten by an End-of-Day result");
    }
}
