using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R-5.1: Kellnerabrechnung - read-only per-waiter summary of the open period.
public static class RestaurantWaiterSettlementTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        var dir = Path.Combine(root, "restaurant-waiter-settlement");
        Directory.CreateDirectory(dir);
        try
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT", EnvironmentVariableTarget.Process);
            var db = new SqliteDatabase(Path.Combine(dir, "r51.db"));
            await new SchemaMigrationService(db, new DatabaseBackupService(db), Path.Combine(dir, "backups"))
                .InitializeDatabaseAsync();

            var repo = new RestaurantRepository(db);
            var area = await repo.SaveAreaAsync("Innen");
            var t1 = await repo.SaveTableAsync(area, "T1", "Tisch 1", seats: 4);
            var t2 = await repo.SaveTableAsync(area, "T2", "Tisch 2", seats: 4);
            var annaTable = await repo.OpenTableAsync(t1, "Anna", guestCount: 2, deviceId: "KASSE-1");
            var benTable = await repo.OpenTableAsync(t2, "Ben", guestCount: 2, deviceId: "KASSE-1");
            await repo.AddItemAsync(annaTable.Id, annaTable.Version, new Product { Id = 51, Name = "Schnitzel", BasePriceCents = 1290, VatRate = 7m }, 1m, "Anna", "KASSE-1");
            await repo.AddItemAsync(benTable.Id, benTable.Version, new Product { Id = 52, Name = "Bier", BasePriceCents = 450, VatRate = 19m }, 2m, "Ben", "KASSE-1");

            var localToday = DateTime.Today;
            var localOffset = TimeZoneInfo.Local.GetUtcOffset(localToday);
            var now = new DateTimeOffset(
                localToday.AddMinutes(20),
                localOffset);
            await using (var c = db.OpenConnection())
            {
                async Task SaleAsync(string op, string type, string method, long total, long cash, long card)
                {
                    await using var q = c.CreateCommand();
                    q.CommandText = """
                        INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,transaction_type,cash_portion_cents,card_portion_cents)
                        VALUES((SELECT COALESCE(MAX(receipt_number),0)+1 FROM sales),$at,$m,$t,$t,$type,$cash,$card);
                        INSERT INTO sale_operators(sale_id,operator_name) VALUES(last_insert_rowid(),$op);
                        """;
                    q.Parameters.AddWithValue("$at", now.AddMinutes(-5).ToString("O"));
                    q.Parameters.AddWithValue("$m", method);
                    q.Parameters.AddWithValue("$t", total);
                    q.Parameters.AddWithValue("$type", type);
                    q.Parameters.AddWithValue("$cash", cash);
                    q.Parameters.AddWithValue("$card", card);
                    q.Parameters.AddWithValue("$op", op);
                    await q.ExecuteNonQueryAsync();
                }
                await SaleAsync("Anna", "SALE", "CASH", 1000, 1000, 0);
                await SaleAsync("Anna", "SALE", "MIXED", 1000, 300, 700);
                await SaleAsync("Anna", "STORNO", "CASH", 1000, 1000, 0);
                await SaleAsync("Ben", "SALE", "CARD", 500, 0, 500);

                await using var ev = c.CreateCommand();
                ev.CommandText = """
                    INSERT INTO restaurant_session_events(session_id,event_type,actor,device_id,created_at,payload_json)
                    VALUES($s,'POSITION_STORNIERT','Anna','KASSE-1',$at,'{"ProductName":"Wasser","QuantityMilli":1000,"LineTotalCents":300}');
                    """;
                ev.Parameters.AddWithValue("$s", annaTable.Id);
                ev.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                await ev.ExecuteNonQueryAsync();
            }

            async Task<string> FingerprintAsync()
            {
                await using var c = db.OpenConnection();
                await using var q = c.CreateCommand();
                q.CommandText = """
                    SELECT (SELECT COUNT(*) FROM sales)||'/'||(SELECT COUNT(*) FROM audit_log)||'/'||
                           (SELECT COUNT(*) FROM restaurant_session_events)||'/'||(SELECT group_concat(version) FROM restaurant_sessions)||'/'||
                           (SELECT COUNT(*) FROM app_settings);
                    """;
                return (string)(await q.ExecuteScalarAsync())!;
            }
            var before = await FingerprintAsync();
            var service = new RestaurantWaiterSettlementService(db);
            var settlement = await service.BuildForOpenPeriodAsync();
            var after = await FingerprintAsync();

            var anna = settlement.Rows.Single(x => x.Waiter == "Anna");
            var ben = settlement.Rows.Single(x => x.Waiter == "Ben");
            assert(
                anna.SaleCount == 2 && anna.SalesCents == 2000 && anna.StornoReturnCents == 1000 &&
                anna.CashCents == 300 && anna.CardCents == 700 && anna.NetCents == anna.CashCents + anna.CardCents &&
                ben.SaleCount == 1 && ben.CardCents == 500 && ben.CashCents == 0,
                "R-5.1 cashed Bons per waiter follow the X report rules: mixed split, Storno taken off its payment type");
            assert(
                anna.OpenTables == 1 && anna.OpenTablesCents == 1290 &&
                ben.OpenTables == 1 && ben.OpenTablesCents == 900 &&
                anna.CancelledPositions == 1 && anna.CancelledPositionsCents == 300 && ben.CancelledPositions == 0,
                "R-5.1 open tables and cancelled table positions are counted per waiter");
            assert(
                before == after && settlement.TotalCashCents == 300 && settlement.TotalCardCents == 1200 &&
                RestaurantWaiterSettlementService.ToText(settlement).Any(x => x.Contains("Offene Tische: 1", StringComparison.Ordinal)),
                "R-5.1 building the Kellnerabrechnung writes nothing (sales, audit, events, table versions, settings unchanged)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", oldEdition, EnvironmentVariableTarget.Process);
        }
    }
}
