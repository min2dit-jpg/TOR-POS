using TorPos.Core;
using TorPos.Infrastructure;

// R132: DSFinV-K 3.2 - the Stammdaten are kept once per Kassenabschluss, and
// "werden Änderungen an den ... Stammdaten vorgenommen, ist zuvor automatisch
// ein Abschluss zu erstellen".
//
// Before R132 a Z-Bericht stored no master data at all, so an export read the
// company data as they were on the day of the export: a closing from before a
// move to a new address was exported with the new one. And nothing stopped the
// company data from changing in the middle of a period.
public static class R132ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r132-master-data");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r132.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);
        var parked = new ParkedReceiptRepository(db);
        var service = new DsfinvkMasterDataService(db, settings, management, new DailyClosingGuard(parked), audit);

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM pragma_table_info('z_report_archive') WHERE name='master_data';";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 1 && SchemaMigrationService.TargetSchemaVersion >= 12,
                "R132 schema migration V12 adds master_data to z_report_archive");
        }

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "Alt GmbH",
            ["company.street"] = "Alte Straße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        var receipt = 132000L;
        async Task SaleAsync(SqliteDatabase target)
        {
            await Task.Delay(15);
            await using var c = target.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents) VALUES($r,$at,'CASH',500,500,'TEST_FIXTURE','SALE',500); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$r", ++receipt);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            await using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Tee',1,500,19,500);";
            item.Parameters.AddWithValue("$s", id);
            await item.ExecuteNonQueryAsync();
        }

        async Task<long> ClosingCountAsync()
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM z_report_archive;";
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }

        await SaleAsync(db);

        // ---------- the guard ----------
        var refusedDirectly = false;
        try { await settings.SaveManyAsync(new Dictionary<string, string> { ["company.name"] = "Neu GmbH" }); }
        catch (MasterDataChangeRequiresClosingException) { refusedDirectly = true; }
        assert(refusedDirectly && await settings.GetAsync("company.name") == "Alt GmbH",
            "R132 company data cannot be changed directly while a sale is waiting for a closing");

        await settings.SaveManyAsync(new Dictionary<string, string> { ["ui.theme"] = "DUNKEL", ["company.name"] = "  Alt GmbH " });
        assert(await settings.GetAsync("ui.theme") == "DUNKEL",
            "R132 other settings, and re-saving the same company data (surrounding spaces aside), are not affected");

        // ---------- automatic closing ----------
        var order = await parked.ParkAsync(new[] { new CartLine { ProductName = "Döner", Quantity = 1, UnitPriceCents = 800, VatRate = 7m } }, 0, "kasse1");
        var refusedWithOpenOrder = false;
        try { await service.SaveSettingsAsync(new Dictionary<string, string> { ["company.name"] = "Neu GmbH" }, "admin"); }
        catch (InvalidOperationException ex) when (ex is not MasterDataChangeRequiresClosingException) { refusedWithOpenOrder = ex.Message.Contains("DSFinV-K 3.2"); }
        assert(refusedWithOpenOrder && await ClosingCountAsync() == 0 && await settings.GetAsync("company.name") == "  Alt GmbH ",
            "R132 while a parked receipt blocks the Z-Bericht, the change is refused and nothing is closed or saved");

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE parked_receipts SET status='CANCELLED' WHERE id=$id;";
            q.Parameters.AddWithValue("$id", order.Id);
            await q.ExecuteNonQueryAsync();
        }

        var changed = await service.SaveSettingsAsync(new Dictionary<string, string> { ["company.name"] = "Neu GmbH", ["company.street"] = "Neue Straße 2" }, "admin");
        string firstSnapshot;
        long autoAudit;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT master_data FROM z_report_archive ORDER BY z_number DESC LIMIT 1;";
            firstSnapshot = (string)(await q.ExecuteScalarAsync())!;
            q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type='Z_REPORT_AUTO_MASTER_DATA';";
            autoAudit = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        var firstMaster = DsfinvkMasterDataRules.Deserialize(firstSnapshot)!;
        assert(changed.AutomaticClosing is not null && changed.ChangedMasterData.Count == 2 &&
               firstMaster.CompanyName == "Alt GmbH" && firstMaster.Street == "Alte Straße 1" && firstMaster.TaxNumber == "27/123/45678" &&
               firstMaster.KasseSerial.StartsWith("TORPOS-") &&
               await settings.GetAsync("company.name") == "Neu GmbH" && autoAudit == 1,
            "R132 a change of company data with a sale waiting closes that period first - under the OLD data - then saves the new data, and records why");

        var again = await service.SaveSettingsAsync(new Dictionary<string, string> { ["company.city"] = "Potsdam" }, "admin");
        assert(again.AutomaticClosing is null && await ClosingCountAsync() == 1 && await settings.GetAsync("company.city") == "Potsdam",
            "R132 with nothing waiting for a closing, company data change without an extra closing");

        // ---------- software version ----------
        await SaleAsync(db);
        await settings.SaveManyAsync(new Dictionary<string, string> { [DsfinvkMasterDataRules.SoftwareVersionKey] = "0.7.33.700 (R70)" });

        // F-2: an open parked receipt blocks the automatic closing exactly like
        // a manual Z. The closing is deferred and the old version kept.
        var blockingOrder = await parked.ParkAsync(new[] { new CartLine { ProductName = "Ayran", Quantity = 1, UnitPriceCents = 250, VatRate = 7m } }, 0, "kasse1");
        var closingsBeforeDeferred = await ClosingCountAsync();
        var deferred = await service.EnsureSoftwareVersionAsync("SYSTEM");
        long deferredAudit;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type='Z_REPORT_AUTO_SOFTWARE_UPDATE_DEFERRED';";
            deferredAudit = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.CommandText = "UPDATE parked_receipts SET status='CANCELLED' WHERE id=$id;";
            q.Parameters.AddWithValue("$id", blockingOrder.Id);
            await q.ExecuteNonQueryAsync();
        }
        var storedAfterDeferred = await settings.GetAsync(DsfinvkMasterDataRules.SoftwareVersionKey);
        assert(deferred is null && await ClosingCountAsync() == closingsBeforeDeferred && deferredAudit == 1 &&
               storedAfterDeferred == "0.7.33.700 (R70)" &&
               DsfinvkMasterDataStore.IsUpdateClosingPending(storedAfterDeferred) &&
               !DsfinvkMasterDataStore.IsUpdateClosingPending(DsfinvkMasterDataStore.RunningSoftwareVersion),
            "F-2 an open parked receipt defers the automatic closing after an update: no Z, old version kept, the deferral audited and reported as pending");

        var updateClosing = await service.EnsureSoftwareVersionAsync("SYSTEM");
        var secondRun = await service.EnsureSoftwareVersionAsync("SYSTEM");
        string secondSnapshot;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT master_data FROM z_report_archive ORDER BY z_number DESC LIMIT 1;";
            secondSnapshot = (string)(await q.ExecuteScalarAsync())!;
        }
        var secondMaster = DsfinvkMasterDataRules.Deserialize(secondSnapshot)!;
        assert(updateClosing is not null && secondRun is null &&
               secondMaster.SoftwareVersion == "0.7.33.700 (R70)" && secondMaster.CompanyName == "Neu GmbH" && secondMaster.City == "Potsdam" &&
               await settings.GetAsync(DsfinvkMasterDataRules.SoftwareVersionKey) == DsfinvkMasterDataStore.RunningSoftwareVersion,
            "R132 sales recorded under an older TOR version are closed under that version at the next start, once; afterwards the running version applies");

        // ---------- the export uses each closing's own master data ----------
        var export = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await export.ValidateAsync(from, to);
        var folder = await export.ExportAsync(from, to, Path.Combine(dir, "out"));
        var closings = File.ReadAllLines(Path.Combine(folder, "cashpointclosing.csv")).Skip(1).ToArray();
        var registers = File.ReadAllLines(Path.Combine(folder, "cashregister.csv")).Skip(1).ToArray();
        assert(report.Ready && report.Issues.All(x => x.Code != "STAMMDATEN") &&
               closings.Length == 2 &&
               closings[0].Contains("\"Alt GmbH\";\"Alte Straße 1\";\"10115\";\"Berlin\"") &&
               closings[1].Contains("\"Neu GmbH\";\"Neue Straße 2\";\"10115\";\"Potsdam\"") &&
               registers[1].Contains("\"0.7.33.700 (R70)\""),
            "R132 the export writes every closing with the company data and software version it was recorded under, not today's");

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO z_report_archive(z_number,created_at,period_from,period_to,operator_name,receipt_count,gross_cents,cash_cents,card_cents,fiscal_status,snapshot_text)
                VALUES(99,$at,$at,$at,'alt',0,0,0,0,'TEST','vor R132');
                """;
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }
        var withLegacy = await export.ValidateAsync(from, DateTimeOffset.Now.AddMinutes(1));
        assert(withLegacy.Issues.Any(x => x.Code == "STAMMDATEN" && !x.Blocking && x.Message.Contains("99")),
            "R132 a closing from before R132 has no stored master data; the export says it uses the current settings for it");

        // ---------- what counts as waiting for a closing ----------
        var testDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r132-test-mode.db"));
        var testSettings = new SettingsRepository(testDb);
        var testAudit = new AuditLogRepository(testDb);
        await new CashMovementRepository(testDb, testAudit).AddAsync(new CashMovementRequest(CashMovementKind.Einlage, 10000, "Wechselgeld", CashBusinessCase.Geldtransit), "kasse1");
        await testSettings.SaveManyAsync(new Dictionary<string, string> { ["company.name"] = "Testbetrieb" });
        var testService = new DsfinvkMasterDataService(testDb, testSettings, new BusinessManagementService(testDb, testSettings, testAudit), new DailyClosingGuard(new ParkedReceiptRepository(testDb)), testAudit);
        var firstStart = await testService.EnsureSoftwareVersionAsync("SYSTEM");
        assert(await testSettings.GetAsync("company.name") == "Testbetrieb" && firstStart is null &&
               await testSettings.GetAsync(DsfinvkMasterDataRules.SoftwareVersionKey) == DsfinvkMasterDataStore.RunningSoftwareVersion,
            "R132 a test-mode cash movement never reaches a closing and does not block company data; the first start records the running version without closing");

        await using (var c = testDb.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode) VALUES($at,'ENTNAHME',2000,'Bank','kasse1','PRODUCTION');";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }
        var refusedForCash = false;
        try { await testSettings.SaveManyAsync(new Dictionary<string, string> { ["company.name"] = "Anders" }); }
        catch (MasterDataChangeRequiresClosingException) { refusedForCash = true; }
        assert(refusedForCash,
            "R132 a fiscal cash movement waits for a closing like a sale does");
    }
}
