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

            assert(
                firstState == "SECURED" &&
                firstRows == 1 &&
                firstSignature.TransactionNumber == "42" &&
                !firstSignature.Outage,
                "Restaurant fiscal retry reuses the journaled TSE Finish exactly once and a replay of an already-SECURED line creates no duplicate Bestellung");

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
        string finishJson)
    {
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
                'RETRY-CLIENT','42',$started,'',
                'FINISHED',NULL,'RESTAURANT-RETRY',$now,
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
        await q.ExecuteNonQueryAsync();
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
