using Microsoft.Data.Sqlite;
using TorPos.App;
using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantFoundationTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.Tischplan),
            "Restaurant includes the essential Tischplan");

        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.SplitrechnungNachArtikel),
            "Restaurant includes essential split billing");

        assert(
            !RestaurantProductFeatures.Includes(
                RestaurantProductTier.Restaurant,
                RestaurantFeature.KitchenDisplaySystem),
            "Restaurant standard does not silently unlock Plus KDS");

        assert(
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.RestaurantPlus,
                RestaurantFeature.KitchenDisplaySystem) &&
            RestaurantProductFeatures.Includes(
                RestaurantProductTier.RestaurantPlus,
                RestaurantFeature.Tischplan),
            "Restaurant Plus contains Plus modules and all standard essentials");

        var exportTime = DateTimeOffset.Parse("2026-09-22T18:00:00+02:00");
        var restaurantOrderRecord = new DsfinvkOrderRecord(
            Id: 900,
            ParkNumber: 0,
            PickupNumber: 0,
            Sequence: 1,
            Kind: OrderBestellungKind.Annahme,
            StartedAt: exportTime,
            CreatedAt: exportTime.AddMinutes(1),
            Operator: "KELLNER-1",
            ImHaus: true,
            Lines: new[]
            {
                new CartLine
                {
                    ProductId = 99,
                    ProductName = "Restaurant Exportartikel",
                    Quantity = 1m,
                    UnitPriceCents = 1290,
                    ListUnitPriceCents = 1290,
                    VatRate = 19m,
                    Unit = "Stück"
                }
            },
            Tse: new DsfinvkTseResult(
                "", "", "", "", null, true, "TEST"),
            Training: false,
            CustomBonId: "RB-900",
            CustomAllocationGroup: "Restaurant session-900");

        var exportRows = DsfinvkClosingBuilder.Build(
            new DsfinvkClosingInput
            {
                Closing = new DsfinvkClosing(1, exportTime.AddHours(1)),
                Master = new DsfinvkMasterData(
                    "K1",
                    "TOR Restaurant Test",
                    "Teststr. 1",
                    "10115",
                    "Berlin",
                    "DE",
                    "12/345/67890",
                    "DE123456789",
                    "TOR",
                    "Restaurant",
                    "TEST-001",
                    "TOR Restaurant",
                    "R185"),
                OrderRecords = new[] { restaurantOrderRecord }
            });

        assert(
            exportRows.For("Bonkopf").Any(
                row => string.Equals(
                    Convert.ToString(row["BON_ID"]),
                    "RB-900",
                    StringComparison.Ordinal)) &&
            exportRows.For("Bonkopf_AbrKreis").Any(
                row => string.Equals(
                    Convert.ToString(row["ABRECHNUNGSKREIS"]),
                    "Restaurant session-900",
                    StringComparison.Ordinal)),
            "Restaurant Bestellung keeps its RB BON_ID and Restaurant allocation group in DSFinV-K rows");

        var standardEntitlements = new RestaurantEntitlementService(
            new FakeCommercialLicenseService(
                new CommercialLicenseStatus(
                    CommercialLicenseState.Active,
                    "TEST",
                    Features: Array.Empty<string>())));

        var plusEntitlements = new RestaurantEntitlementService(
            new FakeCommercialLicenseService(
                new CommercialLicenseStatus(
                    CommercialLicenseState.Active,
                    "TEST",
                    Features: new[] { RestaurantEntitlementService.PlusFeatureCode })));

        assert(
            standardEntitlements.CurrentTier() == RestaurantProductTier.Restaurant &&
            !standardEntitlements.IsEnabled(RestaurantFeature.KitchenDisplaySystem),
            "Restaurant Standard license cannot unlock Plus KDS");

        assert(
            plusEntitlements.CurrentTier() == RestaurantProductTier.RestaurantPlus &&
            plusEntitlements.IsEnabled(RestaurantFeature.KitchenDisplaySystem) &&
            plusEntitlements.IsEnabled(RestaurantFeature.Tischplan),
            "Signed RESTAURANT_PLUS entitlement unlocks Plus while keeping Standard features");

        var standardKdsRejected = false;
        try
        {
            standardEntitlements.Require(
                RestaurantFeature.KitchenDisplaySystem);
        }
        catch (InvalidOperationException)
        {
            standardKdsRejected = true;
        }

        assert(
            standardKdsRejected,
            "Restaurant Standard rejects KDS at the service boundary");

        var standardHandheld = new RestaurantHandheldService(
            standardEntitlements,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);

        var standardHandheldRejected = false;
        try
        {
            await standardHandheld.GetTablesAsync("TEST","TEST");
        }
        catch (InvalidOperationException)
        {
            standardHandheldRejected = true;
        }

        assert(
            standardHandheldRejected,
            "Restaurant Standard rejects handheld access before any database operation");

        var standardReservationRejected = false;
        try
        {
            var standardReservations = new RestaurantReservationService(
                null!,
                standardEntitlements);
            await standardReservations.CreateAsync(
                DateTimeOffset.UtcNow.AddHours(1),
                120,
                2,
                "Testkunde",
                "",
                "",
                null,
                "ADMIN");
        }
        catch (InvalidOperationException)
        {
            standardReservationRejected = true;
        }

        assert(
            standardReservationRejected,
            "Restaurant Standard rejects Plus reservations before any database operation");

        var standardTerminalRejected = false;
        try
        {
            var standardTerminals = new RestaurantTerminalRegistry(
                null!,
                standardEntitlements);
            await standardTerminals.RegisterOrHeartbeatAsync(
                "KASSE-2",
                "Kasse 2",
                "KASSE",
                "TEST",
                "TEST-PC");
        }
        catch (InvalidOperationException)
        {
            standardTerminalRejected = true;
        }

        assert(
            standardTerminalRejected,
            "Restaurant Standard rejects Plus multi-terminal access before any database operation");

        var standardSyncRejected = false;
        try
        {
            var standardSync = new RestaurantSyncService(
                null!,
                standardEntitlements);
            await standardSync.GetEventsAfterAsync(0);
        }
        catch (InvalidOperationException)
        {
            standardSyncRejected = true;
        }

        assert(
            standardSyncRejected,
            "Restaurant Standard rejects Plus delta sync before any database operation");

        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        var root = Path.Combine(
            Path.GetTempPath(),
            "tor-restaurant-foundation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "RESTAURANT",
                EnvironmentVariableTarget.Process);

            var restaurantPath = Path.Combine(root, "restaurant.db");
            var db = new SqliteDatabase(restaurantPath);
            var backup = new DatabaseBackupService(db);
            var migration = new SchemaMigrationService(
                db,
                backup,
                Path.Combine(root, "migration-backups"));

            var result = await migration.InitializeDatabaseAsync();

            assert(
                result.ToVersion == SchemaMigrationService.TargetSchemaVersion &&
                result.ToVersion == 33,
                "Restaurant database reaches schema version 33");

            await using (var c = db.OpenConnection())
            {
                assert(
                    TableExists(c, "restaurant_tables") &&
                    TableExists(c, "restaurant_sessions") &&
                    TableExists(c, "restaurant_session_events") &&
                    TableExists(c, "restaurant_bestellungen") &&
                    TableExists(c, "restaurant_bestellung_items") &&
                    TableExists(c, "restaurant_kitchen_jobs") &&
                    TableExists(c, "restaurant_kitchen_status") &&
                    TableExists(c, "restaurant_pairing_codes") &&
                    TableExists(c, "restaurant_handheld_devices") &&
                    TableExists(c, "restaurant_reservations") &&
                    TableExists(c, "restaurant_terminals") &&
                    TableExists(c, "restaurant_device_commands"),
                    "Restaurant-only tables including Bestellung, kitchen, reservations, terminal and device-command records are created for the Restaurant product");

                var immutableBestellung = false;
                try
                {
                    using var q = c.CreateCommand();
                    q.CommandText = """
                        INSERT INTO restaurant_areas(name,sort_order,is_active) VALUES('TEST',0,1);
                        INSERT INTO restaurant_tables(area_id,code,display_name,seats,sort_order,is_active,version)
                        VALUES((SELECT id FROM restaurant_areas WHERE name='TEST'),'FT','Fiscal Test',2,0,1,1);
                        INSERT INTO restaurant_sessions(id,table_id,opened_at,updated_at,state,opened_by,assigned_waiter,guest_count,note,version)
                        VALUES('fiscal-test',(SELECT id FROM restaurant_tables WHERE code='FT'),$now,$now,'OPEN','T','T',1,'',1);
                        INSERT INTO restaurant_bestellungen(
                            session_id,sequence,kind,started_at,created_at,operator_name,total_cents,
                            client_id,transaction_number,signature_counter,serial_number,signature,
                            start_log_time,log_time,outage,outage_reason)
                        VALUES('fiscal-test',1,'ANNAHME',$now,$now,'T',100,'','','','','','','',1,'TEST');
                        UPDATE restaurant_bestellungen SET operator_name='X' WHERE session_id='fiscal-test';
                        """;
                    q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                    q.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    immutableBestellung = true;
                }

                assert(
                    immutableBestellung,
                    "Restaurant Bestellung records are append-only");
            }

            var pairing = new RestaurantHandheldPairingService(
                db,
                plusEntitlements);

            var pairingCode = await pairing.CreatePairingCodeAsync(
                "ADMIN",
                TimeSpan.FromMinutes(5));

            var paired = await pairing.PairAsync(
                pairingCode.Code,
                "DEVICE-001",
                "Handheld 1");

            await pairing.RequireAuthenticatedAsync(
                paired.DeviceId,
                paired.DeviceToken);

            await using (var c = db.OpenConnection())
            {
                using var token = c.CreateCommand();
                token.CommandText =
                    "SELECT token_hash FROM restaurant_handheld_devices WHERE device_id=$id;";
                token.Parameters.AddWithValue("$id", paired.DeviceId);
                var stored = Convert.ToString(token.ExecuteScalar()) ?? "";

                assert(
                    !string.Equals(stored, paired.DeviceToken, StringComparison.Ordinal) &&
                    stored.Length == 64,
                    "Restaurant handheld stores only a SHA-256 token hash, never the raw device token");
            }

            var pairingReuseRejected = false;
            try
            {
                await pairing.PairAsync(
                    pairingCode.Code,
                    "DEVICE-002",
                    "Handheld 2");
            }
            catch (InvalidOperationException)
            {
                pairingReuseRejected = true;
            }

            assert(
                pairingReuseRejected,
                "Restaurant handheld pairing code is one-time use");

            await pairing.DeactivateAsync(
                paired.DeviceId);

            var deactivatedRejected = false;
            try
            {
                await pairing.RequireAuthenticatedAsync(
                    paired.DeviceId,
                    paired.DeviceToken);
            }
            catch (UnauthorizedAccessException)
            {
                deactivatedRejected = true;
            }

            assert(
                deactivatedRejected,
                "Deactivated Restaurant handheld token is rejected");

            var repo = new RestaurantRepository(db);
            var areaId = await repo.SaveAreaAsync("Innenbereich");
            var tableId = await repo.SaveTableAsync(
                areaId,
                "T01",
                "Tisch 1",
                seats: 4);

            var terminals = new RestaurantTerminalRegistry(
                db,
                plusEntitlements);

            await terminals.RegisterOrHeartbeatAsync(
                "KASSE-2",
                "Kasse 2",
                "KASSE",
                "R190",
                "SERVER-2");

            await terminals.RegisterOrHeartbeatAsync(
                "KASSE-2",
                "Kasse 2 Neu",
                "KASSE",
                "R190",
                "SERVER-2");

            var terminalRows = await terminals.ListAsync();

            assert(
                terminalRows.Count(x => x.TerminalId == "KASSE-2") == 1 &&
                terminalRows.Single(x => x.TerminalId == "KASSE-2").DisplayName == "Kasse 2 Neu" &&
                terminalRows.Single(x => x.TerminalId == "KASSE-2").IsActive,
                "Restaurant Plus terminal heartbeat updates one stable terminal identity without duplicates");

            var reservations = new RestaurantReservationService(
                db,
                plusEntitlements);

            var reservationAt = DateTimeOffset.UtcNow.AddHours(2);
            var reservation = await reservations.CreateAsync(
                reservationAt,
                durationMinutes: 120,
                guestCount: 3,
                customerName: "Familie Test",
                phone: "030123456",
                note: "Fensterplatz",
                tableId,
                actor: "ADMIN");

            var listedReservations = await reservations.ListAsync(
                reservationAt.AddHours(-1),
                reservationAt.AddHours(3));

            assert(
                reservation.Status == "BOOKED" &&
                reservation.TableId == tableId &&
                reservation.Version == 1 &&
                listedReservations.Any(x => x.Id == reservation.Id),
                "Restaurant Plus creates and lists a versioned table reservation");

            var seatedReservation = await reservations.SetStatusAsync(
                reservation.Id,
                reservation.Version,
                "SEATED",
                "ADMIN");

            var staleReservationRejected = false;
            try
            {
                await reservations.AssignTableAsync(
                    reservation.Id,
                    reservation.Version,
                    tableId,
                    "ADMIN");
            }
            catch (InvalidOperationException)
            {
                staleReservationRejected = true;
            }

            assert(
                seatedReservation.Status == "SEATED" &&
                seatedReservation.Version == 2 &&
                staleReservationRejected,
                "Restaurant reservation status advances version and rejects stale writes");

            var session = await repo.OpenTableAsync(
                tableId,
                "KELLNER-1",
                guestCount: 3,
                note: "Kinderstuhl",
                deviceId: "KASSE-1");

            assert(
                session.TableId == tableId &&
                session.State == RestaurantTableSessionState.Open &&
                session.GuestCount == 3 &&
                session.Note == "Kinderstuhl" &&
                session.Version == 1,
                "Opening a table captures guests and note in one versioned Tischvorgang");

            var duplicateRejected = false;
            try
            {
                await repo.OpenTableAsync(
                    tableId,
                    "KELLNER-2",
                    guestCount: 2);
            }
            catch (InvalidOperationException ex)
            {
                duplicateRejected = ex.Message.Contains(
                    "bereits geöffnet",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                duplicateRejected,
                "Second live Tischvorgang for the same table is rejected");

            var details = await repo.UpdateSessionDetailsAsync(
                session.Id,
                expectedVersion: 1,
                guestCount: 4,
                note: "Geburtstagstisch",
                actor: "KELLNER-1",
                deviceId: "KASSE-1");

            assert(
                details.GuestCount == 4 &&
                details.Note == "Geburtstagstisch" &&
                details.Version == 2,
                "Restaurant guest count and table note update advances concurrency version");

            var sync = new RestaurantSyncService(
                db,
                plusEntitlements);

            var initialSync = await sync.GetEventsAfterAsync(
                0,
                500);

            assert(
                initialSync.Events.Count >= 2 &&
                initialSync.Events.Zip(
                    initialSync.Events.Skip(1),
                    (left, right) => left.EventId < right.EventId)
                    .All(x => x),
                "Restaurant Plus delta sync returns append-only events in stable event-id order");

            var emptyDelta = await sync.GetEventsAfterAsync(
                initialSync.LastEventId,
                500);

            assert(
                emptyDelta.Events.Count == 0 &&
                emptyDelta.LastEventId == initialSync.LastEventId,
                "Restaurant Plus delta sync returns no duplicate events after the acknowledged event id");

            var reassigned = await repo.ReassignWaiterAsync(
                session.Id,
                expectedVersion: 2,
                newWaiter: "KELLNER-2",
                actor: "ADMIN",
                deviceId: "KASSE-1");

            assert(
                reassigned.AssignedWaiter == "KELLNER-2" &&
                reassigned.Version == 3,
                "Kellner reassignment increments the optimistic concurrency version");

            var staleRejected = false;
            try
            {
                await repo.ReassignWaiterAsync(
                    session.Id,
                    expectedVersion: 2,
                    newWaiter: "KELLNER-3",
                    actor: "ADMIN");
            }
            catch (InvalidOperationException ex)
            {
                staleRejected = ex.Message.Contains(
                    "zwischenzeitlich geändert",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                staleRejected,
                "Stale table-session writes are rejected instead of overwriting newer data");

            var product = new Product
            {
                Id = 900001,
                Name = "Restaurant Testartikel",
                BasePriceCents = 1290,
                VatRate = 19m
            };

            var idempotentTableId = await repo.SaveTableAsync(
                areaId,
                "TIDEM",
                "Idempotenz Tisch",
                seats: 2,
                sortOrder: 90);

            var idempotentSession = await repo.OpenTableAsync(
                idempotentTableId,
                "KELLNER-1",
                guestCount: 1,
                deviceId: "HANDHELD-IDEM");

            var firstIdempotentAdd =
                await repo.AddItemWithLineTokenAsync(
                    idempotentSession.Id,
                    idempotentSession.Version,
                    product,
                    1m,
                    "KELLNER-1",
                    "ITEM-IDEMPOTENCY-0001",
                    "HANDHELD-IDEM");

            var replayedIdempotentAdd =
                await repo.AddItemWithLineTokenAsync(
                    idempotentSession.Id,
                    idempotentSession.Version,
                    product,
                    1m,
                    "KELLNER-1",
                    "ITEM-IDEMPOTENCY-0001",
                    "HANDHELD-IDEM");

            var idempotentItems =
                await repo.ListActiveItemsAsync(
                    idempotentSession.Id);

            assert(
                firstIdempotentAdd.Created &&
                !replayedIdempotentAdd.Created &&
                firstIdempotentAdd.Item.Id ==
                    replayedIdempotentAdd.Item.Id &&
                idempotentItems.Count == 1 &&
                (await repo.GetSessionAsync(
                    idempotentSession.Id))?.Version == 2,
                "Restaurant add-item retry with the same line token creates exactly one position and advances the table once");

            var idempotentKitchen =
                new RestaurantKitchenOutbox(db);
            var idempotentSessionAfterAdd =
                await repo.GetSessionAsync(
                    idempotentSession.Id)
                ?? throw new InvalidOperationException(
                    "Idempotency session missing.");

            const string idempotentKitchenJob =
                "KITCHEN-IDEMPOTENCY-0001";

            await idempotentKitchen.EnqueueNewItemIdempotentAsync(
                idempotentSessionAfterAdd,
                firstIdempotentAdd.Item,
                "Idempotenz Tisch",
                "KELLNER-1",
                idempotentKitchenJob,
                KitchenStations.Grill);

            await idempotentKitchen.EnqueueNewItemIdempotentAsync(
                idempotentSessionAfterAdd,
                firstIdempotentAdd.Item,
                "Idempotenz Tisch",
                "KELLNER-1",
                idempotentKitchenJob,
                KitchenStations.Grill);

            assert(
                (await idempotentKitchen.PendingAsync())
                    .Count(x => x.Id == idempotentKitchenJob) == 1,
                "Restaurant kitchen retry with the same job id creates exactly one durable printer/KDS job");

            var commandJournal =
                new RestaurantCommandJournal(db);

            var newCommand = await commandJournal.BeginAsync(
                "HANDHELD-IDEM",
                "CMD-0001",
                "ADD_ITEM",
                "HASH-0001",
                idempotentSession.Id);

            await commandJournal.CompleteAsync(
                "HANDHELD-IDEM",
                "CMD-0001",
                2);

            var completedReplay =
                await commandJournal.BeginAsync(
                    "HANDHELD-IDEM",
                    "CMD-0001",
                    "ADD_ITEM",
                    "HASH-0001",
                    idempotentSession.Id);

            assert(
                newCommand.State ==
                    RestaurantCommandClaimState.New &&
                completedReplay.State ==
                    RestaurantCommandClaimState.Completed &&
                completedReplay.ResultSessionVersion == 2,
                "Completed Restaurant device command replay returns the stored result instead of executing again");

            var commandCollisionRejected = false;
            try
            {
                await commandJournal.BeginAsync(
                    "HANDHELD-IDEM",
                    "CMD-0001",
                    "ADD_ITEM",
                    "DIFFERENT-HASH",
                    idempotentSession.Id);
            }
            catch (InvalidOperationException)
            {
                commandCollisionRejected = true;
            }

            assert(
                commandCollisionRejected,
                "Restaurant rejects reuse of one Command-ID for a different request");

            var recoverableCommand =
                await commandJournal.BeginAsync(
                    "HANDHELD-IDEM",
                    "CMD-RECOVER-0001",
                    "CANCEL_ITEM",
                    "HASH-CANCEL-0001",
                    idempotentSession.Id);

            await commandJournal.ReleaseForRecoveryAsync(
                "HANDHELD-IDEM",
                "CMD-RECOVER-0001");

            var recoveredCommand =
                await commandJournal.BeginAsync(
                    "HANDHELD-IDEM",
                    "CMD-RECOVER-0001",
                    "CANCEL_ITEM",
                    "HASH-CANCEL-0001",
                    idempotentSession.Id);

            if (recoverableCommand.State !=
                    RestaurantCommandClaimState.New ||
                recoveredCommand.State !=
                    RestaurantCommandClaimState.Recovered)
            {
                throw new InvalidOperationException(
                    "Interrupted Restaurant device command must be reclaimable for crash-safe retry.");
            }

            var item = await repo.AddItemAsync(
                session.Id,
                expectedSessionVersion: reassigned.Version,
                product,
                quantity: 2m,
                operatorName: "KELLNER-2",
                deviceId: "HANDHELD-TEST");

            var afterItem = await repo.GetSessionAsync(session.Id);
            assert(
                item.QuantityMilli == 2000 &&
                item.LineTotalCents == 2580 &&
                afterItem is not null &&
                afterItem.Version == reassigned.Version + 1,
                "Adding a table item is cent-exact and advances the session version");

            var staleItemRejected = false;
            try
            {
                await repo.AddItemAsync(
                    session.Id,
                    expectedSessionVersion: reassigned.Version,
                    product,
                    quantity: 1m,
                    operatorName: "KELLNER-2");
            }
            catch (InvalidOperationException ex)
            {
                staleItemRejected = ex.Message.Contains(
                    "zwischenzeitlich geändert",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                staleItemRejected,
                "Stale concurrent item add is rejected instead of duplicating a table position");

            var fiscalState = new RestaurantFiscalOrderService(db, null!);
            await InsertRestaurantBestellungAsync(
                db,
                session.Id,
                sequence: 1,
                kind: "ANNAHME",
                product,
                quantityMilli: 2000);

            assert(
                await fiscalState.IsCurrentStateSecuredAsync(session.Id),
                "Restaurant secured-state matches the table after immutable Bestellung capture");

            var splitItems = await repo.ListActiveItemsAsync(session.Id);
            var splitQuote = RestaurantSplitCalculator.ByItems(
                splitItems,
                new[]
                {
                    new RestaurantSplitSelection(
                        splitItems.Single().Id,
                        1000)
                });

            assert(
                splitQuote.TotalCents == 1290 &&
                splitQuote.Lines.Single().QuantityMilli == 1000,
                "Item split selects a partial quantity with cent-exact amount");

            var equalShares = RestaurantSplitCalculator.EqualShares(1000, 3);
            assert(
                equalShares.SequenceEqual(new long[] { 334, 333, 333 }) &&
                equalShares.Sum() == 1000,
                "Equal-person split assigns remainder cents deterministically");

            var secondTableId = await repo.SaveTableAsync(
                areaId,
                "T02",
                "Tisch 2",
                seats: 4,
                sortOrder: 2);

            var moved = await repo.MoveSessionToTableAsync(
                session.Id,
                afterItem!.Version,
                secondTableId,
                "KELLNER-2",
                "KASSE-1");

            assert(
                moved.TableId == secondTableId &&
                moved.Version == afterItem.Version + 1 &&
                await repo.GetLiveSessionForTableAsync(tableId) is null,
                "Tisch umbuchen frees the source table and preserves the live session");

            var thirdTableId = await repo.SaveTableAsync(
                areaId,
                "T03",
                "Tisch 3",
                seats: 4,
                sortOrder: 3);

            var targetSession = await repo.OpenTableAsync(
                thirdTableId,
                "KELLNER-3",
                guestCount: 2,
                deviceId: "KASSE-1");

            var merged = await repo.MergeSessionsAsync(
                moved.Id,
                moved.Version,
                targetSession.Id,
                targetSession.Version,
                "ADMIN",
                "KASSE-1");

            var mergedItems = await repo.ListActiveItemsAsync(merged.Id);
            assert(
                merged.GuestCount == moved.GuestCount + targetSession.GuestCount &&
                mergedItems.Count == 1 &&
                mergedItems.Single().LineTotalCents == 2580 &&
                await repo.GetLiveSessionForTableAsync(secondTableId) is null,
                "Tische zusammenlegen moves open positions and releases the source table");

            await InsertRestaurantBestellungAsync(
                db,
                moved.Id,
                sequence: 2,
                kind: "AENDERUNG",
                product,
                quantityMilli: -2000);

            await InsertRestaurantBestellungAsync(
                db,
                targetSession.Id,
                sequence: 1,
                kind: "ANNAHME",
                product,
                quantityMilli: 2000);

            assert(
                await fiscalState.IsCurrentStateSecuredAsync(moved.Id) &&
                await fiscalState.IsCurrentStateSecuredAsync(targetSession.Id),
                "Balanced merge deltas reconcile both source and target Restaurant orders");

            await using (var c = db.OpenConnection())
            {
                using var partial = c.CreateCommand();
                partial.CommandText = """
                    UPDATE restaurant_session_items
                    SET quantity_milli=1000,version=version+1
                    WHERE session_id=$session AND state='ACTIVE';

                    INSERT INTO restaurant_session_items(
                        session_id,line_token,product_id,product_name,variant_name,
                        quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                        added_by,added_at,version)
                    VALUES($session,$token,$product,$name,'',1000,$price,$vat,0,'PAID','TEST',$now,1);
                    """;
                partial.Parameters.AddWithValue("$session", targetSession.Id);
                partial.Parameters.AddWithValue("$token", Guid.NewGuid().ToString("N"));
                partial.Parameters.AddWithValue("$product", product.Id);
                partial.Parameters.AddWithValue("$name", product.Name);
                partial.Parameters.AddWithValue("$price", product.BasePriceCents);
                partial.Parameters.AddWithValue("$vat", (double)product.VatRate);
                partial.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                partial.ExecuteNonQuery();
            }

            assert(
                await fiscalState.IsCurrentStateSecuredAsync(targetSession.Id),
                "Partial payment keeps ACTIVE plus PAID quantities reconciled with the original Bestellung");

            var payableItems = await repo.ListActiveItemsAsync(merged.Id);
            var payable = payableItems.Single();
            var paymentDraft = await repo.BuildCheckoutDraftAsync(
                merged.Id,
                merged.Version,
                new[]
                {
                    new RestaurantSplitSelection(
                        payable.Id,
                        payable.QuantityMilli)
                });

            var lockedVersion = await repo.PreparePaymentReservationAsync(
                paymentDraft);

            var lockedSession = await repo.GetSessionAsync(merged.Id);
            assert(
                lockedSession is not null &&
                lockedSession.State == RestaurantTableSessionState.CheckRequested &&
                lockedSession.Version == lockedVersion &&
                await repo.HasPreparedPaymentReservationAsync(paymentDraft.OperationId),
                "Prepared Restaurant payment durably locks the table before external payment");

            var writeWhilePaymentRejected = false;
            try
            {
                await repo.AddItemAsync(
                    merged.Id,
                    lockedVersion,
                    product,
                    1m,
                    "KELLNER-3");
            }
            catch (InvalidOperationException)
            {
                writeWhilePaymentRejected = true;
            }

            assert(
                writeWhilePaymentRejected,
                "Table edits are rejected while a Restaurant payment is unresolved");

            await repo.CancelPaymentReservationAsync(
                paymentDraft.OperationId);

            var reopenedAfterCancel = await repo.GetSessionAsync(merged.Id);
            assert(
                reopenedAfterCancel is not null &&
                reopenedAfterCancel.State == RestaurantTableSessionState.Open &&
                !await repo.HasPreparedPaymentReservationAsync(paymentDraft.OperationId),
                "No-charge cancellation reopens the Restaurant table and clears the payment lock");

            var kitchen = new RestaurantKitchenOutbox(db);
            var kitchenTableId = await repo.SaveTableAsync(
                areaId,
                "T06",
                "Tisch 6",
                seats: 4,
                sortOrder: 6);
            var kitchenSession = await repo.OpenTableAsync(
                kitchenTableId,
                "KELLNER-1",
                guestCount: 2);
            var kitchenItem = await repo.AddItemAsync(
                kitchenSession.Id,
                kitchenSession.Version,
                product,
                1m,
                "KELLNER-1");

            var kitchenSessionAfterItem =
                await repo.GetSessionAsync(kitchenSession.Id)
                ?? throw new InvalidOperationException("Kitchen test session missing.");

            var kitchenJobId = await kitchen.EnqueueNewItemAsync(
                kitchenSessionAfterItem,
                kitchenItem,
                "Tisch 6",
                "KELLNER-1",
                KitchenStations.Grill);

            var pendingKitchen = await kitchen.PendingAsync();
            assert(
                pendingKitchen.Any(x =>
                    x.Id == kitchenJobId &&
                    x.Action == "NEW" &&
                    x.Station == KitchenStations.Grill &&
                    x.State == "PENDING"),
                "Restaurant kitchen NEW job is durably queued with Warengruppe station snapshot");

            await kitchen.SetItemStatusAsync(
                kitchenItem.Id,
                "IN_ARBEIT",
                "KÜCHE");
            await kitchen.SetItemStatusAsync(
                kitchenItem.Id,
                "FERTIG",
                "KÜCHE");

            await kitchen.MarkHandedOverAsync(kitchenJobId);
            assert(
                !(await kitchen.PendingAsync()).Any(x => x.Id == kitchenJobId),
                "Handed-over Restaurant kitchen job leaves the pending queue");

            var notedSession = await repo.UpdateSessionDetailsAsync(
                kitchenSessionAfterItem.Id,
                kitchenSessionAfterItem.Version,
                guestCount: 2,
                note: "Ohne Salz",
                actor: "KELLNER-1");

            var noteJobId = await kitchen.EnqueueNoteAsync(
                notedSession,
                "Tisch 6",
                "KELLNER-1");

            assert(
                (await kitchen.PendingAsync()).Any(x =>
                    x.Id == noteJobId &&
                    x.Action == "NOTE" &&
                    x.SessionItemId is null),
                "Restaurant table note change is queued as a separate kitchen NOTE job");

            var failJobId = await kitchen.EnqueueNewItemAsync(
                kitchenSessionAfterItem,
                kitchenItem,
                "Tisch 6",
                "KELLNER-1");
            var parkedAsFailed = false;
            for (var i = 0; i < 5; i++)
                parkedAsFailed = await kitchen.MarkFailedAttemptAsync(
                    failJobId,
                    "Drucker nicht erreichbar");

            assert(
                parkedAsFailed &&
                !(await kitchen.PendingAsync()).Any(x => x.Id == failJobId),
                "Restaurant kitchen queue parks a job after five failed attempts");

            var cancelTableId = await repo.SaveTableAsync(
                areaId,
                "T05",
                "Tisch 5",
                seats: 2,
                sortOrder: 5);
            var cancelSession = await repo.OpenTableAsync(
                cancelTableId,
                "KELLNER-1",
                guestCount: 1);
            var cancelItem = await repo.AddItemAsync(
                cancelSession.Id,
                cancelSession.Version,
                product,
                1m,
                "KELLNER-1");

            await InsertRestaurantBestellungAsync(
                db,
                cancelSession.Id,
                sequence: 1,
                kind: "ANNAHME",
                product,
                quantityMilli: 1000);

            var cancelled = await repo.CancelItemAsync(
                cancelSession.Id,
                expectedSessionVersion: 2,
                cancelItem.Id,
                "KELLNER-1",
                "KASSE-1");

            await InsertRestaurantBestellungAsync(
                db,
                cancelSession.Id,
                sequence: 2,
                kind: "AENDERUNG",
                product,
                quantityMilli: -1000);

            assert(
                cancelled.State == RestaurantSessionItemState.Cancelled &&
                (await repo.ListActiveItemsAsync(cancelSession.Id)).Count == 0,
                "Restaurant position cancellation preserves the row as CANCELLED and removes it from active service");

            assert(
                await fiscalState.IsCurrentStateSecuredAsync(cancelSession.Id),
                "Negative Bestellung delta reconciles a cancelled Restaurant position to zero");

            var cancelledSessionForKitchen =
                await repo.GetSessionAsync(cancelSession.Id)
                ?? throw new InvalidOperationException("Cancelled session missing.");

            const string cancelKitchenJobId =
                "KITCHEN-CANCEL-IDEMPOTENCY-0001";

            await kitchen.EnqueueCancellationIdempotentAsync(
                cancelledSessionForKitchen,
                cancelled,
                "Tisch 5",
                "KELLNER-1",
                cancelKitchenJobId,
                KitchenStations.Grill);

            await kitchen.EnqueueCancellationIdempotentAsync(
                cancelledSessionForKitchen,
                cancelled,
                "Tisch 5",
                "KELLNER-1",
                cancelKitchenJobId,
                KitchenStations.Grill);

            var cancellationAlerts = await kitchen.CancellationAlertsAsync(
                KitchenStations.Grill);

            if (cancellationAlerts.Count(x =>
                    x.JobId == cancelKitchenJobId) != 1)
            {
                throw new InvalidOperationException(
                    "Restaurant cancellation retry must create exactly one durable kitchen CANCEL job.");
            }

            assert(
                cancellationAlerts.Any(x =>
                    x.JobId == cancelKitchenJobId &&
                    x.SessionItemId == cancelled.Id &&
                    x.TableName == "Tisch 5" &&
                    x.ProductName == product.Name),
                "Restaurant KDS surfaces a pending cancellation alert for the cancelled kitchen item");

            await kitchen.MarkHandedOverAsync(cancelKitchenJobId);

            assert(
                !(await kitchen.CancellationAlertsAsync(
                    KitchenStations.Grill)).Any(x =>
                        x.JobId == cancelKitchenJobId),
                "Acknowledged Restaurant KDS cancellation alert leaves the pending board");

            var emptyTableId = await repo.SaveTableAsync(
                areaId,
                "T04",
                "Tisch 4",
                seats: 2,
                sortOrder: 4);
            var emptySession = await repo.OpenTableAsync(
                emptyTableId,
                "KELLNER-1",
                guestCount: 1);

            var closedEmpty = await repo.CloseEmptySessionAsync(
                emptySession.Id,
                emptySession.Version,
                "KELLNER-1");

            assert(
                closedEmpty.State == RestaurantTableSessionState.Closed &&
                closedEmpty.ClosedAt is not null &&
                await repo.GetLiveSessionForTableAsync(emptyTableId) is null,
                "An empty Tischvorgang can be closed and releases the table");

            var guardedCloseRejected = false;
            try
            {
                await repo.CloseEmptySessionAsync(
                    merged.Id,
                    reopenedAfterCancel!.Version,
                    "KELLNER-3");
            }
            catch (InvalidOperationException ex)
            {
                guardedCloseRejected = ex.Message.Contains(
                    "Zahlungsweg",
                    StringComparison.OrdinalIgnoreCase);
            }

            assert(
                guardedCloseRejected,
                "A table with open positions cannot bypass checkout by closing directly");

            await using (var c = db.OpenConnection())
            {
                using var count = c.CreateCommand();
                count.CommandText =
                    "SELECT COUNT(*) FROM restaurant_session_events WHERE session_id=$id;";
                count.Parameters.AddWithValue("$id", session.Id);
                var eventCount = Convert.ToInt32(count.ExecuteScalar());

                var appendOnly = false;
                try
                {
                    using var tamper = c.CreateCommand();
                    tamper.CommandText =
                        "UPDATE restaurant_session_events SET actor='X' WHERE session_id=$id;";
                    tamper.Parameters.AddWithValue("$id", session.Id);
                    tamper.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    appendOnly = true;
                }

                assert(
                    eventCount >= 3 && appendOnly,
                    "Restaurant session events record changes and are append-only");
            }

            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                "KIOSK",
                EnvironmentVariableTarget.Process);

            var kioskPath = Path.Combine(root, "kiosk.db");
            var kioskDb = new SqliteDatabase(kioskPath);
            var kioskMigration = new SchemaMigrationService(
                kioskDb,
                new DatabaseBackupService(kioskDb),
                Path.Combine(root, "kiosk-migration-backups"));
            await kioskMigration.InitializeDatabaseAsync();

            await using (var c = kioskDb.OpenConnection())
            {
                assert(
                    !TableExists(c, "restaurant_tables") &&
                    !TableExists(c, "restaurant_sessions") &&
                    !TableExists(c, "restaurant_bestellungen") &&
                    !TableExists(c, "restaurant_kitchen_jobs") &&
                    !TableExists(c, "restaurant_handheld_devices") &&
                    !TableExists(c, "restaurant_reservations") &&
                    !TableExists(c, "restaurant_terminals") &&
                    !TableExists(c, "restaurant_device_commands"),
                    "Einzelhandel database does not receive Restaurant-only tables");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "TOR_POS_PRODUCT_EDITION",
                oldEdition,
                EnvironmentVariableTarget.Process);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class FakeCommercialLicenseService : ICommercialLicenseService
    {
        private readonly CommercialLicenseStatus _status;

        public FakeCommercialLicenseService(CommercialLicenseStatus status)
        {
            _status = status;
        }

        public string InstallationId => "TEST";
        public string DeviceCode => "TEST";
        public string LicenseFilePath => "";

        public CommercialLicenseStatus Check(string edition) => _status;
        public CommercialLicenseStatus Import(string sourcePath, string edition) => _status;
        public CommercialLicenseStatus Deactivate(
            string edition,
            string deactivatedBy,
            string receiptTargetPath) => _status;

        public void ExportActivationRequest(
            string targetPath,
            string edition,
            string customerNumber,
            string customerName,
            string productVersion)
        {
        }
    }

    private static async Task InsertRestaurantBestellungAsync(
        SqliteDatabase db,
        string sessionId,
        int sequence,
        string kind,
        Product product,
        long quantityMilli)
    {
        await using var c = db.OpenConnection();
        await using var tx = c.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");

        long id;
        await using (var head = c.CreateCommand())
        {
            head.Transaction = tx;
            head.CommandText = """
                INSERT INTO restaurant_bestellungen(
                    session_id,sequence,kind,started_at,created_at,operator_name,total_cents,
                    client_id,transaction_number,signature_counter,serial_number,signature,
                    start_log_time,log_time,outage,outage_reason)
                VALUES($session,$sequence,$kind,$now,$now,'TEST',$total,
                    '','','','','','','',1,'TEST')
                RETURNING id;
                """;
            head.Parameters.AddWithValue("$session", sessionId);
            head.Parameters.AddWithValue("$sequence", sequence);
            head.Parameters.AddWithValue("$kind", kind);
            head.Parameters.AddWithValue("$now", now);
            head.Parameters.AddWithValue(
                "$total",
                (long)Math.Round(
                    (quantityMilli / 1000m) * product.BasePriceCents,
                    MidpointRounding.AwayFromZero));
            id = Convert.ToInt64(await head.ExecuteScalarAsync());
        }

        await using (var item = c.CreateCommand())
        {
            item.Transaction = tx;
            item.CommandText = """
                INSERT INTO restaurant_bestellung_items(
                    bestellung_id,product_id,product_name,quantity_milli,
                    unit_price_cents,vat_rate,pfand_cents)
                VALUES($b,$product,$name,$quantity,$price,$vat,0);
                """;
            item.Parameters.AddWithValue("$b", id);
            item.Parameters.AddWithValue("$product", product.Id);
            item.Parameters.AddWithValue("$name", product.Name);
            item.Parameters.AddWithValue("$quantity", quantityMilli);
            item.Parameters.AddWithValue("$price", product.BasePriceCents);
            item.Parameters.AddWithValue("$vat", (double)product.VatRate);
            await item.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static bool TableExists(SqliteConnection c, string name)
    {
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        q.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(q.ExecuteScalar()) == 1;
    }
}
