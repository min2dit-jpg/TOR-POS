using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R78: TSE-Beleg-Signatur für direkte Kassenverkäufe (KIOSK / IMBISS SALE).
// Läuft NACH dem durablen Sale-Commit; ein TSE-Ausfall darf den bereits
// abgeschlossenen Verkauf nie verändern oder rückgängig machen.
public static class R78ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r78-tse-signing");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r78.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('sale_tse_signatures') WHERE name IN " +
                "('sale_id','client_id','transaction_number','signature_counter','serial_number','signature','log_time','outage');";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 8,
                "R78 schema migration V8 creates the sale_tse_signatures table with all fields");
        }

        var sampleSale = new Sale
        {
            ReceiptNumber = 1,
            CreatedAt = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.FromHours(1)),
            PaymentMethod = PaymentMethod.Cash,
            TotalCents = 1999,
            Lines = new[]
            {
                new CartLine { ProductName = "A", Quantity = 1, UnitPriceCents = 1699, VatRate = 19m },
                new CartLine { ProductName = "B", Quantity = 1, UnitPriceCents = 300, VatRate = 7m },
            }
        };
        var processData = System.Text.Encoding.UTF8.GetString(FiscalProcessData.BuildKassenbeleg(sampleSale));
        // R130: the first draft checked for tags ("UStNormal", "UStErmaessigt")
        // that DSFinV-K Anhang I does not have. The official Kassenbeleg-V1 form
        // is Vorgangstyp ^ five gross tax containers ^ payments.
        assert(processData == "Beleg^16.99_3.00_0.00_0.00_0.00^19.99:Bar",
            $"R78/R130 ProcessData is Kassenbeleg-V1 per DSFinV-K Anhang I - Beleg, 19 % and 7 % gross in their containers, cash payment (actual: {processData})");
        assert(FiscalProcessData.BuildKassenbeleg(sampleSale).SequenceEqual(FiscalProcessData.BuildKassenbeleg(sampleSale)),
            "R78 ProcessData is deterministic for the same sale");

        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var sales = new SaleRepository(db);
        var journal = new CheckoutJournal(db);
        var outages = new TseOutageRepository(db, audit);
        var provider = new FakeTseProvider();
        var failSafe = new TseFailSafeService(provider, outages, audit);
        var signing = new SaleFiscalSigningService(failSafe, settings, sales);

        // R78 SafetyTests never flip TorPos.Core.FiscalRelease.Enabled - that is a
        // deliberate, independent build-level circuit breaker on real fiscal
        // bookings, separate from FiscalComplianceService, and it stays false here
        // exactly like every other SafetyTests fixture. Instead this mirrors the
        // established core-test pattern (see Program.cs's "Retry returns original
        // sale" fixture): manually insert the sale row and mark the checkout
        // operation COMMITTED, so ISaleRepository.CommitAsync takes its normal
        // idempotent-retry path and simply loads/returns the already-committed
        // sale - the exact same code path a real retried commit would use.
        var saleReceiptCounter = 80000L;
        async Task<Sale> CommitRealSaleAsync()
        {
            var snapshot = new CheckoutSnapshot(
                Guid.NewGuid().ToString("N"),
                new[] { new CartLine { ProductName = "R78 Artikel", Quantity = 1, UnitPriceCents = 1000, VatRate = 19m } },
                0,
                PaymentMethod.Cash,
                "tester",
                null);
            await journal.BeginAsync(snapshot);

            var receiptNumber = ++saleReceiptCounter;
            long saleId;
            await using (var c = db.OpenConnection())
            {
                await using var tx = await c.BeginTransactionAsync();
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status) VALUES($r,$now,'CASH',1000,1000,'TEST_FIXTURE'); SELECT last_insert_rowid();";
                    q.Parameters.AddWithValue("$r", receiptNumber);
                    q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
                    saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
                }
                await using (var q = c.CreateCommand())
                {
                    q.Transaction = (SqliteTransaction)tx;
                    q.CommandText = "UPDATE checkout_operations SET state='COMMITTED',sale_id=$sale WHERE id=$id;";
                    q.Parameters.AddWithValue("$sale", saleId);
                    q.Parameters.AddWithValue("$id", snapshot.OperationId);
                    await q.ExecuteNonQueryAsync();
                }
                await tx.CommitAsync();
            }

            return await sales.CommitAsync(snapshot);
        }

        // A) TSE not configured/active. R113 CORRECTION: these two assertions
        // originally required TseOutage to stay FALSE, which is precisely the
        // silent-unsigned-sale bug - the sale looked identical to a signed one
        // and the receipt printed no TSE-AUSFALL note. Fabricating no
        // signature was always correct; being silent about it was not.
        var unsignedSale = await CommitRealSaleAsync();
        await signing.SignAsync(unsignedSale, "tester");
        assert(unsignedSale.TseTransactionNumber == "" && unsignedSale.TseOutage,
            "R78/R113 signing without an active TSE fabricates no signature AND marks the sale as a TSE outage");
        var reloadedUnsigned = await sales.GetByIdAsync(unsignedSale.Id);
        assert(reloadedUnsigned!.TseTransactionNumber == "" && reloadedUnsigned.TseOutage,
            "R78/R113 the documented outage is persisted to the database, not just set on the in-memory sale");

        await reject(
            async () =>
            {
                await using var c = db.OpenConnection();
                await using var q = c.CreateCommand();
                q.CommandText = "UPDATE sales SET fiscal_status='TAMPERED' WHERE id=$id;";
                q.Parameters.AddWithValue("$id", unsignedSale.Id);
                await q.ExecuteNonQueryAsync();
            },
            "R78 the pre-existing sales immutability trigger still forbids UPDATE - this is exactly why TSE signatures live in their own append-only table");

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        // B) Successful sign: transaction number/counter/serial/signature all persist and round-trip.
        var signedSale = await CommitRealSaleAsync();
        await signing.SignAsync(signedSale, "tester");
        assert(signedSale.TseTransactionNumber == "42" && signedSale.TseSignatureCounter == "2" &&
               signedSale.TseSerialNumber == "FAKE-SERIAL" && !signedSale.TseOutage,
            "R78 a successful TSE Start+Finish stamps transaction number, final signature counter and serial on the sale");
        var reloadedSigned = await sales.GetByIdAsync(signedSale.Id);
        assert(reloadedSigned!.TseTransactionNumber == "42" && reloadedSigned.TseSignature == signedSale.TseSignature && !reloadedSigned.TseOutage,
            "R78 the TSE signature round-trips through the database, not just the in-memory sale");
        assert(await outages.GetOpenAsync() is null,
            "R78 a successful signature leaves no open TSE outage");

        // C) TSE fails at Start: the already-committed sale is never touched beyond its TSE fields.
        provider.FailStart = true;
        var startFailSale = await CommitRealSaleAsync();
        var totalBefore = startFailSale.TotalCents;
        var fiscalStatusBefore = startFailSale.FiscalStatus;
        await signing.SignAsync(startFailSale, "tester");
        assert(startFailSale.TseOutage && startFailSale.TseTransactionNumber == "",
            "R78 a failed TSE Start marks TSE-Ausfall without inventing a transaction number");
        assert(startFailSale.TotalCents == totalBefore && startFailSale.FiscalStatus == fiscalStatusBefore,
            "R78 a TSE Start failure never alters the sale's own committed amount or fiscal status");
        assert(await outages.GetOpenAsync() is not null,
            "R78 a failed TSE Start opens a durable TSE outage for later reconciliation");
        provider.FailStart = false;
        await outages.CloseOpenAsync("tester");

        // D) TSE succeeds at Start but fails at Finish: still TSE-Ausfall, sale still untouched.
        provider.FailFinish = true;
        var finishFailSale = await CommitRealSaleAsync();
        await signing.SignAsync(finishFailSale, "tester");
        assert(finishFailSale.TseOutage && finishFailSale.TseTransactionNumber == "",
            "R78 a failed TSE Finish also marks TSE-Ausfall rather than a half-signed receipt");
        assert(provider.StartCalls > 0 && provider.FinishCalls > 0,
            "R78 Finish is only attempted after Start actually succeeded");
        provider.FailFinish = false;
        await outages.CloseOpenAsync("tester");

        // E) No configured client ID. R113 CORRECTION: signing is still
        // correctly skipped, but the outage is recorded rather than silent.
        await settings.SaveManyAsync(new Dictionary<string, string> { ["tse.client_id"] = "" });
        var noClientSale = await CommitRealSaleAsync();
        await signing.SignAsync(noClientSale, "tester");
        assert(noClientSale.TseTransactionNumber == "" && noClientSale.TseOutage,
            "R78/R113 an AKTIV TSE with no configured client ID does not sign, and documents the outage instead of failing silently");
        assert(await outages.GetOpenAsync() is not null,
            "R113 that missing-client-ID outage is durable in tse_outage_log, so it can be reconciled later");
    }

    private sealed class FakeTseProvider : ITseProvider
    {
        public bool FailStart;
        public bool FailFinish;
        public int StartCalls;
        public int FinishCalls;

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
            StartCalls++;
            if (FailStart)
                return Task.FromResult(new TseTransactionResult(false, "Fake TSE Start failed"));
            return Task.FromResult(new TseTransactionResult(true, "OK", 42, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c2ln"));
        }

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("R78 signing only uses Start+Finish, never Update.");

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default)
        {
            FinishCalls++;
            if (FailFinish)
                return Task.FromResult(new TseTransactionResult(false, "Fake TSE Finish failed"));
            return Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c2lnMg=="));
        }

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(true, "OK", targetPath));
    }
}
