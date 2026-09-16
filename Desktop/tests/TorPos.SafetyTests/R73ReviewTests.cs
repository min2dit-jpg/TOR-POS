using TorPos.Core;
using TorPos.Infrastructure;

public static class R73ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var migrationDir =
            Path.Combine(root,"r73-migration");

        Directory.CreateDirectory(
            migrationDir);

        var migrationDb =
            new SqliteDatabase(
                Path.Combine(
                    migrationDir,
                    "r73.db"));

        var migrator =
            new SchemaMigrationService(
                migrationDb,
                new DatabaseBackupService(
                    migrationDb),
                Path.Combine(
                    migrationDir,
                    "migration-backups"));

        var migration =
            await migrator.InitializeDatabaseAsync();

        assert(
            migration.ToVersion >= 5 &&
            SchemaMigrationService.TargetSchemaVersion >= 5,
            "R73 schema V5 installs separate terminal outcome and reconciliation metadata");

        using (var c=migrationDb.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT
                    COUNT(*)
                FROM pragma_table_info('checkout_operations')
                WHERE name IN (
                    'terminal_outcome',
                    'terminal_code',
                    'terminal_message',
                    'terminal_submitted',
                    'resolution',
                    'resolution_actor',
                    'resolution_at');
                """;

            assert(
                Convert.ToInt32(
                    q.ExecuteScalar())==7,
                "R73 checkout journal persists all terminal outcome and resolution fields");
        }

        assert(
            PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(
                "Error",
                "Payment DECLINED by host",
                "") ==
            PaymentTerminalOutcome.Declined,
            "R73 explicit completed decline is classified as DECLINED");

        assert(
            PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(
                "Error",
                "",
                "Vorgang vom Benutzer abgebrochen") ==
            PaymentTerminalOutcome.Cancelled,
            "R73 explicit completed cancellation is classified as CANCELLED");

        assert(
            PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(
                "Error",
                "Unknown failure",
                "") ==
            PaymentTerminalOutcome.Unknown,
            "R73 generic terminal failure remains UNKNOWN instead of assuming no charge");

        assert(
            PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(
                "Error",
                "Cancellation not possible after host response",
                "") ==
            PaymentTerminalOutcome.Unknown,
            "R73 cancellation-related error text is not misclassified without explicit completed cancellation");

        var submittedDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-submitted.db"));

        var submittedJournal =
            new CheckoutJournal(
                submittedDb);

        var submitted =
            Snapshot(
                "r73-submitted");

        await submittedJournal.BeginAsync(
            submitted);

        await submittedJournal.MarkTerminalSubmittedAsync(
            submitted.OperationId,
            "test submit");

        var submittedState =
            await submittedJournal.GetAsync(
                submitted.OperationId);

        assert(
            submittedState is not null &&
            submittedState.State=="SENT" &&
            submittedState.TerminalRequestSubmitted &&
            submittedState.TerminalOutcome==PaymentTerminalOutcome.None,
            "R73 SENT marker durably records that a payment request may already have reached the terminal");

        await reject(
            () => submittedJournal.MarkTerminalSubmittedAsync(
                submitted.OperationId,
                "duplicate"),
            "R73 same checkout cannot be submitted to terminal twice");

        await submittedJournal.TransitionTerminalAsync(
            submitted.OperationId,
            "SENT",
            "UNKNOWN",
            "lost terminal response",
            PaymentTerminalOutcome.Unknown,
            requestSubmitted:true,
            terminalCode:"TIMEOUT",
            terminalMessage:"response missing");

        var unknown =
            await submittedJournal.GetAsync(
                submitted.OperationId);

        assert(
            unknown is not null &&
            unknown.State=="UNKNOWN" &&
            unknown.TerminalOutcome==PaymentTerminalOutcome.Unknown &&
            unknown.TerminalRequestSubmitted &&
            unknown.Resolution==CheckoutResolution.None,
            "R73 true post-submit uncertainty remains open UNKNOWN with no invented resolution");

        await reject(
            () => submittedJournal.BeginAsync(
                Snapshot(
                    "r73-blocked-second")),
            "R73 UNKNOWN checkout still blocks a second payment operation");

        await submittedJournal.ResolveAsync(
            submitted.OperationId,
            "UNKNOWN",
            paid:true,
            actor:"admin-test",
            evidence:"Terminalbeleg 4711 bestätigt");

        var manualPaid =
            await submittedJournal.GetAsync(
                submitted.OperationId);

        assert(
            manualPaid is not null &&
            manualPaid.State=="APPROVED" &&
            manualPaid.TerminalOutcome==PaymentTerminalOutcome.Unknown &&
            manualPaid.Resolution==CheckoutResolution.ManualPaid &&
            manualPaid.ResolutionActor=="admin-test",
            "R73 manual paid reconciliation preserves original UNKNOWN terminal outcome");

        await reject(
            () => submittedJournal.ResolveAsync(
                submitted.OperationId,
                "APPROVED",
                paid:false,
                actor:"admin-test",
                evidence:"versuchter downgrade"),
            "R73 APPROVED payment cannot be reconciled backwards to NOT_CHARGED");

        var declinedDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-declined.db"));

        var declinedJournal =
            new CheckoutJournal(
                declinedDb);

        var declined =
            Snapshot(
                "r73-declined");

        await declinedJournal.BeginAsync(
            declined);

        await declinedJournal.MarkTerminalSubmittedAsync(
            declined.OperationId,
            "test submit");

        await declinedJournal.TransitionTerminalAsync(
            declined.OperationId,
            "SENT",
            "NOT_CHARGED",
            "host decline",
            PaymentTerminalOutcome.Declined,
            requestSubmitted:true,
            terminalCode:"DECLINED",
            terminalMessage:"explicit decline");

        var declinedState =
            await declinedJournal.GetAsync(
                declined.OperationId);

        assert(
            declinedState is not null &&
            declinedState.State=="NOT_CHARGED" &&
            declinedState.TerminalOutcome==PaymentTerminalOutcome.Declined &&
            declinedState.Resolution==CheckoutResolution.AutoNotCharged &&
            (await declinedJournal.GetOpenAsync()).Count==0,
            "R73 explicit DECLINED closes safely as NOT_CHARGED and releases the checkout lock");

        await declinedJournal.BeginAsync(
            Snapshot(
                "r73-after-decline"));

        assert(
            (await declinedJournal.GetOpenAsync()).Single().Snapshot.OperationId=="r73-after-decline",
            "R73 a new payment may start after an explicit completed decline");

        var cancelledDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-cancelled.db"));

        var cancelledJournal =
            new CheckoutJournal(
                cancelledDb);

        var cancelled =
            Snapshot(
                "r73-cancelled");

        await cancelledJournal.BeginAsync(
            cancelled);

        await cancelledJournal.MarkTerminalSubmittedAsync(
            cancelled.OperationId,
            "test submit");

        await cancelledJournal.TransitionTerminalAsync(
            cancelled.OperationId,
            "SENT",
            "NOT_CHARGED",
            "user cancelled",
            PaymentTerminalOutcome.Cancelled,
            requestSubmitted:true,
            terminalCode:"ABORTED",
            terminalMessage:"user cancellation");

        var cancelledState =
            await cancelledJournal.GetAsync(
                cancelled.OperationId);

        assert(
            cancelledState is not null &&
            cancelledState.State=="NOT_CHARGED" &&
            cancelledState.TerminalOutcome==PaymentTerminalOutcome.Cancelled &&
            cancelledState.Resolution==CheckoutResolution.AutoNotCharged,
            "R73 explicit CANCELLED is kept separate from DECLINED while both safely mean no charge");

        var notSentDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-notsent.db"));

        var notSentJournal =
            new CheckoutJournal(
                notSentDb);

        var notSent =
            Snapshot(
                "r73-notsent");

        await notSentJournal.BeginAsync(
            notSent);

        await reject(
            () => notSentJournal.ResolveAsync(
                notSent.OperationId,
                "PREPARED",
                paid:true,
                actor:"admin-test",
                evidence:"manuelle Zahlungsbehauptung"),
            "R73 PREPARED card checkout cannot be manually promoted to paid before any submitted marker");

        await notSentJournal.TransitionTerminalAsync(
            notSent.OperationId,
            "PREPARED",
            "NOT_CHARGED",
            "connect failed before payment command",
            PaymentTerminalOutcome.NotSent,
            requestSubmitted:false,
            terminalCode:"CONNECT_TIMEOUT",
            terminalMessage:"not connected");

        var notSentState =
            await notSentJournal.GetAsync(
                notSent.OperationId);

        assert(
            notSentState is not null &&
            notSentState.State=="NOT_CHARGED" &&
            notSentState.TerminalOutcome==PaymentTerminalOutcome.NotSent &&
            !notSentState.TerminalRequestSubmitted &&
            notSentState.Resolution==CheckoutResolution.AutoNotCharged,
            "R73 NOT_SENT proves no payment command was admitted and closes without UNKNOWN");

        await reject(
            () => notSentJournal.TransitionTerminalAsync(
                notSent.OperationId,
                "NOT_CHARGED",
                "UNKNOWN",
                "invalid mapping",
                PaymentTerminalOutcome.Declined,
                requestSubmitted:true,
                terminalCode:"DECLINED",
                terminalMessage:"invalid"),
            "R73 invalid terminal-outcome/state combinations are rejected");

        var manualNoChargeDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-manual-nocharge.db"));

        var manualNoChargeJournal =
            new CheckoutJournal(
                manualNoChargeDb);

        var manualNoCharge =
            Snapshot(
                "r73-manual-nocharge");

        await manualNoChargeJournal.BeginAsync(
            manualNoCharge);

        await manualNoChargeJournal.MarkTerminalSubmittedAsync(
            manualNoCharge.OperationId,
            "test submit");

        await manualNoChargeJournal.TransitionTerminalAsync(
            manualNoCharge.OperationId,
            "SENT",
            "UNKNOWN",
            "ambiguous",
            PaymentTerminalOutcome.Unknown,
            requestSubmitted:true,
            terminalCode:"TIMEOUT",
            terminalMessage:"ambiguous");

        await manualNoChargeJournal.ResolveAsync(
            manualNoCharge.OperationId,
            "UNKNOWN",
            paid:false,
            actor:"admin-test",
            evidence:"Netzbetreiber bestätigt keine Belastung");

        var resolvedNoCharge =
            await manualNoChargeJournal.GetAsync(
                manualNoCharge.OperationId);

        assert(
            resolvedNoCharge is not null &&
            resolvedNoCharge.State=="NOT_CHARGED" &&
            resolvedNoCharge.TerminalOutcome==PaymentTerminalOutcome.Unknown &&
            resolvedNoCharge.Resolution==CheckoutResolution.ManualNotCharged &&
            (await manualNoChargeJournal.GetOpenAsync()).Count==0,
            "R73 manual no-charge reconciliation closes UNKNOWN without rewriting original terminal outcome");

        var approvedDb =
            await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "r73-approved.db"));

        var approvedJournal =
            new CheckoutJournal(
                approvedDb);

        var approved =
            Snapshot(
                "r73-approved");

        await approvedJournal.BeginAsync(
            approved);

        await approvedJournal.MarkTerminalSubmittedAsync(
            approved.OperationId,
            "test submit");

        await approvedJournal.TransitionTerminalAsync(
            approved.OperationId,
            "SENT",
            "APPROVED",
            "terminal approved",
            PaymentTerminalOutcome.Approved,
            requestSubmitted:true,
            terminalCode:"Successful",
            terminalMessage:"approved");

        var approvedState =
            await approvedJournal.GetAsync(
                approved.OperationId);

        assert(
            approvedState is not null &&
            approvedState.State=="APPROVED" &&
            approvedState.TerminalOutcome==PaymentTerminalOutcome.Approved &&
            approvedState.Resolution==CheckoutResolution.AutoApproved,
            "R73 automatic terminal approval stores APPROVED outcome separately from checkout state");

        using (var c=approvedDb.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT COUNT(*)
                FROM audit_log
                WHERE entity_type='CHECKOUT'
                  AND entity_id=$id
                  AND event_type IN (
                    'CHECKOUT_TERMINAL_SUBMITTED',
                    'CHECKOUT_TERMINAL_OUTCOME');
                """;

            q.Parameters.AddWithValue(
                "$id",
                approved.OperationId);

            assert(
                Convert.ToInt32(
                    q.ExecuteScalar())==2,
                "R73 terminal submission and outcome changes are durably auditable");
        }
    }

    private static CheckoutSnapshot Snapshot(
        string operationId) =>
        new(
            operationId,
            new[]
            {
                new CartLine
                {
                    ProductId=1,
                    ProductName="R73 TEST",
                    Quantity=1,
                    UnitPriceCents=1000,
                    ListUnitPriceCents=1000,
                    VatRate=19
                }
            },
            0,
            PaymentMethod.Card,
            "tester",
            null);
}
