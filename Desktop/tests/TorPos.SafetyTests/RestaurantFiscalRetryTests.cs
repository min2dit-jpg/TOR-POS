using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

public static class RestaurantFiscalRetryTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var previousEdition =
            Environment.GetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION");

        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var db = await SafetyDatabase.CreateCurrentAsync(
                Path.Combine(
                    root,
                    "restaurant-fiscal-retry-" +
                    Guid.NewGuid().ToString("N") +
                    ".db"));

            var repo = new RestaurantRepository(db);
            var areaId =
                await repo.SaveAreaAsync(
                    "Retry",
                    1);
            var tableId =
                await repo.SaveTableAsync(
                    areaId,
                    "RETRY-1",
                    "Retry Tisch",
                    2,
                    1);
            var session =
                await repo.OpenTableAsync(
                    tableId,
                    "RETRY-TEST",
                    1,
                    "KASSE-RETRY");

            var product = new Product
            {
                Id = 940001,
                Name = "Retry Artikel",
                BasePriceCents = 900,
                VatRate = 7m,
                Unit = "Stück",
                IsActive = true
            };

            var firstItem =
                await repo.AddItemAsync(
                    session.Id,
                    session.Version,
                    product,
                    1m,
                    "RETRY-TEST",
                    "KASSE-RETRY");

            var firstVorgangId =
                "restaurant-retry-journal-" +
                Guid.NewGuid().ToString("N");
            var firstStarted =
                DateTimeOffset.UtcNow.AddSeconds(-2);
            var signed =
                SaleTseResult.SignedResult(
                    "RETRY-CLIENT",
                    "42",
                    "7",
                    "RETRY-SERIAL",
                    "RETRY-SIGNATURE",
                    DateTimeOffset.UtcNow,
                    firstStarted);

            await SeedFinishedVorgangAsync(
                db,
                firstVorgangId,
                firstStarted,
                JsonSerializer.Serialize(signed));

            // Null fail-safe dependencies are intentional: this test proves
            // recovery consumes the durable F-6 journal and does not touch
            // the TSE transport a second time.
            var vorgaenge =
                new TseVorgangService(
                    db,
                    null!,
                    null!);
            var fiscal =
                new RestaurantFiscalOrderService(
                    db,
                    vorgaenge);
            var firstVorgang =
                new RestaurantFiscalVorgang(
                    firstVorgangId,
                    firstStarted);

            await fiscal.SecureAddedItemAsync(
                session.Id,
                firstItem,
                firstVorgang,
                "RETRY-TEST");

            // Replaying the same application command after the DB commit must
            // return immediately because the item is already SECURED.
            await fiscal.SecureAddedItemAsync(
                session.Id,
                firstItem,
                firstVorgang,
                "RETRY-TEST");

            var firstState =
                await ReadItemFiscalStateAsync(
                    db,
                    firstItem.Id);
            var firstRows =
                await CountBestellungenAsync(
                    db,
                    session.Id);
            var firstSignature =
                await ReadLatestSignatureAsync(
                    db,
                    session.Id);
            var firstVorgangState =
                await ReadVorgangConsumptionAsync(
                    db,
                    firstVorgangId);

            assert(
                firstState == "SECURED" &&
                firstRows == 1 &&
                firstSignature.TransactionNumber == "42" &&
                !firstSignature.Outage,
                "Restaurant fiscal retry reuses the journaled TSE Finish exactly once and a replay of an already-SECURED line creates no duplicate Bestellung");

            assert(
                firstVorgangState.Reference ==
                    $"RESTAURANT-COMMITTED:{session.Id}" &&
                firstVorgangState.FinishJournal.Length == 0,
                "Restaurant Bestellung commit atomically consumes the F-6 journal and removes the Vorgang from future recovery candidates");

            var current =
                await repo.GetSessionAsync(session.Id)
                ?? throw new InvalidOperationException(
                    "Retry Restaurant session missing.");

            var secondItem =
                await repo.AddItemAsync(
                    session.Id,
                    current.Version,
                    new Product
                    {
                        Id = 940002,
                        Name = "Retry Ohne Journal",
                        BasePriceCents = 500,
                        VatRate = 19m,
                        Unit = "Stück",
                        IsActive = true
                    },
                    1m,
                    "RETRY-TEST",
                    "KASSE-RETRY");

            var secondVorgangId =
                "restaurant-retry-nojournal-" +
                Guid.NewGuid().ToString("N");
            var secondStarted =
                DateTimeOffset.UtcNow.AddSeconds(-1);

            await SeedFinishedVorgangAsync(
                db,
                secondVorgangId,
                secondStarted,
                "");

            await fiscal.SecureAddedItemAsync(
                session.Id,
                secondItem,
                new RestaurantFiscalVorgang(
                    secondVorgangId,
                    secondStarted),
                "RETRY-TEST");

            var secondState =
                await ReadItemFiscalStateAsync(
                    db,
                    secondItem.Id);
            var secondRows =
                await CountBestellungenAsync(
                    db,
                    session.Id);
            var secondSignature =
                await ReadLatestSignatureAsync(
                    db,
                    session.Id);

            assert(
                secondState == "SECURED" &&
                secondRows == 2 &&
                secondSignature.Outage &&
                secondSignature.OutageReason.Contains(
                    "Keine zweite TSE-Transaktion",
                    StringComparison.Ordinal),
                "Restaurant fiscal recovery fails closed when a terminal Vorgang has no finish journal and never creates a second TSE transaction");

            var beforeCancel =
                await repo.GetSessionAsync(session.Id)
                ?? throw new InvalidOperationException(
                    "Retry Restaurant session missing before cancellation.");
            await repo.CancelItemAsync(
                session.Id,
                beforeCancel.Version,
                firstItem.Id,
                "RETRY-TEST",
                "KASSE-RETRY");

            var cancelRecoveryId =
                "restaurant-retry-cancel-" +
                Guid.NewGuid().ToString("N");
            var cancelStarted =
                DateTimeOffset.UtcNow.AddMilliseconds(-500);
            var cancelSigned =
                SaleTseResult.SignedResult(
                    "RETRY-CLIENT",
                    "43",
                    "8",
                    "RETRY-SERIAL",
                    "RETRY-CANCEL-SIGNATURE",
                    DateTimeOffset.UtcNow,
                    cancelStarted);

            await SeedFinishedVorgangAsync(
                db,
                cancelRecoveryId,
                cancelStarted,
                JsonSerializer.Serialize(cancelSigned),
                $"RESTAURANT:{session.Id}");

            var cancelReconciled =
                await fiscal.ReconcileSessionAsync(
                    session.Id,
                    "RETRY-TEST");

            var afterCancelRows =
                await CountBestellungenAsync(
                    db,
                    session.Id);
            var afterCancelSignature =
                await ReadLatestSignatureAsync(
                    db,
                    session.Id);

            assert(
                cancelReconciled &&
                await fiscal.IsCurrentStateSecuredAsync(session.Id) &&
                afterCancelRows == 3 &&
                afterCancelSignature.TransactionNumber == "43",
                "Restaurant cancellation reconciliation reuses the journaled TSE result and restores a payable secured table without a second TSE");

            var mergeArea =
                await repo.SaveAreaAsync(
                    "Merge Retry",
                    2);
            var sourceTable =
                await repo.SaveTableAsync(
                    mergeArea,
                    "MR-S",
                    "Merge Quelle",
                    2,
                    1);
            var targetTable =
                await repo.SaveTableAsync(
                    mergeArea,
                    "MR-T",
                    "Merge Ziel",
                    2,
                    2);
            var source =
                await repo.OpenTableAsync(
                    sourceTable,
                    "RETRY-TEST");
            var target =
                await repo.OpenTableAsync(
                    targetTable,
                    "RETRY-TEST");

            var sourceItem =
                await repo.AddItemAsync(
                    source.Id,
                    source.Version,
                    new Product
                    {
                        Id = 940010,
                        Name = "Merge Quelle Artikel",
                        BasePriceCents = 700,
                        VatRate = 7m,
                        Unit = "Stück",
                        IsActive = true
                    },
                    1m,
                    "RETRY-TEST");
            var sourceAfterAdd =
                await repo.GetSessionAsync(source.Id)
                ?? throw new InvalidOperationException("Merge source missing.");
            var targetItem =
                await repo.AddItemAsync(
                    target.Id,
                    target.Version,
                    new Product
                    {
                        Id = 940011,
                        Name = "Merge Ziel Artikel",
                        BasePriceCents = 600,
                        VatRate = 19m,
                        Unit = "Stück",
                        IsActive = true
                    },
                    1m,
                    "RETRY-TEST");
            var targetAfterAdd =
                await repo.GetSessionAsync(target.Id)
                ?? throw new InvalidOperationException("Merge target missing.");

            async Task SecureSeededAsync(
                RestaurantTableSession owner,
                RestaurantSessionItem item,
                string tx)
            {
                var id =
                    "restaurant-retry-seed-" +
                    Guid.NewGuid().ToString("N");
                var started =
                    DateTimeOffset.UtcNow.AddSeconds(-2);
                var result =
                    SaleTseResult.SignedResult(
                        "RETRY-CLIENT",
                        tx,
                        tx,
                        "RETRY-SERIAL",
                        "SEED-" + tx,
                        DateTimeOffset.UtcNow,
                        started);
                await SeedFinishedVorgangAsync(
                    db,
                    id,
                    started,
                    JsonSerializer.Serialize(result),
                    $"RESTAURANT:{owner.Id}");
                await fiscal.SecureAddedItemAsync(
                    owner.Id,
                    item,
                    new RestaurantFiscalVorgang(id, started),
                    "RETRY-TEST");
            }

            await SecureSeededAsync(
                sourceAfterAdd,
                sourceItem,
                "50");
            await SecureSeededAsync(
                targetAfterAdd,
                targetItem,
                "51");

            sourceAfterAdd =
                await repo.GetSessionAsync(source.Id)
                ?? throw new InvalidOperationException("Merge source missing after secure.");
            targetAfterAdd =
                await repo.GetSessionAsync(target.Id)
                ?? throw new InvalidOperationException("Merge target missing after secure.");

            await repo.MergeSessionsAsync(
                source.Id,
                sourceAfterAdd.Version,
                target.Id,
                targetAfterAdd.Version,
                "RETRY-TEST",
                "KASSE-RETRY");

            var sourceRecoveryId =
                "restaurant-retry-merge-source-" +
                Guid.NewGuid().ToString("N");
            var targetRecoveryId =
                "restaurant-retry-merge-target-" +
                Guid.NewGuid().ToString("N");
            var mergeStarted =
                DateTimeOffset.UtcNow.AddMilliseconds(-250);

            await SeedFinishedVorgangAsync(
                db,
                sourceRecoveryId,
                mergeStarted,
                JsonSerializer.Serialize(
                    SaleTseResult.SignedResult(
                        "RETRY-CLIENT",
                        "52",
                        "52",
                        "RETRY-SERIAL",
                        "MERGE-SOURCE",
                        DateTimeOffset.UtcNow,
                        mergeStarted)),
                $"RESTAURANT:{source.Id}");
            await SeedFinishedVorgangAsync(
                db,
                targetRecoveryId,
                mergeStarted,
                JsonSerializer.Serialize(
                    SaleTseResult.SignedResult(
                        "RETRY-CLIENT",
                        "53",
                        "53",
                        "RETRY-SERIAL",
                        "MERGE-TARGET",
                        DateTimeOffset.UtcNow,
                        mergeStarted)),
                $"RESTAURANT:{target.Id}");

            var sourceRecovered =
                await fiscal.ReconcileSessionAsync(
                    source.Id,
                    "RETRY-TEST");
            var targetRecovered =
                await fiscal.ReconcileSessionAsync(
                    target.Id,
                    "RETRY-TEST");

            assert(
                sourceRecovered &&
                targetRecovered &&
                await fiscal.IsCurrentStateSecuredAsync(source.Id) &&
                await fiscal.IsCurrentStateSecuredAsync(target.Id),
                "Restaurant merge reconciliation repairs both DB-committed sessions from journaled TSE results without duplicate TSE transactions");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                previousEdition,
                EnvironmentVariableTarget.Process);
        }
    }

    private static async Task SeedFinishedVorgangAsync(
        SqliteDatabase db,
        string id,
        DateTimeOffset startedAt,
        string finishJson,
        string reference = "RESTAURANT-RETRY")
    {
        var transactionNumber = "42";
        if (!string.IsNullOrWhiteSpace(finishJson))
        {
            try
            {
                transactionNumber =
                    JsonSerializer.Deserialize<SaleTseResult>(finishJson)
                        ?.TransactionNumber
                    ?? transactionNumber;
            }
            catch (JsonException)
            {
            }
        }

        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            INSERT INTO tse_vorgaenge(
                id,training,started_at,
                client_id,transaction_number,start_log_time,start_error,
                state,parked_receipt_id,reference,updated_at,
                finish_attempted_at,finish_result_json)
            VALUES(
                $id,0,$started,
                'RETRY-CLIENT',$transaction,$started,'',
                'FINISHED',NULL,$reference,$now,
                $now,$journal);
            """;
        q.Parameters.AddWithValue(
            "$id",
            id);
        q.Parameters.AddWithValue(
            "$started",
            startedAt.ToString("O"));
        q.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));
        q.Parameters.AddWithValue(
            "$journal",
            finishJson);
        q.Parameters.AddWithValue(
            "$transaction",
            transactionNumber);
        q.Parameters.AddWithValue(
            "$reference",
            reference);
        await q.ExecuteNonQueryAsync();
    }

    private static async Task<(string Reference, string FinishJournal)>
        ReadVorgangConsumptionAsync(
            SqliteDatabase db,
            string vorgangId)
    {
        await using var c = db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT reference,finish_result_json
            FROM tse_vorgaenge
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", vorgangId);
        await using var r = await q.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            throw new InvalidOperationException("Restaurant retry Vorgang missing.");
        return (r.GetString(0), r.GetString(1));
    }

    private static async Task<string> ReadItemFiscalStateAsync(
        SqliteDatabase db,
        long itemId)
    {
        await using var c = db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT fiscal_state FROM restaurant_session_items WHERE id=$id;";
        q.Parameters.AddWithValue(
            "$id",
            itemId);
        return Convert.ToString(
                   await q.ExecuteScalarAsync())
               ?? "";
    }

    private static async Task<long> CountBestellungenAsync(
        SqliteDatabase db,
        string sessionId)
    {
        await using var c = db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText =
            "SELECT COUNT(*) FROM restaurant_bestellungen WHERE session_id=$session;";
        q.Parameters.AddWithValue(
            "$session",
            sessionId);
        return Convert.ToInt64(
            await q.ExecuteScalarAsync());
    }

    private static async Task<(
        string TransactionNumber,
        bool Outage,
        string OutageReason)> ReadLatestSignatureAsync(
            SqliteDatabase db,
            string sessionId)
    {
        await using var c = db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT transaction_number,outage,outage_reason
            FROM restaurant_bestellungen
            WHERE session_id=$session
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        q.Parameters.AddWithValue(
            "$session",
            sessionId);

        await using var r =
            await q.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            throw new InvalidOperationException(
                "Restaurant retry Bestellung missing.");
        }

        return (
            r.GetString(0),
            r.GetInt64(1) == 1,
            r.GetString(2));
    }
}
