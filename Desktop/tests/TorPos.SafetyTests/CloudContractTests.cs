using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// TOR Cloud accepted z.closed and cash.movement events for a long time, but the
// till never sent them: the portal's Z archive stayed empty and Einlagen/
// Entnahmen were invisible. The till now queues both in the same transaction
// as the local booking, and the payloads are pinned to the fixtures the Cloud
// tests use (Cloud/tests/fixtures) - the same contract pattern as R149.
public static class CloudContractTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "cloud-contract");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "cloud.db"));
        var outbox = new TorCloudOutbox(db);
        await outbox.SaveConfigurationAsync(new TorCloudConfiguration("https://api.torpos.de/", "KASSE-1", "protected", true));

        // ---------- z.closed ----------
        var z = new TorCloudZClosing(
            7,
            new DateTimeOffset(2026, 9, 26, 22, 5, 0, TimeSpan.FromHours(2)),
            new DateTimeOffset(2026, 9, 25, 22, 10, 0, TimeSpan.FromHours(2)),
            42, 123450, 80000, 43450, 130000, 2000, 1550, 1800, 1200,
            new[] { new TorCloudZVat(7m, 50000, 3500, 53500), new TorCloudZVat(19m, 58782, 11168, 69950) },
            "PRODUCTION_ALLOWED",
            "chef");
        await using (var c = db.OpenConnection())
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync())
        {
            TorCloudOutbox.EnqueueZClosed(c, tx, z);
            await tx.CommitAsync();
        }

        var sentZ = await PayloadAsync(db, "z-7");
        var expectedZ = Fixture("z-closed-kasse.json");
        assert(expectedZ is not null && sentZ is not null && JsonNode.DeepEquals(JsonNode.Parse(sentZ), expectedZ),
            $"Cloud contract: the z.closed event the till queues is exactly Cloud/tests/fixtures/z-closed-kasse.json (sent: {sentZ})");

        // A real Kassenabschluss queues it in the same transaction.
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var archived = await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var closing = await PayloadAsync(db, $"z-{archived.ZNumber}");
        var closingNode = closing is null ? null : JsonNode.Parse(closing);
        assert(
            closingNode is not null &&
            (string?)closingNode["z_number"] == archived.ZNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
            (long?)closingNode["gross_cents"] == archived.GrossCents &&
            (string?)closingNode["fiscal_status"] == "TEST" &&
            closingNode["vat"] is JsonArray,
            "Cloud contract: a Kassenabschluss queues its z.closed event in the same transaction, keyed by the Z number");

        // ---------- cash.movement ----------
        var deposit = new CashMovement(12, DateTimeOffset.Now, CashMovementKind.Einlage, 5000, "Wechselgeld", "chef", CashMovement.ProductionMode, CashBusinessCase.Geldtransit);
        await using (var c = db.OpenConnection())
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync())
        {
            TorCloudOutbox.EnqueueCashMovement(c, tx, deposit);
            TorCloudOutbox.EnqueueCashMovement(c, tx, deposit with { Id = 13, FiscalMode = CashMovement.TestMode });
            TorCloudOutbox.EnqueueCashMovement(c, tx, deposit with { Id = 14, Kind = CashMovementKind.CashCount, BusinessCase = null });
            await tx.CommitAsync();
        }

        var sentCash = await PayloadAsync(db, "cash-12");
        var expectedCash = Fixture("cash-movement-kasse.json");
        assert(expectedCash is not null && sentCash is not null && JsonNode.DeepEquals(JsonNode.Parse(sentCash), expectedCash),
            $"Cloud contract: a real Einlage is queued exactly as Cloud/tests/fixtures/cash-movement-kasse.json (sent: {sentCash})");
        assert(await PayloadAsync(db, "cash-13") is null && await PayloadAsync(db, "cash-14") is null,
            "Cloud contract: test entries and the Kassensturz count itself are never sent to TOR Cloud");

        // A test-mode movement through the repository stays local.
        var movement = await new CashMovementRepository(db, audit).AddAsync(new CashMovementRequest(CashMovementKind.Entnahme, 700, "Test", CashBusinessCase.Auszahlung), "chef");
        assert(await PayloadAsync(db, $"cash-{movement.Id}") is null,
            "Cloud contract: a test Entnahme booked through the repository is not queued for TOR Cloud");

        var repository = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/FiscalComplianceServices.cs"));
        var management = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/BusinessManagementService.cs"));
        assert(
            Count(repository, "TorCloudOutbox.EnqueueCashMovement(c, tx, movement);") == 2 &&
            management.Contains("TorCloudOutbox.EnqueueZClosed(c, (SqliteTransaction)tx, new TorCloudZClosing(", StringComparison.Ordinal),
            "Cloud contract: both cash booking paths (Einlage/Entnahme and Kassendifferenz) and the Z closing queue their Cloud event inside their own transaction");
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static async Task<string?> PayloadAsync(SqliteDatabase db, string eventId)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT payload FROM cloud_outbox WHERE event_id=$id;";
        q.Parameters.AddWithValue("$id", eventId);
        return await q.ExecuteScalarAsync() as string;
    }

    private static JsonNode? Fixture(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Cloud", "tests", "fixtures", name);
            if (File.Exists(candidate))
                return JsonNode.Parse(File.ReadAllText(candidate))?["payload"];
        }

        return null;
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
