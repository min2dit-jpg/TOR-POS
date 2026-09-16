using TorPos.Infrastructure;

public static class R70ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root, "r70");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "audit.db");
        var db = new SqliteDatabase(path);
        var backup = new DatabaseBackupService(db);
        var migrator = new SchemaMigrationService(
            db,
            backup,
            Path.Combine(dir, "migration-backups"));

        var migration = await migrator.InitializeDatabaseAsync();

        assert(
            migration.ToVersion >= 2 &&
            SchemaMigrationService.TargetSchemaVersion >= 2,
            "R70 schema V2 installs controlled POS action audit");

        var service = new ControlledPosActionService(db);
        var operationId = Guid.NewGuid().ToString("N");
        var actionId = Guid.NewGuid().ToString("N");

        await service.AppendAsync(
            new PosActionLogRequest
            {
                ActionId = actionId,
                Phase = "AUTHORIZED",
                Actor = "tester",
                RegisterId = "1",
                OperationId = operationId,
                ActionType = "SOFORT_STORNO",
                Reason = "Fehlbuchung",
                EntityType = "CURRENT_CART_LINE",
                EntityId = "42",
                BeforeTotalCents = 1200,
                AfterTotalCents = 700,
                AmountCents = 500,
                Details = "product=Test"
            });

        await service.AppendAsync(
            new PosActionLogRequest
            {
                ActionId = actionId,
                Phase = "APPLIED",
                Actor = "tester",
                RegisterId = "1",
                OperationId = operationId,
                ActionType = "SOFORT_STORNO",
                Reason = "Fehlbuchung",
                EntityType = "CURRENT_CART_LINE",
                EntityId = "42",
                BeforeTotalCents = 1200,
                AfterTotalCents = 700,
                AmountCents = 500,
                Details = "product=Test"
            });

        var integrity = await service.VerifyIntegrityAsync();

        assert(
            integrity.Valid && integrity.EntryCount == 2,
            "R70 controlled action hash chain verifies after authorized and applied phases");

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT a.entry_hash,b.prev_hash
                FROM pos_action_log a
                JOIN pos_action_log b ON b.id=a.id+1
                ORDER BY a.id
                LIMIT 1;
                """;
            using var r = q.ExecuteReader();
            assert(
                r.Read() &&
                string.Equals(
                    r.GetString(0),
                    r.GetString(1),
                    StringComparison.OrdinalIgnoreCase),
                "R70 every controlled audit entry links to previous entry hash");
        }

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COUNT(*)
                FROM audit_log
                WHERE event_type='SOFORT_STORNO_APPLIED'
                  AND details LIKE '%Fehlbuchung%'
                  AND details LIKE '%' || $operation || '%';
                """;
            q.Parameters.AddWithValue("$operation", operationId);
            assert(
                Convert.ToInt32(q.ExecuteScalar()) == 1,
                "R70 controlled action is mirrored to immutable general audit in same workflow");
        }

        await reject(
            async () =>
            {
                using var c = db.OpenConnection();
                using var q = c.CreateCommand();
                q.CommandText = "UPDATE pos_action_log SET reason='tamper' WHERE id=1;";
                await q.ExecuteNonQueryAsync();
            },
            "R70 controlled POS audit rows cannot be updated");

        await reject(
            async () =>
            {
                using var c = db.OpenConnection();
                using var q = c.CreateCommand();
                q.CommandText = "DELETE FROM pos_action_log WHERE id=1;";
                await q.ExecuteNonQueryAsync();
            },
            "R70 controlled POS audit rows cannot be deleted");

        await reject(
            () => service.AppendAsync(
                new PosActionLogRequest
                {
                    ActionId = actionId,
                    Phase = "APPLIED",
                    Actor = "tester",
                    RegisterId = "1",
                    OperationId = operationId,
                    ActionType = "SOFORT_STORNO",
                    Reason = "Fehlbuchung",
                    EntityType = "CURRENT_CART_LINE",
                    EntityId = "42",
                    BeforeTotalCents = 1200,
                    AfterTotalCents = 700,
                    AmountCents = 500,
                    Details = "duplicate"
                }),
            "R70 same controlled action phase cannot be appended twice");

        // Demonstrate tamper evidence even if an attacker with raw DB access
        // deliberately removes the update trigger first.
        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = """
                DROP TRIGGER trg_pos_action_no_update;
                UPDATE pos_action_log
                SET details='raw-db-tamper'
                WHERE id=1;
                """;
            q.ExecuteNonQuery();
        }

        var tampered = await service.VerifyIntegrityAsync();

        assert(
            !tampered.Valid,
            "R70 hash-chain verification detects raw DB tampering after trigger bypass");
    }
}
