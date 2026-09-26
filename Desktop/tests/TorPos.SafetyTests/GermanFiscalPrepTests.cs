using System.IO.Compression;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// German fiscal preparation (Zweites Kassengesetz, Regierungsentwurf vom
// 23.09.2026 - not enacted): TSE-Wechsel journal, notification data model,
// versioned rule sets, digital receipt service layer, QR safety, provider
// readiness, AI boundary and DSFinV-K preflight/packaging. Nothing here may
// loosen FiscalRelease or activate a draft rule.
public static class GermanFiscalPrepTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "german-fiscal-prep");
        Directory.CreateDirectory(dir);

        Rulesets(assert);
        ChangeDetection(assert);
        await Journal(dir, assert);
        Notification(assert);
        await NotificationProvider(assert);
        Readiness(assert);
        ReceiptDelivery(assert);
        QrPayload(assert);
        Ai(assert);
        Preflight(assert);
        await PreflightInExport(dir, assert);
        Package(dir, assert);
        Architecture(assert);
    }

    private static void Rulesets(Action<bool, string> assert)
    {
        assert(
            !GermanFiscalRulesets.Planned2028Enacted &&
            !GermanFiscalRulesets.Planned2028.Enacted &&
            GermanFiscalRulesets.Resolve(new DateOnly(2026, 9, 26)) == GermanFiscalRulesets.Current &&
            GermanFiscalRulesets.Resolve(new DateOnly(2028, 1, 1)) == GermanFiscalRulesets.Current &&
            GermanFiscalRulesets.Resolve(new DateOnly(2031, 6, 1)) == GermanFiscalRulesets.Current,
            "Fiscal prep: the unenacted Planned2028 rule set is never resolved for a real till, not even after 01.01.2028");

        var refused = false;
        try { GermanFiscalRulesets.RequireEnacted(GermanFiscalRulesets.Preview(GermanFiscalRulesetVersion.Planned2028)); }
        catch (InvalidOperationException ex) { refused = ex.Message.Contains("nicht in Kraft", StringComparison.Ordinal); }
        assert(refused && GermanFiscalRulesets.RequireEnacted(GermanFiscalRulesets.Current) == GermanFiscalRulesets.Current,
            "Fiscal prep: a previewed draft rule set is refused wherever a profile would decide fiscal behaviour");

        var current = GermanFiscalRulesets.Current;
        assert(
            current.Enacted && current.TseRequired && current.DsfinvkRequired && current.PaperReceiptRequired &&
            !current.DigitalReceiptRequired && !current.TseChangeNotificationRequired &&
            GermanFiscalRulesets.All.All(p => p.LegalBasis.Length > 0 && p.VerifiedStatus.Length > 0) &&
            GermanFiscalRulesets.Planned2028.VerifiedStatus.Contains("ENTWURF", StringComparison.Ordinal),
            "Fiscal prep: every rule set documents its legal basis and status; the current one keeps paper receipt, TSE and DSFinV-K");
    }

    private static TseIdentity Tse(string serial, string provider = TseProviderCatalog.SwissbitHardware) =>
        new(provider, serial, "BSI-K-TR-0000-2026", "2031-01-01", "E:\\");

    private static void ChangeDetection(Action<bool, string> assert)
    {
        assert(
            !TseChangeDetector.IsChange(Tse("ABCDEF01"), Tse(" abcdef01 ")) &&
            !TseChangeDetector.IsChange(Tse("ABCDEF01"), Tse("ABCDEF01") with { CertificateValidUntil = "2032-01-01", DevicePath = "F:\\" }) &&
            !TseChangeDetector.IsChange(Tse("ABCDEF01"), Tse("")),
            "Fiscal prep: reading the same TSE again or losing the serial is not recorded as a TSE-Wechsel");
        assert(
            TseChangeDetector.IsChange(Tse("ABCDEF01"), Tse("ABCDEF02")) &&
            TseChangeDetector.IsChange(Tse(""), Tse("ABCDEF01")) &&
            TseChangeDetector.IsChange(Tse("ABCDEF01"), Tse("ABCDEF01", TseProviderCatalog.FiskalyDirectCloud)),
            "Fiscal prep: a new serial, a first TSE and a provider change are each a TSE-Wechsel");
        assert(
            TseChangeDetector.KindOf(TseProviderCatalog.SwissbitHardware) == TseKind.Hardware &&
            TseChangeDetector.KindOf(TseProviderCatalog.FiskalyDirectCloud) == TseKind.Cloud &&
            TseChangeDetector.KindOf(TseProviderCatalog.FiskaltrustLocalMiddleware) == TseKind.LocalMiddleware &&
            TseChangeDetector.KindOf("UNBEKANNT") == TseKind.Other &&
            TseChangeDetector.KindOf("") == TseKind.Other,
            "Fiscal prep: the TSE type comes from the provider catalog and an unknown provider is never guessed");
    }

    private static TseChangeRecord Change(string id, TseIdentity previous, TseIdentity next, TseChangeOutcome outcome = TseChangeOutcome.Succeeded) =>
        new(id, DateTimeOffset.Now, previous, next, "Zertifikat läuft ab", "admin", "EAS-1", "KASSE-1", "CLIENT-1", "Test GmbH",
            outcome, outcome == TseChangeOutcome.Failed ? "E1" : "", outcome == TseChangeOutcome.Failed ? "PIN falsch" : "");

    private static async Task Journal(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "journal.db"));
        var journal = new TseChangeJournal(db);

        long salesBefore;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM sales;";
            salesBefore = Convert.ToInt64(await q.ExecuteScalarAsync());
        }

        var change = Change("chg-1", Tse("OLD0001"), Tse("NEW0002"));
        await journal.RecordAsync(change, GermanFiscalRulesets.Current);
        var listed = await journal.ListAsync();
        var history = await journal.NotificationHistoryAsync("chg-1");
        assert(
            listed.Count == 1 && listed[0] == change &&
            listed[0].Previous.SerialNumber == "OLD0001" && listed[0].Next.SerialNumber == "NEW0002" &&
            listed[0].KassenId == "EAS-1" && listed[0].TerminalId == "KASSE-1" && listed[0].Mandant == "Test GmbH" &&
            history.Count == 1 && history[0].Status == TseChangeNotificationStatus.NotRequired,
            "Fiscal prep: a TSE-Wechsel is journaled with old/new TSE, Kasse, terminal, Mandant, actor and reason; under the current law its notification starts as NotRequired");

        var duplicate = false;
        try { await journal.RecordAsync(change, GermanFiscalRulesets.Current); }
        catch (InvalidOperationException) { duplicate = true; }
        assert(duplicate && (await journal.ListAsync()).Count == 1, "Fiscal prep: the same TSE-Wechsel cannot be journaled twice");

        var updateRejected = false;
        var deleteRejected = false;
        await using (var c = db.OpenConnection())
        {
            try
            {
                await using var q = c.CreateCommand();
                q.CommandText = "UPDATE audit_log SET details='{}' WHERE event_type='TSE_WECHSEL';";
                await q.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { updateRejected = true; }
            try
            {
                await using var q = c.CreateCommand();
                q.CommandText = "DELETE FROM audit_log WHERE event_type='TSE_WECHSEL';";
                await q.ExecuteNonQueryAsync();
            }
            catch (SqliteException) { deleteRejected = true; }
        }
        assert(updateRejected && deleteRejected && (await journal.ListAsync()).Single() == change,
            "Fiscal prep: the TSE-Wechsel journal is append-only - UPDATE and DELETE are rejected by the audit log triggers");

        long salesAfter;
        await using (var c = db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT COUNT(*) FROM sales;";
            salesAfter = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        assert(salesAfter == salesBefore, "Fiscal prep: journaling a TSE-Wechsel never writes sales or moves them to the new TSE");

        var invalid = new List<bool>();
        foreach (var broken in new[]
        {
            change with { ChangeId = "x1", Reason = " " },
            change with { ChangeId = "x2", KassenId = "" },
            change with { ChangeId = "x3", Next = Tse("") },
            change with { ChangeId = "x4", Outcome = TseChangeOutcome.Failed, ErrorMessage = "" },
        })
        {
            try { await journal.RecordAsync(broken, GermanFiscalRulesets.Current); invalid.Add(false); }
            catch (ArgumentException) { invalid.Add(true); }
        }
        assert(invalid.All(x => x), "Fiscal prep: a TSE-Wechsel without reason, Kassen-ID, new serial or (when failed) error text is refused");

        var badTransition = false;
        try { await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.Submitted, "admin", submissionReference: "ET-1"); }
        catch (InvalidOperationException) { badTransition = true; }
        var noReference = false;
        await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.Pending, "admin", "Steuerberater prüft");
        await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.ReadyForSubmission, "admin");
        try { await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.Submitted, "admin"); }
        catch (InvalidOperationException) { noReference = true; }
        await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.Submitted, "admin", "manuell über Mein ELSTER", "ET-4711");
        var afterSubmitted = false;
        try { await journal.SetNotificationStatusAsync("chg-1", TseChangeNotificationStatus.Pending, "admin"); }
        catch (InvalidOperationException) { afterSubmitted = true; }
        var unknown = false;
        try { await journal.SetNotificationStatusAsync("fehlt", TseChangeNotificationStatus.Pending, "admin"); }
        catch (InvalidOperationException) { unknown = true; }
        var states = (await journal.NotificationHistoryAsync("chg-1")).Select(x => x.Status).ToArray();
        assert(
            badTransition && noReference && afterSubmitted && unknown &&
            states.SequenceEqual(new[]
            {
                TseChangeNotificationStatus.NotRequired, TseChangeNotificationStatus.Pending,
                TseChangeNotificationStatus.ReadyForSubmission, TseChangeNotificationStatus.Submitted
            }) &&
            (await journal.NotificationHistoryAsync("chg-1"))[^1].SubmissionReference == "ET-4711",
            "Fiscal prep: notification states are an append-only history with checked transitions; Submitted needs the ELSTER reference and is final");

        // The recorder, as the settings screen uses it.
        var identity = new SystemIdentityRepository(db);
        var recorderDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "recorder.db"));
        var recorderJournal = new TseChangeJournal(recorderDb);
        var recorderIdentity = new SystemIdentityRepository(recorderDb);
        var a = new Dictionary<string, string> { ["tse.provider"] = TseProviderCatalog.SwissbitHardware, ["tse.serial"] = "AAA111", ["company.name"] = "Test GmbH", ["tse.client_id"] = "C1" };
        var b = new Dictionary<string, string>(a) { ["tse.serial"] = "BBB222" };
        var same = await TseChangeRecorder.RecordIfChangedAsync(recorderJournal, recorderIdentity, a, a, "Geräteprüfung", "admin", TseChangeOutcome.Succeeded);
        var failed = await TseChangeRecorder.RecordIfChangedAsync(recorderJournal, recorderIdentity, a, b, "TSE-Aktivierung", "admin", TseChangeOutcome.Failed, "E", "PIN falsch");
        // The failed activation already stored BBB222 in the settings; the later success must still be recorded.
        var succeeded = await TseChangeRecorder.RecordIfChangedAsync(recorderJournal, recorderIdentity, b, b, "TSE-Aktivierung", "admin", TseChangeOutcome.Succeeded);
        var again = await TseChangeRecorder.RecordIfChangedAsync(recorderJournal, recorderIdentity, b, b, "Geräteprüfung", "admin", TseChangeOutcome.Succeeded);
        var eas = (await recorderIdentity.GetAsync()).EasSerial;
        assert(
            same is null && again is null &&
            failed is { Outcome: TseChangeOutcome.Failed, ErrorMessage: "PIN falsch" } &&
            succeeded is { Outcome: TseChangeOutcome.Succeeded } &&
            succeeded.Previous.SerialNumber == "AAA111" && succeeded.Next.SerialNumber == "BBB222" &&
            succeeded.KassenId == eas && succeeded.Mandant == "Test GmbH" && succeeded.ClientId == "C1" &&
            (await recorderJournal.ListAsync()).Count == 2 && (await identity.GetAsync()).EasSerial.Length > 0,
            "Fiscal prep: the recorder journals only real changes, keeps failed attempts with their error and still records the later successful activation");

        var json = change.ToJson();
        assert(
            new[] { "pin", "puk", "seed", "apikey", "api_key", "password", "secret" }
                .All(word => !json.Contains(word, StringComparison.OrdinalIgnoreCase)),
            "Fiscal prep: a TSE-Wechsel record has no field for PIN, PUK, credential seed, API key or password");
    }

    private static void Notification(Action<bool, string> assert)
    {
        var planned = GermanFiscalRulesets.Planned2028;
        var enactedPreview = planned with { Enacted = true };
        assert(
            TseChangeNotificationRules.InitialFor(GermanFiscalRulesets.Current, TseChangeOutcome.Succeeded) == TseChangeNotificationStatus.NotRequired &&
            TseChangeNotificationRules.InitialFor(planned, TseChangeOutcome.Succeeded) == TseChangeNotificationStatus.NotRequired &&
            TseChangeNotificationRules.InitialFor(enactedPreview, TseChangeOutcome.Succeeded) == TseChangeNotificationStatus.Pending &&
            TseChangeNotificationRules.InitialFor(enactedPreview, TseChangeOutcome.Failed) == TseChangeNotificationStatus.NotRequired,
            "Fiscal prep: a notification becomes Pending only under an enacted rule set that requires it; a draft never creates a duty");

        var all = Enum.GetValues<TseChangeNotificationStatus>();
        assert(
            all.All(to => !TseChangeNotificationRules.CanTransition(TseChangeNotificationStatus.Submitted, to)) &&
            all.All(to => !TseChangeNotificationRules.CanTransition(TseChangeNotificationStatus.Superseded, to)) &&
            all.All(from => !TseChangeNotificationRules.CanTransition(from, from)) &&
            all.Where(from => from != TseChangeNotificationStatus.ReadyForSubmission)
                .All(from => !TseChangeNotificationRules.CanTransition(from, TseChangeNotificationStatus.Submitted)),
            "Fiscal prep: only a ReadyForSubmission notification can become Submitted; Submitted and Superseded are final");
    }

    private static async Task NotificationProvider(Action<bool, string> assert)
    {
        var provider = new DisabledFiscalNotificationProvider();
        var refused = false;
        try { await provider.SubmitAsync(Change("n1", Tse("A"), Tse("B"))); }
        catch (InvalidOperationException ex) { refused = ex.Message.Contains("nicht freigegeben", StringComparison.Ordinal); }
        var gateRefused = false;
        try { FiscalNotificationRelease.RequireAutomaticSubmission(); }
        catch (InvalidOperationException) { gateRefused = true; }
        assert(
            !FiscalNotificationRelease.AutomaticSubmissionEnabled && refused && gateRefused &&
            !provider.GetReadiness().Usable && provider.GetReadiness().Can(ProviderCapability.FiscalNotification),
            "Fiscal prep: automatic ELSTER/Finanzamt submission stays behind its closed release gate");
    }

    private static void Readiness(Action<bool, string> assert)
    {
        var connected = new ProviderReadiness("FISKALY", ProviderCapability.TseSigning | ProviderCapability.TseExport, true, true, false, ProviderHealth.Healthy);
        var ready = connected with { Validated = true };
        var degraded = ready with { Health = ProviderHealth.Degraded };
        assert(
            !connected.Usable && connected.StatusText.Contains("nicht fiskal freigegeben", StringComparison.Ordinal) &&
            ready.Usable && ready.StatusText == "Bereit" &&
            !degraded.Usable &&
            !(ready with { Reachable = false }).Usable &&
            !(ready with { Configured = false }).Usable &&
            ready.Can(ProviderCapability.TseSigning) && !ready.Can(ProviderCapability.CardPayment) && !ready.Can(ProviderCapability.None),
            "Fiscal prep: configured, reachable, validated and healthy are separate facts - a connected but unvalidated provider is never 'ready'");
    }

    private static void ReceiptDelivery(Action<bool, string> assert)
    {
        var notCompleted = false;
        try { ReceiptDeliveryPolicy.Plan(GermanFiscalRulesets.Current, ReceiptFiscalState.NotCompleted, new[] { ReceiptDeliveryChannel.QrCode }); }
        catch (InvalidOperationException ex) { notCompleted = ex.Message.StartsWith("TSE nicht verfügbar", StringComparison.Ordinal); }
        assert(notCompleted, "Fiscal prep: no receipt channel is offered for a Vorgang that is not fiscally completed - with the German message");

        var digital = new[] { ReceiptDeliveryChannel.Pdf, ReceiptDeliveryChannel.QrCode, ReceiptDeliveryChannel.QrCode, ReceiptDeliveryChannel.Paper };
        var current = ReceiptDeliveryPolicy.Plan(GermanFiscalRulesets.Current, ReceiptFiscalState.Signed, digital);
        var outage = ReceiptDeliveryPolicy.Plan(GermanFiscalRulesets.Current, ReceiptFiscalState.DocumentedOutage, Array.Empty<ReceiptDeliveryChannel>());
        var planned = ReceiptDeliveryPolicy.Plan(GermanFiscalRulesets.Preview(GermanFiscalRulesetVersion.Planned2028), ReceiptFiscalState.Signed, digital);
        var plannedNoDigital = ReceiptDeliveryPolicy.Plan(GermanFiscalRulesets.Preview(GermanFiscalRulesetVersion.Planned2028), ReceiptFiscalState.Signed, Array.Empty<ReceiptDeliveryChannel>());
        assert(
            current.DefaultChannel == ReceiptDeliveryChannel.Paper &&
            current.Offered.SequenceEqual(new[] { ReceiptDeliveryChannel.QrCode, ReceiptDeliveryChannel.Pdf, ReceiptDeliveryChannel.Paper }) &&
            outage.Offered.SequenceEqual(new[] { ReceiptDeliveryChannel.Paper }) &&
            planned.DefaultChannel == ReceiptDeliveryChannel.QrCode && planned.Offered.Contains(ReceiptDeliveryChannel.Paper) &&
            plannedNoDigital.DefaultChannel == ReceiptDeliveryChannel.Paper,
            "Fiscal prep: today paper stays the default; the 2028 preview defaults to QR with paper on request; paper is always offered");

        var qrOk = new ReceiptDeliveryResult(ReceiptDeliveryChannel.QrCode, true, "ok");
        assert(
            ReceiptDeliveryPolicy.MustPrintPaper(ReceiptDeliveryChannel.Paper, null) &&
            ReceiptDeliveryPolicy.MustPrintPaper(ReceiptDeliveryChannel.QrCode, null) &&
            ReceiptDeliveryPolicy.MustPrintPaper(ReceiptDeliveryChannel.QrCode, qrOk with { Delivered = false }) &&
            ReceiptDeliveryPolicy.MustPrintPaper(ReceiptDeliveryChannel.Email, qrOk) &&
            !ReceiptDeliveryPolicy.MustPrintPaper(ReceiptDeliveryChannel.QrCode, qrOk),
            "Fiscal prep: paper is printed whenever the chosen digital receipt was not confirmed - the customer is never left without a receipt");
    }

    private static void QrPayload(Action<bool, string> assert)
    {
        var token = new string('A', 20) + "b-_9" + new string('z', 19);
        var ok = "https://bon.example.de/r/" + token;
        assert(
            QrReceiptPayload.IsSafe(ok) && QrReceiptPayload.Require(ok) == ok &&
            !QrReceiptPayload.IsSafe(ok + "?email=kunde@example.de") &&
            !QrReceiptPayload.IsSafe(ok + "#tse") &&
            !QrReceiptPayload.IsSafe("https://user:pass@bon.example.de/r/" + token) &&
            !QrReceiptPayload.IsSafe("http://bon.example.de/r/" + token) &&
            !QrReceiptPayload.IsSafe("https://bon.example.de/r/" + token + "/x") &&
            !QrReceiptPayload.IsSafe("https://bon.example.de/r/" + token[..42]) &&
            !QrReceiptPayload.IsSafe(" " + ok) &&
            !QrReceiptPayload.IsSafe("TSE;SIG=abc;SERIAL=123") &&
            !QrReceiptPayload.IsSafe(null),
            "Fiscal prep: a receipt QR code carries only an HTTPS link with an opaque 256-bit token - no customer data, credentials, query or fiscal payload");
    }

    private static void Ai(Action<bool, string> assert)
    {
        var forbidden = new[]
        {
            AiOperation.FinalizeTseTransaction, AiOperation.ChangeTseRecord, AiOperation.CompletePayment,
            AiOperation.ModifyPastSale, AiOperation.ModifyFinalInvoice, AiOperation.ModifyDsfinvkRecord,
            AiOperation.DeleteOrModifyAuditLog, AiOperation.ElevateUserPermission
        };
        assert(
            forbidden.All(op => AiSafetyBoundary.Classify(op) == AiOperationClass.Forbidden &&
                                !AiSafetyBoundary.Authorize(op, userAccepted: true, userHasPermission: true, executedByDeterministicService: true).Allowed) &&
            AiSafetyBoundary.Classify((AiOperation)999) == AiOperationClass.Forbidden,
            "Fiscal prep: AI can never finalize TSE, complete payment, change past sales/invoices/DSFinV-K/audit log or raise permissions - not even with confirmation");

        var price = AiOperation.ChangePriceOrTaxSetting;
        assert(
            !AiSafetyBoundary.Authorize(price, false, true, true).Allowed &&
            !AiSafetyBoundary.Authorize(price, true, false, true).Allowed &&
            !AiSafetyBoundary.Authorize(price, true, true, false).Allowed &&
            AiSafetyBoundary.Authorize(price, true, true, true).Allowed &&
            AiSafetyBoundary.Authorize(AiOperation.SuggestPrice, false, false, false).Allowed &&
            Enum.GetValues<AiOperation>().All(op => Enum.IsDefined(AiSafetyBoundary.Classify(op))),
            "Fiscal prep: a price/tax change from AI needs user acceptance, permission and the deterministic service; reading and suggesting need nothing");

        var record = new AiAuditRecord(AiAuditEventTypes.RecommendationCreated, DateTimeOffset.Now, "admin", "Anthropic", "model", AiOperation.SuggestPrice,
            "Umsätze 09/2026 aggregiert", new string('x', 5000) + "\u0000", AiUserDecision.None, "").Validate();
        var unknownType = false;
        try { (record with { EventType = "AiDidSomething" }).Validate(); }
        catch (ArgumentException) { unknownType = true; }
        var noActor = false;
        try { (record with { Actor = "" }).Validate(); }
        catch (ArgumentException) { noActor = true; }
        assert(
            record.Recommendation.Length == AiAuditRecord.MaxTextLength && !record.Recommendation.Contains('\u0000') &&
            unknownType && noActor && AiAuditEventTypes.All.Count == 5 && AiAuditRecord.AuditEntityType == "AI",
            "Fiscal prep: AI audit events have fixed types, need user and model, and bounded clean text for the audit log only");
    }

    private static void Preflight(Action<bool, string> assert)
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
        var whole = DsfinvkExportRange.ForDates(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), berlin);
        var now = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.FromHours(2));
        var clean = DsfinvkPreflightChecks.Range(whole.FromInclusive, whole.ToInclusive, now, berlin);
        var partial = DsfinvkPreflightChecks.Range(whole.FromInclusive.AddHours(6), whole.ToInclusive, now, berlin);
        var future = DsfinvkPreflightChecks.Range(whole.FromInclusive, now.AddDays(3), now, berlin);
        assert(
            clean.Count == 0 &&
            partial.Single().Code == "RANGE_TEILTAG" && !partial.Single().Blocking &&
            future.Any(x => x.Code == "RANGE_ZUKUNFT" && !x.Blocking) &&
            DsfinvkPreflightChecks.Range(now, now.AddDays(-1), now, berlin).Count == 0,
            "Fiscal prep: DSFinV-K preflight warns about partial days and future periods across DST, without blocking whole-day exports");

        var gaps = DsfinvkPreflightChecks.ClosingContinuity(new long[] { 1, 2, 4, 7, 8, 9, 12 }, 9);
        assert(
            DsfinvkPreflightChecks.ClosingContinuity(new long[] { 1, 2, 3 }, 3).Count == 0 &&
            gaps.Single().Code == "Z_LUECKE" && gaps.Single().Message.Contains("Z_NR 3, 5-6)", StringComparison.Ordinal) &&
            DsfinvkPreflightChecks.ClosingContinuity(new long[] { 1, 2, 2 }, 2).Any(x => x.Code == "Z_DOPPELT" && x.Blocking),
            "Fiscal prep: missing Z numbers up to the export's last closing are named, duplicate Z numbers block");

        var one = DsfinvkPreflightChecks.TsePeriods(new[] { "A", "a", "" }, Array.Empty<(string, string)>());
        var undocumented = DsfinvkPreflightChecks.TsePeriods(new[] { "A", "B", "C" }, new[] { ("A", "B") });
        var documented = DsfinvkPreflightChecks.TsePeriods(new[] { "A", "B" }, new[] { ("a", "b") });
        assert(
            one.Count == 0 &&
            undocumented.Any(x => x.Code == "TSE_PERIODEN") &&
            undocumented.Single(x => x.Code == "TSE_WECHSEL_UNDOKUMENTIERT").Message.Contains("B → C", StringComparison.Ordinal) &&
            !undocumented.Single(x => x.Code == "TSE_WECHSEL_UNDOKUMENTIERT").Message.Contains("A → B", StringComparison.Ordinal) &&
            documented.Single().Code == "TSE_PERIODEN" && documented.All(x => !x.Blocking),
            "Fiscal prep: several TSE in one export are listed and each undocumented TSE-Wechsel is named");
    }

    private static async Task PreflightInExport(string dir, Action<bool, string> assert)
    {
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "preflight.db"));
        var export = new DsfinvkExportService(db, new SettingsRepository(db));
        var today = DateOnly.FromDateTime(DateTime.Now);
        var range = DsfinvkExportRange.ForDates(today.AddDays(-7), today.AddDays(-1));
        var whole = await export.ValidateAsync(range.FromInclusive, range.ToInclusive);
        var partial = await export.ValidateAsync(range.FromInclusive.AddHours(3), DateTimeOffset.Now.AddDays(1));
        assert(
            !whole.Ready && whole.Issues.Any(x => x.Code == "NO_CLOSING" && x.Blocking) &&
            whole.Issues.All(x => x.Code is not ("RANGE_TEILTAG" or "RANGE_ZUKUNFT")) &&
            partial.Issues.Any(x => x.Code == "RANGE_TEILTAG") && partial.Issues.Any(x => x.Code == "RANGE_ZUKUNFT"),
            "Fiscal prep: the DSFinV-K export runs the new preflight rules and still refuses a period without Kassenabschluss");
    }

    private static void Package(string dir, Action<bool, string> assert)
    {
        var export = Path.Combine(dir, "DSFinV-K_TEST_20260901-20260930");
        Directory.CreateDirectory(export);
        var bytes = new System.Text.UTF8Encoding(false).GetBytes("\"Z_KASSE_ID\";\"Größe\"\r\n\"1\";\"Döner €\"\r\n");
        File.WriteAllBytes(Path.Combine(export, "transactions.csv"), bytes);
        File.WriteAllText(Path.Combine(export, "index.xml"), "<xml/>");
        var zip = Path.Combine(dir, "package.zip");
        var result = DsfinvkPackage.Create(export, zip);

        byte[] packed;
        using (var archive = ZipFile.OpenRead(zip))
        using (var stream = archive.GetEntry("DSFinV-K_TEST_20260901-20260930/transactions.csv")!.Open())
        using (var copy = new MemoryStream())
        {
            stream.CopyTo(copy);
            packed = copy.ToArray();
        }

        assert(
            result.FileCount == 2 && packed.SequenceEqual(bytes) &&
            result.Sha256 == DsfinvkPackage.FileSha256(zip) &&
            File.ReadAllText(result.ChecksumPath) == $"{result.Sha256}  package.zip\n",
            "Fiscal prep: the DSFinV-K ZIP keeps every export byte unchanged and carries its SHA-256 checksum");

        var tampered = Path.Combine(dir, "tampered.zip");
        using (var archive = ZipFile.Open(tampered, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("DSFinV-K_TEST_20260901-20260930/transactions.csv").Open()))
                w.Write("\"Z_KASSE_ID\";\"Groesse\"\n");
            using (var w = new StreamWriter(archive.CreateEntry("DSFinV-K_TEST_20260901-20260930/index.xml").Open()))
                w.Write("<xml/>");
        }
        var mismatch = false;
        try { DsfinvkPackage.Verify(export, tampered); }
        catch (InvalidOperationException ex) { mismatch = ex.Message.Contains("transactions.csv", StringComparison.Ordinal); }

        var incomplete = Path.Combine(dir, "DSFinV-K_X.unvollstaendig");
        Directory.CreateDirectory(incomplete);
        var incompleteRefused = false;
        try { DsfinvkPackage.Create(incomplete, Path.Combine(dir, "x.zip")); }
        catch (InvalidOperationException) { incompleteRefused = true; }
        assert(mismatch && incompleteRefused && !File.Exists(Path.Combine(dir, "x.zip")),
            "Fiscal prep: a ZIP that differs from the export is refused and an unfinished export is never packed");
    }

    private static void Architecture(Action<bool, string> assert)
    {
        var fiscalCore = new[]
        {
            "Desktop/src/TorPos.Infrastructure/SaleFiscalSigningService.cs",
            "Desktop/src/TorPos.Infrastructure/OrderFiscalSigningService.cs",
            "Desktop/src/TorPos.Infrastructure/CashMovementFiscalSigningService.cs",
            "Desktop/src/TorPos.Infrastructure/TseVorgangService.cs",
            "Desktop/src/TorPos.Infrastructure/CheckoutJournal.cs",
            "Desktop/src/TorPos.Infrastructure/RestaurantFiscalOrderService.cs",
            "Desktop/src/TorPos.Core/DsfinvkClosingBuilder.cs",
            "Desktop/src/TorPos.Core/CheckoutSafety.cs",
        };
        assert(
            fiscalCore.All(path =>
            {
                var source = File.ReadAllText(FindRepoFile(path));
                return !source.Contains("AiOperation", StringComparison.Ordinal) &&
                       !source.Contains("AiAuditRecord", StringComparison.Ordinal) &&
                       !source.Contains("AiSafetyBoundary", StringComparison.Ordinal);
            }),
            "Fiscal prep: the TSE/payment/DSFinV-K core has no AI entry point");

        var prepSources = new[]
        {
            "Desktop/src/TorPos.Core/GermanFiscalRuleset.cs",
            "Desktop/src/TorPos.Core/TseChange.cs",
            "Desktop/src/TorPos.Core/DigitalReceiptDelivery.cs",
            "Desktop/src/TorPos.Core/ProviderReadiness.cs",
            "Desktop/src/TorPos.Core/AiSafetyBoundary.cs",
            "Desktop/src/TorPos.Core/DsfinvkPreflightChecks.cs",
            "Desktop/src/TorPos.Infrastructure/TseChangeJournal.cs",
            "Desktop/src/TorPos.Infrastructure/DsfinvkPackage.cs",
        }.Select(path => File.ReadAllText(FindRepoFile(path))).ToArray();
        assert(
            prepSources.All(source =>
                !source.Contains("business.mode", StringComparison.Ordinal) &&
                !source.Contains("TOR_POS_PRODUCT_EDITION", StringComparison.Ordinal) &&
                !source.Contains("ProductionAllowed = true", StringComparison.Ordinal) &&
                !source.Contains("UPDATE audit_log", StringComparison.Ordinal) &&
                !source.Contains("DELETE FROM", StringComparison.Ordinal)) &&
            File.ReadAllText(FindRepoFile("Desktop/tools/Verify-Fiscal-Release-Gates.ps1")).Contains("AutomaticSubmissionEnabled", StringComparison.Ordinal),
            "Fiscal prep: the shared services are edition-neutral, never open FiscalRelease, never rewrite the audit log, and CI locks the notification gate");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
