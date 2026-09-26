using Microsoft.Data.Sqlite;
using TorPos.Application;
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
            LoginWindow.NormalizeLockedEdition("restaurant") == "RESTAURANT",
            "Dedicated TOR Restaurant build remains locked to RESTAURANT at login");

        assert(
            LoginWindow.NormalizeLockedEdition("unknown") is null,
            "Login edition lock rejects unknown product identities");

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

        var testLanAddresses = new[]
        {
            System.Net.IPAddress.Parse("8.8.8.8"),
            System.Net.IPAddress.Loopback,
            System.Net.IPAddress.Parse("192.168.44.21"),
            System.Net.IPAddress.Parse("10.10.0.7")
        };

        assert(
            RestaurantLocalApiHost.ResolveLanBindAddress(
                "AUTO_PRIVATE",
                testLanAddresses).Equals(
                    System.Net.IPAddress.Parse("10.10.0.7")),
            "G-2 Restaurant API AUTO_PRIVATE selects only a private LAN IPv4 and never ListenAnyIP/public/loopback");

        var nonLocalPrivateRejected = false;
        try
        {
            _ = RestaurantLocalApiHost.ResolveLanBindAddress(
                "192.168.99.10",
                testLanAddresses);
        }
        catch (InvalidOperationException)
        {
            nonLocalPrivateRejected = true;
        }

        assert(
            nonLocalPrivateRejected,
            "G-2 Restaurant API rejects a private bind address that is not present on a local interface");

        var unsafeBindRejected = false;
        try
        {
            _ = RestaurantLocalApiHost.ResolveLanBindAddress(
                "8.8.8.8",
                testLanAddresses);
        }
        catch (InvalidOperationException)
        {
            try
            {
                _ = RestaurantLocalApiHost.ResolveLanBindAddress(
                    "127.0.0.1",
                    testLanAddresses);
            }
            catch (InvalidOperationException)
            {
                unsafeBindRejected = true;
            }
        }

        assert(
            unsafeBindRejected,
            "G-2 Restaurant API rejects public and loopback bind addresses");

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

        var restaurantOtherOrderRecord =
            restaurantOrderRecord with
            {
                Id = 901,
                CustomBonId = "RB-901",
                CustomAllocationGroup = "Restaurant session-901"
            };

        var restaurantStornoRecord =
            restaurantOrderRecord with
            {
                Id = 902,
                Sequence = 2,
                Kind = OrderBestellungKind.Storno,
                CreatedAt = exportTime.AddMinutes(2),
                Lines = new[]
                {
                    new CartLine(
                        restaurantOrderRecord.Lines.Single())
                    {
                        Quantity = -1m
                    }
                },
                CustomBonId = "RB-902",
                CustomAllocationGroup = "Restaurant session-900"
            };

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
                OrderRecords = new[]
                {
                    restaurantOtherOrderRecord,
                    restaurantOrderRecord,
                    restaurantStornoRecord
                }
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

        assert(
            exportRows.For("Bon_Referenzen").Any(
                row =>
                    string.Equals(
                        Convert.ToString(row["BON_ID"]),
                        "RB-902",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        Convert.ToString(row["REF_BON_ID"]),
                        "RB-900",
                        StringComparison.Ordinal)),
            "K-4 Restaurant Storno references the acceptance RB identity of the same session instead of BE-0-1");

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

        var selfOrderWithoutPlus =
            new RestaurantEntitlementService(
                new FakeCommercialLicenseService(
                    new CommercialLicenseStatus(
                        CommercialLicenseState.Active,
                        "TEST",
                        Features: new[]
                        {
                            RestaurantEntitlementService.SelfOrderFeatureCode
                        })));

        var selfOrderEntitlements =
            new RestaurantEntitlementService(
                new FakeCommercialLicenseService(
                    new CommercialLicenseStatus(
                        CommercialLicenseState.Active,
                        "TEST",
                        Features: new[]
                        {
                            RestaurantEntitlementService.PlusFeatureCode,
                            RestaurantEntitlementService.SelfOrderFeatureCode
                        })));

        assert(
            RestaurantProductFeatures.IsAddOn(
                RestaurantFeature.QrTischbestellung) &&
            !RestaurantProductFeatures.Includes(
                RestaurantProductTier.RestaurantPlus,
                RestaurantFeature.QrTischbestellung),
            "Self Order stays a separately licensed add-on and is not silently bundled into Restaurant Plus");

        assert(
            !plusEntitlements.IsEnabled(
                RestaurantFeature.QrTischbestellung),
            "Restaurant Plus alone does not unlock the paid Self Order add-on");

        assert(
            !selfOrderWithoutPlus.IsEnabled(
                RestaurantFeature.QrTischbestellung),
            "Self Order add-on code without Restaurant Plus does not unlock QR ordering");

        assert(
            selfOrderEntitlements.IsEnabled(
                RestaurantFeature.QrTischbestellung),
            "Restaurant Plus plus RESTAURANT_SELF_ORDER unlocks QR table ordering");

        var selfOrderQr =
            RestaurantSelfOrderSecurity.CreateTableQrSecret();

        assert(
            selfOrderQr.PublicToken.Length >= 40 &&
            selfOrderQr.TokenHash.Length == 64 &&
            !string.Equals(
                selfOrderQr.PublicToken,
                selfOrderQr.TokenHash,
                StringComparison.Ordinal) &&
            RestaurantSelfOrderSecurity.VerifyToken(
                selfOrderQr.PublicToken,
                selfOrderQr.TokenHash),
            "Self Order QR creates a high-entropy public token while persistence can keep only its SHA-256 hash");

        assert(
            !RestaurantSelfOrderSecurity.VerifyToken(
                selfOrderQr.PublicToken + "x",
                selfOrderQr.TokenHash),
            "Self Order QR token verification rejects a modified table token");

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
                result.ToVersion == SchemaMigrationService.TargetSchemaVersion,
                $"Restaurant database reaches current schema version {SchemaMigrationService.TargetSchemaVersion}");

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
                    TableExists(c, "restaurant_device_commands") &&
                    TableExists(c, "restaurant_operator_sessions") &&
                    TableExists(c, "restaurant_self_order_tables") &&
                    TableExists(c, "restaurant_self_order_sessions"),
                    "Restaurant-only tables including Bestellung, kitchen, reservations, terminal, device-command and Self Order records are created for the Restaurant product");

                assert(
                    ColumnExists(c, "restaurant_session_items", "fiscal_state"),
                    "K-4 Restaurant schema tracks PENDING versus SECURED fiscal item state");

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
                pairingCode.Id,
                pairingCode.Code,
                "DEVICE-001",
                "Handheld 1");

            await pairing.RequireAuthenticatedAsync(
                paired.DeviceId,
                paired.DeviceToken);

            string lastSeenAfterFirstAuth;
            await using (var authRead = db.OpenConnection())
            {
                await using var q = authRead.CreateCommand();
                q.CommandText =
                    "SELECT last_seen_at FROM restaurant_handheld_devices WHERE device_id=$id;";
                q.Parameters.AddWithValue("$id", paired.DeviceId);
                lastSeenAfterFirstAuth =
                    Convert.ToString(
                        await q.ExecuteScalarAsync()) ?? "";
            }

            await Task.WhenAll(
                Enumerable.Range(0, 20)
                    .Select(_ =>
                        pairing.RequireAuthenticatedAsync(
                            paired.DeviceId,
                            paired.DeviceToken)));

            string lastSeenAfterPollingBurst;
            await using (var authRead = db.OpenConnection())
            {
                await using var q = authRead.CreateCommand();
                q.CommandText =
                    "SELECT last_seen_at FROM restaurant_handheld_devices WHERE device_id=$id;";
                q.Parameters.AddWithValue("$id", paired.DeviceId);
                lastSeenAfterPollingBurst =
                    Convert.ToString(
                        await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                lastSeenAfterPollingBurst ==
                    lastSeenAfterFirstAuth,
                "Repeated Restaurant handheld authentication within the heartbeat window does not create a database write storm");

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
                    pairingCode.Id,
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

            var lockedPairingCode =
                await pairing.CreatePairingCodeAsync(
                    "ADMIN",
                    TimeSpan.FromMinutes(5));

            var pairingLockedAfterFive = false;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    await pairing.PairAsync(
                        lockedPairingCode.Id,
                        "000000" == lockedPairingCode.Code
                            ? "000001"
                            : "000000",
                        "DEVICE-BRUTE",
                        "Brute Test");
                }
                catch (InvalidOperationException)
                {
                    if (attempt == 5)
                        pairingLockedAfterFive = true;
                }
            }

            var correctCodeRejectedAfterLock = false;
            try
            {
                await pairing.PairAsync(
                    lockedPairingCode.Id,
                    lockedPairingCode.Code,
                    "DEVICE-BRUTE",
                    "Brute Test");
            }
            catch (InvalidOperationException)
            {
                correctCodeRejectedAfterLock = true;
            }

            assert(
                pairingLockedAfterFive &&
                correctCodeRejectedAfterLock,
                "G-2 pairing session locks durably after five wrong codes and cannot be revived with the correct code");

            var overwriteCode =
                await pairing.CreatePairingCodeAsync(
                    "ADMIN",
                    TimeSpan.FromMinutes(5));

            var deviceOverwriteRejected = false;
            try
            {
                await pairing.PairAsync(
                    overwriteCode.Id,
                    overwriteCode.Code,
                    paired.DeviceId,
                    "Hijacked Handheld");
            }
            catch (InvalidOperationException)
            {
                deviceOverwriteRejected = true;
            }

            assert(
                deviceOverwriteRejected,
                "G-2 pairing cannot overwrite or silently reactivate an existing Restaurant device identity");

            var operatorAuth =
                new AuthenticationService(db);
            await operatorAuth.InitializeAsync();

            var operatorStaff =
                (await operatorAuth.GetStaffUsersAsync()).First();
            await operatorAuth.SaveStaffUserAsync(
                new StaffUserUpdate(
                    operatorStaff.Id,
                    "kellner1",
                    true,
                    UserPermissions.Sale,
                    "",
                    "4826"),
                "admin");

            var operatorSessions =
                new RestaurantOperatorSessionService(
                    db,
                    plusEntitlements,
                    operatorAuth);

            var operatorLogin =
                await operatorSessions.LoginAsync(
                    paired.DeviceId,
                    "kellner1",
                    "4826");

            string storedOperatorTokenHash;
            await using (var operatorRead = db.OpenReadConnection())
            {
                await using var q = operatorRead.CreateCommand();
                q.CommandText = """
                    SELECT token_hash
                    FROM restaurant_operator_sessions
                    WHERE device_id=$device
                      AND username='kellner1'
                      AND revoked_at IS NULL
                    LIMIT 1;
                    """;
                q.Parameters.AddWithValue(
                    "$device",
                    paired.DeviceId);
                storedOperatorTokenHash =
                    Convert.ToString(
                        await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                operatorLogin.SessionToken.Length == 64 &&
                storedOperatorTokenHash.Length == 64 &&
                !string.Equals(
                    operatorLogin.SessionToken,
                    storedOperatorTokenHash,
                    StringComparison.Ordinal),
                "Restaurant operator session stores only a SHA-256 token hash, never the raw shift token");

            var authenticatedOperator =
                await operatorSessions.RequireAsync(
                    paired.DeviceId,
                    operatorLogin.SessionToken);

            assert(
                authenticatedOperator.Username == "kellner1" &&
                !authenticatedOperator.IsAdmin &&
                authenticatedOperator.Can(
                    UserPermissions.Sale),
                "Restaurant operator session resolves a non-admin canonical POS waiter with Sale permission");

            var g1Handheld = new RestaurantHandheldService(
                plusEntitlements,
                null!,
                null!,
                null!,
                null!,
                pairing,
                operatorSessions,
                null!,
                null!,
                null!);

            var invalidHandheldQuantityRejected = false;
            try
            {
                await g1Handheld.AddItemAsync(
                    new RestaurantHandheldAddItemRequest(
                        "G2-QUANTITY",
                        1,
                        1,
                        1.5m,
                        "kellner1",
                        "",
                        paired.DeviceId,
                        paired.DeviceToken,
                        "G2-QUANTITY-0001",
                        operatorLogin.SessionToken));
            }
            catch (ArgumentOutOfRangeException)
            {
                invalidHandheldQuantityRejected = true;
            }

            assert(
                invalidHandheldQuantityRejected,
                "G-2 handheld rejects fractional Stück quantity before catalog or database mutation");

            var excessiveHandheldQuantityRejected = false;
            try
            {
                await g1Handheld.AddItemAsync(
                    new RestaurantHandheldAddItemRequest(
                        "G2-QUANTITY",
                        1,
                        1,
                        100m,
                        "kellner1",
                        "",
                        paired.DeviceId,
                        paired.DeviceToken,
                        "G2-QUANTITY-0002",
                        operatorLogin.SessionToken));
            }
            catch (ArgumentOutOfRangeException)
            {
                excessiveHandheldQuantityRejected = true;
            }

            assert(
                excessiveHandheldQuantityRejected,
                "G-2 handheld bounds Stück quantity to 1..99 before multiplication or database mutation");

            var unauthorizedStornoRejected = false;
            try
            {
                await g1Handheld.CancelItemAsync(
                    new RestaurantHandheldCancelItemRequest(
                        "G1-SESSION",
                        1,
                        1,
                        "kellner1",
                        "",
                        paired.DeviceId,
                        paired.DeviceToken,
                        "G1-CANCEL-0001",
                        operatorLogin.SessionToken,
                        "Fehlbuchung"));
            }
            catch (UnauthorizedAccessException)
            {
                unauthorizedStornoRejected = true;
            }

            assert(
                unauthorizedStornoRejected,
                "G-1 handheld Restaurant storno requires ImmediateStorno even with a valid paired device and operator session");

            await operatorAuth.SaveStaffUserAsync(
                new StaffUserUpdate(
                    operatorStaff.Id,
                    "kellner1",
                    true,
                    UserPermissions.Sale |
                    UserPermissions.ImmediateStorno,
                    "",
                    "4826"),
                "admin");

            var stornoLogin =
                await operatorAuth.LoginWithPinAsync(
                    "kellner1",
                    "4826");

            assert(
                stornoLogin.Success &&
                stornoLogin.User is not null &&
                stornoLogin.User.Can(
                    UserPermissions.ImmediateStorno),
                "G-1 Restaurant storno permission is resolved from the canonical POS operator identity");

            var wrongDeviceRejected = false;
            try
            {
                await operatorSessions.RequireAsync(
                    "DEVICE-OTHER",
                    operatorLogin.SessionToken);
            }
            catch (UnauthorizedAccessException)
            {
                wrongDeviceRejected = true;
            }

            assert(
                wrongDeviceRejected,
                "Restaurant operator session token is bound to its paired device");

            await operatorSessions.LogoutAsync(
                paired.DeviceId,
                operatorLogin.SessionToken);

            var loggedOutRejected = false;
            try
            {
                await operatorSessions.RequireAsync(
                    paired.DeviceId,
                    operatorLogin.SessionToken);
            }
            catch (UnauthorizedAccessException)
            {
                loggedOutRejected = true;
            }

            assert(
                loggedOutRejected,
                "Restaurant operator logout revokes the shift token immediately");

            var stornoOperatorSession =
                await operatorSessions.LoginAsync(
                    paired.DeviceId,
                    "kellner1",
                    "4826");

            var missingOperatorSessionRejected = false;
            try
            {
                await g1Handheld.CancelItemAsync(
                    new RestaurantHandheldCancelItemRequest(
                        "G2A-SESSION",
                        1,
                        1,
                        "kellner1",
                        "4826",
                        paired.DeviceId,
                        paired.DeviceToken,
                        "G2A-CANCEL-0001",
                        "",
                        "Fehlbuchung"));
            }
            catch (UnauthorizedAccessException)
            {
                missingOperatorSessionRejected = true;
            }

            assert(
                missingOperatorSessionRejected,
                "G-2 Restaurant mutation rejects device-authenticated requests without an operator session token");

            var missingReasonRejected = false;
            try
            {
                await g1Handheld.CancelItemAsync(
                    new RestaurantHandheldCancelItemRequest(
                        "G1-SESSION",
                        1,
                        1,
                        "kellner1",
                        "",
                        paired.DeviceId,
                        paired.DeviceToken,
                        "G1-CANCEL-0002",
                        stornoOperatorSession.SessionToken,
                        ""));
            }
            catch (ArgumentException ex)
                when (ex.Message.Contains(
                    "Stornogrund",
                    StringComparison.OrdinalIgnoreCase))
            {
                missingReasonRejected = true;
            }

            assert(
                missingReasonRejected,
                "G-1 handheld Restaurant storno rejects a blank reason before command or repository mutation");

            await operatorSessions.LogoutAsync(
                paired.DeviceId,
                stornoOperatorSession.SessionToken);

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

            var terminalTypeChangeRejected = false;
            try
            {
                await terminals.RegisterOrHeartbeatAsync(
                    "KASSE-2",
                    "Manipuliertes Gerät",
                    "HANDHELD",
                    "R190",
                    "SERVER-2");
            }
            catch (UnauthorizedAccessException)
            {
                terminalTypeChangeRejected = true;
            }

            assert(
                terminalTypeChangeRejected,
                "G-2 registered Restaurant terminal type cannot be escalated or changed by heartbeat");

            await terminals.RequireTypeAsync(
                "KASSE-2",
                new[] { "KASSE" });

            var terminalScopeRejected = false;
            try
            {
                await terminals.RequireTypeAsync(
                    "KASSE-2",
                    new[] { "HANDHELD" });
            }
            catch (UnauthorizedAccessException)
            {
                terminalScopeRejected = true;
            }

            assert(
                terminalScopeRejected,
                "G-2 Restaurant terminal scope guard rejects an endpoint role not assigned to the device");

            var terminalBeforeCoalesce =
                terminalRows.Single(x =>
                    x.TerminalId == "KASSE-2");

            await terminals.RegisterOrHeartbeatAsync(
                "KASSE-2",
                "Kasse 2 Neu",
                "KASSE",
                "R190",
                "SERVER-2");

            var terminalAfterCoalesce =
                (await terminals.ListAsync())
                    .Single(x => x.TerminalId == "KASSE-2");

            if (!terminalAfterCoalesce.IsOnline ||
                terminalAfterCoalesce.LastSeenAt !=
                    terminalBeforeCoalesce.LastSeenAt)
            {
                throw new InvalidOperationException(
                    "Restaurant terminal heartbeat must stay online while coalescing redundant high-frequency database writes.");
            }

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

            // One table, one party at a time: an overlapping reservation for
            // the same table is refused on create and on table assignment.
            async Task<bool> Refused(Func<Task> action)
            {
                try { await action(); return false; }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Tisch ist in diesem Zeitraum bereits reserviert", StringComparison.Ordinal)) { return true; }
            }
            var overlapCreate = await Refused(() => reservations.CreateAsync(
                reservationAt.AddHours(1), 60, 2, "Überschneidung", "", "", tableId, "ADMIN"));
            var adjacent = await reservations.CreateAsync(
                reservationAt.AddHours(2), 90, 2, "Direkt danach", "", "", tableId, "ADMIN");
            var unassigned = await reservations.CreateAsync(
                reservationAt.AddHours(3), 60, 2, "Ohne Tisch", "", "", null, "ADMIN");
            var overlapAssign = await Refused(() => reservations.AssignTableAsync(
                unassigned.Id, unassigned.Version, tableId, "ADMIN"));
            await reservations.SetStatusAsync(adjacent.Id, adjacent.Version, "CANCELLED", "ADMIN");
            var assignedAfterCancel = await reservations.AssignTableAsync(
                unassigned.Id, unassigned.Version, tableId, "ADMIN");
            // Tisch 1 has 4 seats: a party of 8 is refused on create and on
            // assignment, and can still be booked without a table.
            async Task<bool> TooMany(Func<Task> action)
            {
                try { await action(); return false; }
                catch (InvalidOperationException ex) when (ex.Message.Contains("hat 4 Plätze, die Reservierung 8 Gäste", StringComparison.Ordinal)) { return true; }
            }
            var bigCreate = await TooMany(() => reservations.CreateAsync(
                reservationAt.AddDays(1), 120, 8, "Große Runde", "", "", tableId, "ADMIN"));
            var bigParty = await reservations.CreateAsync(
                reservationAt.AddDays(1), 120, 8, "Große Runde", "", "", null, "ADMIN");
            var bigAssign = await TooMany(() => reservations.AssignTableAsync(
                bigParty.Id, bigParty.Version, tableId, "ADMIN"));
            assert(
                bigCreate && bigAssign && bigParty.TableId is null,
                "Restaurant reservation: a party larger than the table's seats is refused on create and on table assignment, and can be booked without a table");

            assert(
                overlapCreate && adjacent.TableId == tableId && overlapAssign &&
                assignedAfterCancel.TableId == tableId,
                "Restaurant reservation: an overlapping BOOKED/SEATED reservation keeps the table - a second one is refused on create and on table assignment; back-to-back and cancelled reservations do not block");

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

            var selfOrder = new RestaurantSelfOrderService(
                db,
                repo,
                selfOrderEntitlements);

            var tableQr = await selfOrder.RotateTableQrAsync(
                tableId,
                "ADMIN");

            string storedTableTokenHash;
            await using (var c = db.OpenReadConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT token_hash
                    FROM restaurant_self_order_tables
                    WHERE table_id=$table;
                    """;
                q.Parameters.AddWithValue("$table", tableId);
                storedTableTokenHash =
                    Convert.ToString(
                        await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                storedTableTokenHash.Length == 64 &&
                !string.Equals(
                    storedTableTokenHash,
                    tableQr.PublicToken,
                    StringComparison.Ordinal) &&
                RestaurantSelfOrderSecurity.VerifyToken(
                    tableQr.PublicToken,
                    storedTableTokenHash),
                "Self Order persists only the table QR hash and never the printable raw table token");

            var selfCapability =
                await selfOrder.ActivateSessionAsync(
                    session.Id,
                    RestaurantSelfOrderApprovalMode.ConfirmationRequired,
                    "ADMIN",
                    TimeSpan.FromHours(4));

            string storedCapabilityHash;
            await using (var c = db.OpenReadConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT capability_hash
                    FROM restaurant_self_order_sessions
                    WHERE session_id=$session;
                    """;
                q.Parameters.AddWithValue("$session", session.Id);
                storedCapabilityHash =
                    Convert.ToString(
                        await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                storedCapabilityHash.Length == 64 &&
                !string.Equals(
                    storedCapabilityHash,
                    selfCapability.CapabilitySecret,
                    StringComparison.Ordinal) &&
                selfCapability.TableId == tableId &&
                selfCapability.ExpiresAt > DateTimeOffset.UtcNow,
                "Self Order active table-session stores only a capability hash and returns a bounded raw capability once");

            var selfOrderValid =
                await selfOrder.ValidateOrderCapabilityAsync(
                    tableQr.PublicToken,
                    selfCapability.PublicSessionId,
                    selfCapability.CapabilitySecret);

            assert(
                selfOrderValid.Valid &&
                selfOrderValid.SessionId == session.Id &&
                selfOrderValid.TableId == tableId &&
                selfOrderValid.ApprovalMode ==
                    RestaurantSelfOrderApprovalMode.ConfirmationRequired,
                "Self Order requires the matching static table QR and active table-session capability together");

            var selfOrderWrongSecret =
                await selfOrder.ValidateOrderCapabilityAsync(
                    tableQr.PublicToken,
                    selfCapability.PublicSessionId,
                    selfCapability.CapabilitySecret + "x");

            assert(
                !selfOrderWrongSecret.Valid,
                "Self Order static table QR alone cannot authorize an order with a wrong session capability");

            await selfOrder.CloseSessionAsync(
                session.Id);

            var selfOrderAfterClose =
                await selfOrder.ValidateOrderCapabilityAsync(
                    tableQr.PublicToken,
                    selfCapability.PublicSessionId,
                    selfCapability.CapabilitySecret);

            assert(
                !selfOrderAfterClose.Valid,
                "Closing a Self Order table-session capability immediately invalidates subsequent customer ordering");

            var selfOrderSecurityTableId =
                await repo.SaveTableAsync(
                    areaId,
                    "TSOSEC",
                    "Self Order Sicherheit",
                    seats: 2,
                    sortOrder: 92);

            var selfOrderSecuritySession =
                await repo.OpenTableAsync(
                    selfOrderSecurityTableId,
                    "SELF-ORDER-TEST",
                    guestCount: 1,
                    deviceId: "KASSE-SELF");

            var firstSecurityQr =
                await selfOrder.RotateTableQrAsync(
                    selfOrderSecurityTableId,
                    "ADMIN");

            var securityCapability =
                await selfOrder.ActivateSessionAsync(
                    selfOrderSecuritySession.Id,
                    RestaurantSelfOrderApprovalMode.Automatic,
                    "ADMIN",
                    TimeSpan.FromHours(1));

            var rotatedSecurityQr =
                await selfOrder.RotateTableQrAsync(
                    selfOrderSecurityTableId,
                    "ADMIN");

            var oldQrValidation =
                await selfOrder.ValidateOrderCapabilityAsync(
                    firstSecurityQr.PublicToken,
                    securityCapability.PublicSessionId,
                    securityCapability.CapabilitySecret);

            var rotatedQrValidation =
                await selfOrder.ValidateOrderCapabilityAsync(
                    rotatedSecurityQr.PublicToken,
                    securityCapability.PublicSessionId,
                    securityCapability.CapabilitySecret);

            assert(
                !oldQrValidation.Valid &&
                rotatedQrValidation.Valid,
                "Self Order QR rotation invalidates the previously printed table QR without invalidating the current table-session capability");

            var closedSecuritySession =
                await repo.CloseEmptySessionAsync(
                    selfOrderSecuritySession.Id,
                    selfOrderSecuritySession.Version,
                    "ADMIN",
                    "KASSE-SELF");

            var validationAfterRealTableClose =
                await selfOrder.ValidateOrderCapabilityAsync(
                    rotatedSecurityQr.PublicToken,
                    securityCapability.PublicSessionId,
                    securityCapability.CapabilitySecret);

            assert(
                closedSecuritySession.State ==
                    RestaurantTableSessionState.Closed &&
                !validationAfterRealTableClose.Valid,
                "Self Order capability becomes invalid when the real Restaurant table session closes even without a separate Self Order close call");

            var inboxProduct = new Product
            {
                Id = 910001,
                Name = "Self Order Testartikel",
                BasePriceCents = 790,
                PfandCents = 25,
                VatRate = 7m,
                Unit = "Stück",
                IsActive = true
            };

            var inboxCatalog =
                new FakeProductCatalog(inboxProduct);

            var inboxService =
                new RestaurantSelfOrderInboxService(
                    db,
                    selfOrderEntitlements,
                    selfOrder,
                    inboxCatalog);

            var inboxTableId =
                await repo.SaveTableAsync(
                    areaId,
                    "TSOINBOX",
                    "Self Order Inbox",
                    seats: 4,
                    sortOrder: 93);

            var inboxSession =
                await repo.OpenTableAsync(
                    inboxTableId,
                    "SELF-ORDER-TEST",
                    guestCount: 2,
                    deviceId: "KASSE-SELF");

            var inboxQr =
                await selfOrder.RotateTableQrAsync(
                    inboxTableId,
                    "ADMIN");

            var inboxCapability =
                await selfOrder.ActivateSessionAsync(
                    inboxSession.Id,
                    RestaurantSelfOrderApprovalMode.ConfirmationRequired,
                    "ADMIN",
                    TimeSpan.FromHours(1));

            var receivedOrder =
                await inboxService.ReceiveAsync(
                    inboxQr.PublicToken,
                    inboxCapability.PublicSessionId,
                    inboxCapability.CapabilitySecret,
                    "client-order-0001",
                    new[]
                    {
                        new RestaurantSelfOrderLineRequest(
                            inboxProduct.Id,
                            2)
                    },
                    "Ohne Eis");

            long storedUnitPrice;
            long storedLineTotal;
            long storedOrderTotal;
            long storedPfand;
            decimal storedVat;

            await using (var c = db.OpenReadConnection())
            {
                await using var q = c.CreateCommand();
                q.CommandText = """
                    SELECT
                        i.unit_price_cents,
                        i.line_total_cents,
                        o.total_cents,
                        i.pfand_cents,
                        i.vat_rate
                    FROM restaurant_self_order_orders o
                    JOIN restaurant_self_order_order_items i
                      ON i.order_id=o.id
                    WHERE o.id=$id;
                    """;
                q.Parameters.AddWithValue(
                    "$id",
                    receivedOrder.OrderId);

                await using var r =
                    await q.ExecuteReaderAsync();

                if (!await r.ReadAsync())
                    throw new InvalidOperationException(
                        "Self Order inbox snapshot missing.");

                storedUnitPrice = r.GetInt64(0);
                storedLineTotal = r.GetInt64(1);
                storedOrderTotal = r.GetInt64(2);
                storedPfand = r.GetInt64(3);
                storedVat = Convert.ToDecimal(r.GetDouble(4));
            }

            assert(
                receivedOrder.State ==
                    RestaurantSelfOrderOrderState.Received &&
                !receivedOrder.Replay &&
                storedUnitPrice == 815 &&
                storedLineTotal == 1630 &&
                storedOrderTotal == 1630 &&
                storedPfand == 25 &&
                storedVat == 7m,
                "Self Order inbox snapshots price, VAT and Pfand exclusively from the POS catalog instead of customer-supplied values");

            var inboxLineTamperRejected = false;
            try
            {
                await using var c = db.OpenConnection();
                await using var q = c.CreateCommand();
                q.CommandText = """
                    UPDATE restaurant_self_order_order_items
                    SET unit_price_cents=1
                    WHERE order_id=$id;
                    """;
                q.Parameters.AddWithValue(
                    "$id",
                    receivedOrder.OrderId);
                await q.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                inboxLineTamperRejected = true;
            }

            assert(
                inboxLineTamperRejected,
                "Self Order RECEIVED line snapshots are immutable after persistence");

            inboxProduct.BasePriceCents = 990;
            inboxProduct.IsActive = false;

            var replayedOrder =
                await inboxService.ReceiveAsync(
                    inboxQr.PublicToken,
                    inboxCapability.PublicSessionId,
                    inboxCapability.CapabilitySecret,
                    "client-order-0001",
                    new[]
                    {
                        new RestaurantSelfOrderLineRequest(
                            inboxProduct.Id,
                            2)
                    },
                    "Ohne Eis");

            assert(
                replayedOrder.Replay &&
                replayedOrder.OrderId ==
                    receivedOrder.OrderId &&
                replayedOrder.PublicOrderId ==
                    receivedOrder.PublicOrderId,
                "Self Order network retry stays idempotent even if catalog price or active state changes after the first accepted inbox write");

            var conflictingReplayRejected = false;
            try
            {
                await inboxService.ReceiveAsync(
                    inboxQr.PublicToken,
                    inboxCapability.PublicSessionId,
                    inboxCapability.CapabilitySecret,
                    "client-order-0001",
                    new[]
                    {
                        new RestaurantSelfOrderLineRequest(
                            inboxProduct.Id,
                            1)
                    },
                    "Ohne Eis");
            }
            catch (InvalidOperationException)
            {
                conflictingReplayRejected = true;
            }

            assert(
                conflictingReplayRejected,
                "Self Order rejects reuse of a client-order id with changed order content");

            var invalidCapabilityRejected = false;
            try
            {
                await inboxService.ReceiveAsync(
                    inboxQr.PublicToken,
                    inboxCapability.PublicSessionId,
                    inboxCapability.CapabilitySecret + "x",
                    "client-order-0002",
                    new[]
                    {
                        new RestaurantSelfOrderLineRequest(
                            inboxProduct.Id,
                            1)
                    });
            }
            catch (UnauthorizedAccessException)
            {
                invalidCapabilityRejected = true;
            }

            assert(
                invalidCapabilityRejected,
                "Self Order inbox rejects an invalid session capability before creating a RECEIVED order");

            // Hold the global mutation lane so close is admitted before receive.
            // Before the race fix, ReceiveAsync validated outside IoQueue while
            // the close was still waiting, then persisted after the close.
            inboxProduct.IsActive = true;
            var raceQueueEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var raceQueueRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var raceBlocker = IoQueue.RunAsync(async () =>
            {
                raceQueueEntered.TrySetResult();
                await raceQueueRelease.Task;
            });

            await raceQueueEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(3));

            var closeBeforeReceive =
                selfOrder.CloseSessionAsync(
                    inboxSession.Id);

            const string raceClientOrderId =
                "client-order-race-0003";
            var receiveAfterQueuedClose =
                inboxService.ReceiveAsync(
                    inboxQr.PublicToken,
                    inboxCapability.PublicSessionId,
                    inboxCapability.CapabilitySecret,
                    raceClientOrderId,
                    new[]
                    {
                        new RestaurantSelfOrderLineRequest(
                            inboxProduct.Id,
                            1)
                    });

            // Give the pre-fix outside-queue validation a chance to finish
            // while the close is deliberately still blocked.
            await Task.Delay(100);
            raceQueueRelease.TrySetResult();
            await raceBlocker;
            await closeBeforeReceive;

            var racedReceiveRejected = false;
            try
            {
                await receiveAfterQueuedClose;
            }
            catch (UnauthorizedAccessException)
            {
                racedReceiveRejected = true;
            }

            long racedOrderCount;
            await using (var c = db.OpenReadConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_self_order_orders
                    WHERE session_id=$session
                      AND client_order_id=$client;
                    """;
                q.Parameters.AddWithValue(
                    "$session",
                    inboxSession.Id);
                q.Parameters.AddWithValue(
                    "$client",
                    raceClientOrderId);
                racedOrderCount =
                    Convert.ToInt64(
                        await q.ExecuteScalarAsync());
            }

            assert(
                racedReceiveRejected &&
                racedOrderCount == 0,
                "Self Order revalidates capability inside the serialized mutation lane so a queued session close wins before RECEIVED persistence");

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

            var reducedRestaurantProduct = new Product
            {
                Id = 900002,
                Name = "Restaurant Reduziert",
                BasePriceCents = 1000,
                VatRate = 7m,
                ImHausApplicable = true,
                Unit = "Stück"
            };

            var vatGuardTableId = await repo.SaveTableAsync(
                areaId,
                "TK4VAT",
                "K4 MwSt/Z",
                seats: 2,
                sortOrder: 88);

            var vatGuardSession = await repo.OpenTableAsync(
                vatGuardTableId,
                "KELLNER-VAT",
                guestCount: 1,
                deviceId: "KASSE-VAT");

            var vatGuardItem = await repo.AddItemAsync(
                vatGuardSession.Id,
                vatGuardSession.Version,
                reducedRestaurantProduct,
                1m,
                "KELLNER-VAT",
                "KASSE-VAT");

            assert(
                vatGuardItem.VatRate == 7m,
                "K-4/R2026 Restaurant Bestellung snapshots the effective current Im-Haus VAT: food remains 7% from 01.01.2026 while drinks keep their own 19% base rate");

            var restaurantClosingGuard = new DailyClosingGuard(
                new ParkedReceiptRepository(db),
                db);
            var restaurantClosingCheck =
                await restaurantClosingGuard.CheckAsync();

            assert(
                !restaurantClosingCheck.Allowed &&
                restaurantClosingCheck.Message.Contains(
                    "Restaurant",
                    StringComparison.OrdinalIgnoreCase),
                "K-4 Z-Abschluss is blocked while a Restaurant table session is OPEN");

            await repo.DiscardPendingItemAsync(
                vatGuardSession.Id,
                vatGuardItem.Id,
                "KELLNER-VAT",
                "KASSE-VAT");

            var vatGuardAfterDiscard =
                await repo.GetSessionAsync(vatGuardSession.Id)
                ?? throw new InvalidOperationException(
                    "K4 VAT/Z test session missing.");

            await repo.CloseEmptySessionAsync(
                vatGuardAfterDiscard.Id,
                vatGuardAfterDiscard.Version,
                "KELLNER-VAT",
                "KASSE-VAT");

            var concurrentTableId = await repo.SaveTableAsync(
                areaId,
                "TCONC",
                "Parallel Tisch",
                seats: 4,
                sortOrder: 89);

            var concurrentSession = await repo.OpenTableAsync(
                concurrentTableId,
                "KELLNER-A",
                guestCount: 2,
                deviceId: "HANDHELD-A");

            async Task<bool> TryParallelAddAsync(
                string waiter,
                string deviceId)
            {
                try
                {
                    await repo.AddItemAsync(
                        concurrentSession.Id,
                        concurrentSession.Version,
                        product,
                        1m,
                        waiter,
                        deviceId);
                    return true;
                }
                catch (InvalidOperationException ex)
                    when (ex.Message.Contains(
                        "zwischenzeitlich geändert",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            var parallelResults = await Task.WhenAll(
                TryParallelAddAsync(
                    "KELLNER-A",
                    "HANDHELD-A"),
                TryParallelAddAsync(
                    "KELLNER-B",
                    "HANDHELD-B"));

            var afterParallel =
                await repo.GetSessionAsync(
                    concurrentSession.Id)
                ?? throw new InvalidOperationException(
                    "Parallel test session missing.");

            assert(
                parallelResults.Count(x => x) == 1 &&
                afterParallel.Version ==
                    concurrentSession.Version + 1 &&
                (await repo.ListActiveItemsAsync(
                    concurrentSession.Id)).Count == 1,
                "Two simultaneous Restaurant waiter writes never silently overwrite or duplicate the same table version");

            await repo.AddItemAsync(
                concurrentSession.Id,
                afterParallel.Version,
                product,
                1m,
                "KELLNER-B",
                "HANDHELD-B");

            var afterParallelRetry =
                await repo.GetSessionAsync(
                    concurrentSession.Id)
                ?? throw new InvalidOperationException(
                    "Parallel retry session missing.");

            assert(
                afterParallelRetry.Version ==
                    concurrentSession.Version + 2 &&
                (await repo.ListActiveItemsAsync(
                    concurrentSession.Id)).Count == 2,
                "Conflicted Restaurant waiter command succeeds after refreshing the table version without losing the first waiter write");

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
                "11111111111111111111111111111111";

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

            // A Storno whose printout failed five times stayed off the KDS: the
            // item is already gone from the board, so the kitchen kept cooking.
            const string failedCancelJob =
                "33333333333333333333333333333333";
            await idempotentKitchen.EnqueueCancellationIdempotentAsync(
                idempotentSessionAfterAdd,
                firstIdempotentAdd.Item,
                "Idempotenz Tisch",
                "KELLNER-1",
                failedCancelJob,
                KitchenStations.Grill);
            var parked = false;
            for (var attempt = 0; attempt < 5; attempt++)
                parked = await idempotentKitchen.MarkFailedAttemptAsync(failedCancelJob, "Drucker offline");
            var failedAlert = (await idempotentKitchen.CancellationAlertsAsync(KitchenStations.Grill))
                .SingleOrDefault(x => x.JobId == failedCancelJob);
            var failedCount = await idempotentKitchen.FailedCountAsync(KitchenStations.Grill);
            var requeued = await idempotentKitchen.RequeueFailedAsync(KitchenStations.Grill);
            var retried = (await idempotentKitchen.PendingAsync())
                .SingleOrDefault(x => x.Id == failedCancelJob);
            for (var attempt = 0; attempt < 5; attempt++)
                await idempotentKitchen.MarkFailedAttemptAsync(failedCancelJob, "Drucker offline");
            await idempotentKitchen.AcknowledgeCancellationAsync(failedCancelJob);
            assert(
                parked && failedAlert is { PrintFailed: true, LastError: "Drucker offline" } &&
                failedCount == 1 && requeued == 1 &&
                retried is { State: "PENDING", Attempts: 0 } &&
                (await idempotentKitchen.CancellationAlertsAsync()).All(x => x.JobId != failedCancelJob) &&
                await idempotentKitchen.FailedCountAsync() == 0,
                "Restaurant kitchen: a Storno whose printout failed five times stays on the KDS marked as not printed until the kitchen confirms it, and DRUCK WIEDERHOLEN puts failed jobs back in the queue");

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

            var fiscalState = new RestaurantFiscalOrderService(db, null!);
            string itemFiscalState;
            await using (var c = db.OpenReadConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText =
                    "SELECT fiscal_state FROM restaurant_session_items WHERE id=$id;";
                q.Parameters.AddWithValue("$id", item.Id);
                itemFiscalState =
                    Convert.ToString(await q.ExecuteScalarAsync()) ?? "";
            }

            assert(
                itemFiscalState == "PENDING" &&
                !await fiscalState.IsCurrentStateSecuredAsync(session.Id),
                "K-4 newly added Restaurant item remains PENDING and blocks secured-state until Bestellung capture commits");

            var liveSummaries =
                await repo.ListLiveTableSummariesAsync();

            var liveSummary = liveSummaries.Single(x =>
                x.TableId == tableId);

            if (!liveSummary.IsOpen ||
                liveSummary.SessionId != session.Id ||
                liveSummary.SessionVersion != afterItem!.Version ||
                liveSummary.OpenTotalCents != 2580)
            {
                throw new InvalidOperationException(
                    "Single-query Restaurant table summary must preserve live session version and cent-exact open total.");
            }

            var concurrentSummaries =
                await Task.WhenAll(
                    Enumerable.Range(0, 20)
                        .Select(_ =>
                            repo.ListLiveTableSummariesAsync()));

            if (concurrentSummaries.Any(snapshot =>
                    snapshot.Single(x => x.TableId == tableId)
                        .OpenTotalCents != 2580))
            {
                throw new InvalidOperationException(
                    "Concurrent Restaurant read-only table refreshes must stay consistent under WAL.");
            }

            var readOnlyRejectedWrite = false;
            try
            {
                await using var readOnly =
                    db.OpenReadConnection();
                await using var illegalWrite =
                    readOnly.CreateCommand();
                illegalWrite.CommandText =
                    "UPDATE restaurant_tables SET display_name='ILLEGAL' WHERE id=$id;";
                illegalWrite.Parameters.AddWithValue(
                    "$id",
                    tableId);
                await illegalWrite.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                readOnlyRejectedWrite = true;
            }

            if (!readOnlyRejectedWrite)
            {
                throw new InvalidOperationException(
                    "Restaurant read-only hot-path connection must reject writes.");
            }

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

            var rollbackTableId = await repo.SaveTableAsync(
                areaId,
                "TK4ROLL",
                "K4 Rollback",
                seats: 2,
                sortOrder: 91);
            var rollbackSession = await repo.OpenTableAsync(
                rollbackTableId,
                "KELLNER-4",
                deviceId: "KASSE-K4");
            var rollbackItem = await repo.AddItemAsync(
                rollbackSession.Id,
                rollbackSession.Version,
                product,
                1m,
                "KELLNER-4",
                "KASSE-K4");
            var discarded = await repo.DiscardPendingItemAsync(
                rollbackSession.Id,
                rollbackItem.Id,
                "KELLNER-4",
                "KASSE-K4");

            assert(
                discarded &&
                await repo.GetItemAsync(
                    rollbackSession.Id,
                    rollbackItem.Id) is null &&
                await fiscalState.IsCurrentStateSecuredAsync(
                    rollbackSession.Id),
                "K-4 aborted PENDING Restaurant item can be compensated without leaving the table permanently inconsistent");

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

            var partialCheckoutDraft = await repo.BuildCheckoutDraftAsync(
                session.Id,
                afterItem!.Version,
                new[]
                {
                    new RestaurantSplitSelection(
                        splitItems.Single().Id,
                        1000)
                });

            assert(
                partialCheckoutDraft.TotalCents == 1290 &&
                partialCheckoutDraft.Lines.Length == 1 &&
                partialCheckoutDraft.Lines.Single().Quantity == 1m &&
                partialCheckoutDraft.Selections.Single().QuantityMilli == 1000 &&
                !string.IsNullOrWhiteSpace(partialCheckoutDraft.OperationId),
                "Restaurant item split produces a real partial checkout draft without mutating the open table");

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

            var laneJournal = new PrintJobJournal(
                Path.Combine(root, "restaurant-printer-lanes"));
            var slowLaneStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            await using var slowPrinterLane =
                new StarMcPrint3PrinterService(
                    laneJournal,
                    TimeSpan.FromMilliseconds(80),
                    async () =>
                    {
                        slowLaneStarted.TrySetResult();
                        await Task.Delay(300);
                    },
                    "TEST-KITCHEN-SLOW");

            await using var fastPrinterLane =
                new StarMcPrint3PrinterService(
                    laneJournal,
                    TimeSpan.FromSeconds(1),
                    () => Task.CompletedTask,
                    "TEST-KITCHEN-FAST");

            var lanePrint = new KitchenPrintJob(
                DateTimeOffset.UtcNow,
                0,
                0,
                "KELLNER-1",
                new[]
                {
                    new KitchenPrintLine(
                        "Testartikel",
                        1m)
                });

            var slowPrintId =
                Guid.NewGuid().ToString("N");
            var fastPrintId =
                Guid.NewGuid().ToString("N");

            var slowPrintTask =
                slowPrinterLane.SubmitOrderAsync(
                    new PrintJobRecord(
                        slowPrintId,
                        "QUEUED",
                        "TEST-KITCHEN-SLOW",
                        null,
                        null,
                        "",
                        Kitchen: lanePrint));

            await slowLaneStarted.Task.WaitAsync(
                TimeSpan.FromSeconds(3));

            await fastPrinterLane
                .SubmitOrderAsync(
                    new PrintJobRecord(
                        fastPrintId,
                        "QUEUED",
                        "TEST-KITCHEN-FAST",
                        null,
                        null,
                        "",
                        Kitchen: lanePrint))
                .WaitAsync(
                    TimeSpan.FromSeconds(3));

            var slowTimedOut = false;
            try
            {
                await slowPrintTask;
            }
            catch (TimeoutException)
            {
                slowTimedOut = true;
            }

            var fastPrintRecord =
                await laneJournal.GetAsync(fastPrintId);

            if (!slowTimedOut ||
                fastPrintRecord?.State != "SPOOL_ACCEPTED")
            {
                throw new InvalidOperationException(
                    "A timed-out Restaurant kitchen printer lane must not block another physical printer lane.");
            }

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
                "22222222222222222222222222222222";

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
                    !TableExists(c, "restaurant_device_commands") &&
                    !TableExists(c, "restaurant_operator_sessions"),
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

    private sealed class FakeProductCatalog : IProductCatalog
    {
        public FakeProductCatalog(params Product[] products)
        {
            Products = products;
        }

        public IReadOnlyList<ProductGroup> Groups { get; } =
            Array.Empty<ProductGroup>();

        public IReadOnlyList<Category> Categories { get; } =
            Array.Empty<Category>();

        public IReadOnlyList<Product> Products { get; }

        public ValueTask ReloadAsync(
            CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public bool TryGetByBarcode(
            string barcode,
            out Product? product)
        {
            product = Products.FirstOrDefault(x =>
                string.Equals(
                    x.Barcode,
                    barcode,
                    StringComparison.OrdinalIgnoreCase));
            return product is not null;
        }

        public IReadOnlyList<Product> GetByCategory(
            long categoryId) =>
            Products
                .Where(x => x.CategoryId == categoryId)
                .ToArray();
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

        await using (var secured = c.CreateCommand())
        {
            secured.Transaction = tx;
            secured.CommandText = """
                UPDATE restaurant_session_items
                SET fiscal_state='SECURED'
                WHERE session_id=$session
                  AND fiscal_state='PENDING';
                """;
            secured.Parameters.AddWithValue("$session", sessionId);
            await secured.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    private static bool ColumnExists(
        SqliteConnection c,
        string table,
        string column)
    {
        using var q = c.CreateCommand();
        q.CommandText = $"PRAGMA table_info({table});";
        using var r = q.ExecuteReader();
        while (r.Read())
        {
            if (string.Equals(
                    r.GetString(1),
                    column,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
