using TorPos.Core;
using TorPos.Infrastructure;

// R83: IMBISS ORDER acceptance gets its own "Bestellung-V1" TSE Vorgang,
// separate from the "Kassenbeleg-V1" signed later at payment (R78). Unlike
// R79/R82's fiscal bookings, parking an order has no FiscalRelease.
// RequireProduction() gate (accepting an order isn't itself an irreversible
// Produktivbuchung the way a sale/storno/retoure commit is) - so
// OrderFiscalSigningService's Start/Finish/outage paths can be exercised
// directly here, the same way R78 exercised SaleFiscalSigningService.
public static class R83ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r83-order-tse-signing");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r83.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('parked_receipts') WHERE name IN " +
                "('tse_client_id','tse_transaction_number','tse_signature_counter','tse_serial_number','tse_signature','tse_log_time','tse_outage');";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 7,
                "R83 schema migration V11 adds all seven TSE fiscal columns to parked_receipts");
        }

        var sampleOrder = new ParkedReceipt
        {
            Id = 1,
            ParkNumber = 777,
            PickupNumber = 12,
            CreatedAt = new DateTimeOffset(2026, 3, 1, 11, 0, 0, TimeSpan.FromHours(1)),
            TotalCents = 1250,
            Lines = new[] { new CartLine { ProductName = "Döner", Quantity = 1, UnitPriceCents = 1250, VatRate = 19m } }
        };
        var processData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildBestellung(sampleOrder));
        assert(
            processData.StartsWith("Bestellung^") && processData.Contains("12.50") && processData.Contains("Park-Nr:777"),
            "R83 the Bestellung ProcessData carries its own marker, the order total and the Park-Nr");
        assert(
            !processData.Contains("Beleg-Nr") && !processData.Contains("AVBelegstorno"),
            "R83 an order acceptance's ProcessData is never mistaken for a Kassenbeleg or a Storno");

        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var orders = new ParkedReceiptRepository(db);
        var outages = new TseOutageRepository(db, audit);
        var provider = new FakeTseProvider();
        var failSafe = new TseFailSafeService(provider, outages, audit);
        var signing = new OrderFiscalSigningService(failSafe, settings, orders);

        async Task<ParkedReceipt> AcceptOrderAsync()
        {
            var lines = new[] { new CartLine { ProductName = "R83 Döner", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m } };
            return await orders.ParkAsync(lines, 0, "tester", assignPickupNumber: true, orderPrint: false);
        }

        // A) TSE not configured/active. R113 CORRECTION: the original R83
        // assertions here required TseOutage to stay FALSE - i.e. this test
        // encoded the silent-return bug as intended behavior, which is part of
        // why it survived so long. Not fabricating a signature was always
        // right; staying silent about it was not.
        var unsignedOrder = await AcceptOrderAsync();
        await signing.SignAsync(unsignedOrder, "tester");
        assert(unsignedOrder.TseTransactionNumber == "" && unsignedOrder.TseOutage,
            "R83/R113 signing an order without an active TSE fabricates no signature AND marks TSE-Ausfall");
        var reloadedUnsigned = await orders.GetOpenByIdAsync(unsignedOrder.Id);
        assert(reloadedUnsigned!.TseTransactionNumber == "" && reloadedUnsigned.TseOutage,
            "R83/R113 that documented outage is persisted, not just held in the in-memory order");
        assert(await outages.GetOpenAsync() is not null,
            "R113 an inactive TSE at signing time opens a durable outage record");
        await outages.CloseOpenAsync("tester");

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        // B) Successful sign: fields persist and round-trip through the database.
        var signedOrder = await AcceptOrderAsync();
        await signing.SignAsync(signedOrder, "tester");
        assert(signedOrder.TseTransactionNumber == "42" && signedOrder.TseSignatureCounter == "2" &&
               signedOrder.TseSerialNumber == "FAKE-SERIAL" && !signedOrder.TseOutage,
            "R83 a successful Bestellung Start+Finish stamps transaction number, signature counter and serial on the order");
        var reloadedSigned = await orders.GetOpenByIdAsync(signedOrder.Id);
        assert(reloadedSigned!.TseTransactionNumber == "42" && reloadedSigned.TseSignature == signedOrder.TseSignature && !reloadedSigned.TseOutage,
            "R83 the order's TSE signature round-trips through the database, not just the in-memory object");
        assert(await outages.GetOpenAsync() is null,
            "R83 a successful order signature leaves no open TSE outage");

        // C) TSE fails at Start: the order stays accepted, only TSE-Ausfall is recorded.
        provider.FailStart = true;
        var startFailOrder = await AcceptOrderAsync();
        var pickupBefore = startFailOrder.PickupNumber;
        await signing.SignAsync(startFailOrder, "tester");
        assert(startFailOrder.TseOutage && startFailOrder.TseTransactionNumber == "",
            "R83 a failed TSE Start on order acceptance marks TSE-Ausfall without inventing a transaction number");
        assert(startFailOrder.PickupNumber == pickupBefore,
            "R83 a TSE Start failure never alters the order's own pickup number or other accepted data");
        assert(await outages.GetOpenAsync() is not null,
            "R83 a failed TSE Start on order acceptance opens a durable TSE outage");
        provider.FailStart = false;
        await outages.CloseOpenAsync("tester");

        // D) No configured client ID. R113 CORRECTION: same as (A) - the
        // signing attempt is still correctly skipped, but it is now recorded.
        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.client_id"] = "" });
        var noClientOrder = await AcceptOrderAsync();
        await signing.SignAsync(noClientOrder, "tester");
        assert(noClientOrder.TseTransactionNumber == "" && noClientOrder.TseOutage,
            "R83/R113 an AKTIV TSE with no configured client ID does not sign, and records the outage instead of failing silently");
        assert(await outages.GetOpenAsync() is not null,
            "R113 a missing TSE client ID opens a durable outage record");
    }

    private sealed class FakeTseProvider : ITseProvider
    {
        public bool FailStart;
        public bool FailFinish;

        public string ProviderId => "FAKE";
        public string DisplayName => "Fake TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");

        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new TseProbeResult(TseConnectionState.Ready, "OK"));

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default)
        {
            if (FailStart)
                return Task.FromResult(new TseTransactionResult(false, "Fake TSE Start failed"));
            return Task.FromResult(new TseTransactionResult(true, "OK", 42, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c2ln"));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("R83 signing only uses Start+Finish, never Update.");

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            if (FailFinish)
                return Task.FromResult(new TseTransactionResult(false, "Fake TSE Finish failed"));
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c2lnMg=="));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(true, "OK", targetPath));
    }
}
