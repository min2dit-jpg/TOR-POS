using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class RestaurantRepository
{
    private readonly SqliteDatabase _db;

    public RestaurantRepository(SqliteDatabase db)
    {
        _db = db;
    }

    public async Task<long> SaveAreaAsync(
        string name,
        int sortOrder = 0,
        CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("Bereichsname darf nicht leer sein.", nameof(name));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_areas(name,sort_order,is_active)
                VALUES($name,$sort,1)
                ON CONFLICT(name) DO UPDATE SET
                    sort_order=excluded.sort_order,
                    is_active=1
                RETURNING id;
                """;
            q.Parameters.AddWithValue("$name", name);
            q.Parameters.AddWithValue("$sort", sortOrder);
            return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        });
    }

    public async Task<long> SaveTableAsync(
        long areaId,
        string code,
        string displayName,
        int seats,
        int sortOrder = 0,
        CancellationToken ct = default)
    {
        code = (code ?? "").Trim();
        displayName = (displayName ?? "").Trim();
        if (areaId <= 0) throw new ArgumentOutOfRangeException(nameof(areaId));
        if (code.Length == 0) throw new ArgumentException("Tischcode darf nicht leer sein.", nameof(code));
        if (displayName.Length == 0) throw new ArgumentException("Tischname darf nicht leer sein.", nameof(displayName));
        if (seats is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(seats));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_tables(
                    area_id,code,display_name,seats,sort_order,is_active,version)
                VALUES($area,$code,$name,$seats,$sort,1,1)
                ON CONFLICT(code) DO UPDATE SET
                    area_id=excluded.area_id,
                    display_name=excluded.display_name,
                    seats=excluded.seats,
                    sort_order=excluded.sort_order,
                    is_active=1,
                    version=restaurant_tables.version+1
                RETURNING id;
                """;
            q.Parameters.AddWithValue("$area", areaId);
            q.Parameters.AddWithValue("$code", code);
            q.Parameters.AddWithValue("$name", displayName);
            q.Parameters.AddWithValue("$seats", seats);
            q.Parameters.AddWithValue("$sort", sortOrder);
            return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
        });
    }

    public async Task<IReadOnlyList<RestaurantTable>> ListTablesAsync(
        CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            var result = new List<RestaurantTable>();
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,area_id,code,display_name,seats,sort_order,is_active,version
                FROM restaurant_tables
                WHERE is_active=1
                ORDER BY area_id,sort_order,id;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                result.Add(new RestaurantTable(
                    r.GetInt64(0),
                    r.GetInt64(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.GetInt32(4),
                    r.GetInt32(5),
                    r.GetInt32(6) != 0,
                    r.GetInt64(7)));
            }
            return (IReadOnlyList<RestaurantTable>)result;
        });
    }

    public async Task<RestaurantTableSession> OpenTableAsync(
        long tableId,
        string operatorName,
        int guestCount = 1,
        string deviceId = "",
        CancellationToken ct = default)
    {
        operatorName = (operatorName ?? "").Trim();
        if (tableId <= 0) throw new ArgumentOutOfRangeException(nameof(tableId));
        if (operatorName.Length == 0) throw new ArgumentException("Bediener fehlt.", nameof(operatorName));
        if (guestCount is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(guestCount));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var table = c.CreateCommand())
            {
                table.Transaction = tx;
                table.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_tables
                    WHERE id=$id AND is_active=1;
                    """;
                table.Parameters.AddWithValue("$id", tableId);
                if (Convert.ToInt32(await table.ExecuteScalarAsync(ct)) != 1)
                    throw new InvalidOperationException("Tisch ist nicht vorhanden oder deaktiviert.");
            }

            await using (var existing = c.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_sessions
                    WHERE table_id=$table
                      AND state IN ('OPEN','CHECK_REQUESTED');
                    """;
                existing.Parameters.AddWithValue("$table", tableId);
                if (Convert.ToInt32(await existing.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException("Tisch ist bereits geöffnet.");
            }

            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var insert = c.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO restaurant_sessions(
                        id,table_id,opened_at,updated_at,closed_at,state,
                        opened_by,assigned_waiter,guest_count,note,version)
                    VALUES(
                        $id,$table,$now,$now,NULL,'OPEN',
                        $operator,$operator,$guests,'',1);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$table", tableId);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$operator", operatorName);
                insert.Parameters.AddWithValue("$guests", guestCount);
                await insert.ExecuteNonQueryAsync(ct);
            }

            await AppendEventAsync(
                c, tx, id, "TISCH_GEOEFFNET",
                operatorName, deviceId, "{}", now, ct);

            await tx.CommitAsync(ct);

            return new RestaurantTableSession(
                id,
                tableId,
                DateTimeOffset.Parse(now),
                DateTimeOffset.Parse(now),
                null,
                RestaurantTableSessionState.Open,
                operatorName,
                operatorName,
                guestCount,
                "",
                1);
        });
    }

    public async Task<RestaurantTableSession?> GetLiveSessionForTableAsync(
        long tableId,
        CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,table_id,opened_at,updated_at,closed_at,state,
                       opened_by,assigned_waiter,guest_count,note,version
                FROM restaurant_sessions
                WHERE table_id=$table
                  AND state IN ('OPEN','CHECK_REQUESTED')
                LIMIT 1;
                """;
            q.Parameters.AddWithValue("$table", tableId);
            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            return ReadSession(r);
        });
    }

    public async Task<RestaurantTableSession> ReassignWaiterAsync(
        string sessionId,
        long expectedVersion,
        string newWaiter,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        newWaiter = (newWaiter ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (sessionId.Length == 0) throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (newWaiter.Length == 0) throw new ArgumentException("Kellner fehlt.", nameof(newWaiter));
        if (expectedVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE restaurant_sessions
                    SET assigned_waiter=$waiter,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND state IN ('OPEN','CHECK_REQUESTED');
                    """;
                q.Parameters.AddWithValue("$waiter", newWaiter);
                q.Parameters.AddWithValue("$now", now);
                q.Parameters.AddWithValue("$id", sessionId);
                q.Parameters.AddWithValue("$version", expectedVersion);
                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            await AppendEventAsync(
                c, tx, sessionId, "KELLNER_GEWECHSELT",
                actor, deviceId,
                System.Text.Json.JsonSerializer.Serialize(new { waiter = newWaiter }),
                now, ct);

            RestaurantTableSession result;
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT id,table_id,opened_at,updated_at,closed_at,state,
                           opened_by,assigned_waiter,guest_count,note,version
                    FROM restaurant_sessions
                    WHERE id=$id;
                    """;
                read.Parameters.AddWithValue("$id", sessionId);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException("Tischvorgang nicht gefunden.");
                result = ReadSession(r);
            }

            await tx.CommitAsync(ct);
            return result;
        });
    }

    public async Task<RestaurantSessionItem> AddItemAsync(
        string sessionId,
        long expectedSessionVersion,
        Product product,
        decimal quantity,
        string operatorName,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        operatorName = (operatorName ?? "").Trim();
        if (sessionId.Length == 0) throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedSessionVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
        if (product.Id <= 0) throw new ArgumentException("Artikel fehlt.", nameof(product));
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));

        var quantityMilli = (long)Math.Round(quantity * 1000m, MidpointRounding.AwayFromZero);
        if (quantityMilli <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");
            var token = Guid.NewGuid().ToString("N");

            await using (var touch = c.CreateCommand())
            {
                touch.Transaction = tx;
                touch.CommandText = """
                    UPDATE restaurant_sessions
                    SET updated_at=$now,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND state='OPEN';
                    """;
                touch.Parameters.AddWithValue("$now", now);
                touch.Parameters.AddWithValue("$id", sessionId);
                touch.Parameters.AddWithValue("$version", expectedSessionVersion);
                if (await touch.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            long itemId;
            await using (var insert = c.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO restaurant_session_items(
                        session_id,line_token,product_id,product_name,variant_name,
                        quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                        added_by,added_at,version)
                    VALUES(
                        $session,$token,$product,$name,'',
                        $quantity,$price,$vat,$pfand,'ACTIVE',
                        $operator,$now,1)
                    RETURNING id;
                    """;
                insert.Parameters.AddWithValue("$session", sessionId);
                insert.Parameters.AddWithValue("$token", token);
                insert.Parameters.AddWithValue("$product", product.Id);
                insert.Parameters.AddWithValue("$name", product.Name);
                insert.Parameters.AddWithValue("$quantity", quantityMilli);
                insert.Parameters.AddWithValue("$price", product.BasePriceCents + product.PfandCents);
                insert.Parameters.AddWithValue("$vat", product.VatRate);
                insert.Parameters.AddWithValue("$pfand", product.PfandCents);
                insert.Parameters.AddWithValue("$operator", operatorName);
                insert.Parameters.AddWithValue("$now", now);
                itemId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }

            await AppendEventAsync(
                c, tx, sessionId, "POSITION_HINZUGEFUEGT",
                operatorName, deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    lineToken = token,
                    productId = product.Id,
                    productName = product.Name,
                    quantityMilli,
                    unitPriceCents = product.BasePriceCents + product.PfandCents
                }),
                now, ct);

            await tx.CommitAsync(ct);

            return new RestaurantSessionItem(
                itemId,
                sessionId,
                token,
                product.Id,
                product.Name,
                "",
                quantityMilli,
                product.BasePriceCents + product.PfandCents,
                product.VatRate,
                product.PfandCents,
                RestaurantSessionItemState.Active,
                operatorName,
                DateTimeOffset.Parse(now),
                1);
        });
    }

    public async Task<IReadOnlyList<RestaurantSessionItem>> ListActiveItemsAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0)
            return Array.Empty<RestaurantSessionItem>();

        return await IoQueue.RunAsync(async () =>
        {
            var result = new List<RestaurantSessionItem>();
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,session_id,line_token,product_id,product_name,variant_name,
                       quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                       added_by,added_at,version
                FROM restaurant_session_items
                WHERE session_id=$session
                  AND state='ACTIVE'
                ORDER BY id;
                """;
            q.Parameters.AddWithValue("$session", sessionId);
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                result.Add(new RestaurantSessionItem(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.GetInt64(3),
                    r.GetString(4),
                    r.GetString(5),
                    r.GetInt64(6),
                    r.GetInt64(7),
                    Convert.ToDecimal(r.GetDouble(8)),
                    r.GetInt64(9),
                    RestaurantSessionItemState.Active,
                    r.GetString(11),
                    DateTimeOffset.Parse(r.GetString(12)),
                    r.GetInt64(13)));
            }

            return (IReadOnlyList<RestaurantSessionItem>)result;
        });
    }

    public async Task<RestaurantTableSession?> GetSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0)
            return null;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,table_id,opened_at,updated_at,closed_at,state,
                       opened_by,assigned_waiter,guest_count,note,version
                FROM restaurant_sessions
                WHERE id=$id;
                """;
            q.Parameters.AddWithValue("$id", sessionId);
            await using var r = await q.ExecuteReaderAsync(ct);
            return await r.ReadAsync(ct) ? ReadSession(r) : null;
        });
    }

    public async Task<RestaurantTableSession> MoveSessionToTableAsync(
        string sessionId,
        long expectedVersion,
        long targetTableId,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (sessionId.Length == 0) throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (targetTableId <= 0) throw new ArgumentOutOfRangeException(nameof(targetTableId));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var target = c.CreateCommand())
            {
                target.Transaction = tx;
                target.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_tables
                    WHERE id=$id AND is_active=1;
                    """;
                target.Parameters.AddWithValue("$id", targetTableId);
                if (Convert.ToInt32(await target.ExecuteScalarAsync(ct)) != 1)
                    throw new InvalidOperationException("Zieltisch ist nicht vorhanden oder deaktiviert.");
            }

            await using (var occupied = c.CreateCommand())
            {
                occupied.Transaction = tx;
                occupied.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_sessions
                    WHERE table_id=$table
                      AND state IN ('OPEN','CHECK_REQUESTED')
                      AND id<>$session;
                    """;
                occupied.Parameters.AddWithValue("$table", targetTableId);
                occupied.Parameters.AddWithValue("$session", sessionId);
                if (Convert.ToInt32(await occupied.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException("Zieltisch ist bereits belegt.");
            }

            long sourceTableId;
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT table_id
                    FROM restaurant_sessions
                    WHERE id=$id
                      AND version=$version
                      AND state IN ('OPEN','CHECK_REQUESTED');
                    """;
                read.Parameters.AddWithValue("$id", sessionId);
                read.Parameters.AddWithValue("$version", expectedVersion);
                var raw = await read.ExecuteScalarAsync(ct);
                if (raw is null)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
                sourceTableId = Convert.ToInt64(raw);
            }

            if (sourceTableId == targetTableId)
                throw new InvalidOperationException("Quell- und Zieltisch sind identisch.");

            await using (var update = c.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE restaurant_sessions
                    SET table_id=$target,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND state IN ('OPEN','CHECK_REQUESTED');
                    """;
                update.Parameters.AddWithValue("$target", targetTableId);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", sessionId);
                update.Parameters.AddWithValue("$version", expectedVersion);
                if (await update.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            await AppendEventAsync(
                c, tx, sessionId, "TISCH_UMGEBUCHT",
                actor, deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    sourceTableId,
                    targetTableId
                }),
                now, ct);

            var result = await ReadSessionAsync(c, tx, sessionId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public async Task<RestaurantTableSession> MergeSessionsAsync(
        string sourceSessionId,
        long expectedSourceVersion,
        string targetSessionId,
        long expectedTargetVersion,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sourceSessionId = (sourceSessionId ?? "").Trim();
        targetSessionId = (targetSessionId ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (sourceSessionId.Length == 0 || targetSessionId.Length == 0)
            throw new ArgumentException("Tischvorgang fehlt.");
        if (string.Equals(sourceSessionId, targetSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Ein Tischvorgang kann nicht mit sich selbst zusammengelegt werden.");

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            var source = await ReadLiveSessionForUpdateAsync(
                c, tx, sourceSessionId, expectedSourceVersion, ct);
            var target = await ReadLiveSessionForUpdateAsync(
                c, tx, targetSessionId, expectedTargetVersion, ct);

            await using (var moveItems = c.CreateCommand())
            {
                moveItems.Transaction = tx;
                moveItems.CommandText = """
                    UPDATE restaurant_session_items
                    SET session_id=$target,
                        version=version+1
                    WHERE session_id=$source
                      AND state='ACTIVE';
                    """;
                moveItems.Parameters.AddWithValue("$target", targetSessionId);
                moveItems.Parameters.AddWithValue("$source", sourceSessionId);
                await moveItems.ExecuteNonQueryAsync(ct);
            }

            await using (var closeSource = c.CreateCommand())
            {
                closeSource.Transaction = tx;
                closeSource.CommandText = """
                    UPDATE restaurant_sessions
                    SET state='CANCELLED',
                        closed_at=$now,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id AND version=$version;
                    """;
                closeSource.Parameters.AddWithValue("$now", now);
                closeSource.Parameters.AddWithValue("$id", sourceSessionId);
                closeSource.Parameters.AddWithValue("$version", expectedSourceVersion);
                if (await closeSource.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Quelltisch wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            await using (var touchTarget = c.CreateCommand())
            {
                touchTarget.Transaction = tx;
                touchTarget.CommandText = """
                    UPDATE restaurant_sessions
                    SET guest_count=guest_count+$guests,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id AND version=$version;
                    """;
                touchTarget.Parameters.AddWithValue("$guests", source.GuestCount);
                touchTarget.Parameters.AddWithValue("$now", now);
                touchTarget.Parameters.AddWithValue("$id", targetSessionId);
                touchTarget.Parameters.AddWithValue("$version", expectedTargetVersion);
                if (await touchTarget.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Zieltisch wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                sourceSessionId,
                sourceTableId = source.TableId,
                targetSessionId,
                targetTableId = target.TableId
            });

            await AppendEventAsync(
                c, tx, sourceSessionId, "TISCHE_ZUSAMMENGELEGT_QUELLE",
                actor, deviceId, payload, now, ct);
            await AppendEventAsync(
                c, tx, targetSessionId, "TISCHE_ZUSAMMENGELEGT_ZIEL",
                actor, deviceId, payload, now, ct);

            var result = await ReadSessionAsync(c, tx, targetSessionId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public async Task<RestaurantTableSession> CloseEmptySessionAsync(
        string sessionId,
        long expectedVersion,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await ReadLiveSessionForUpdateAsync(
                c, tx, sessionId, expectedVersion, ct);

            await using (var count = c.CreateCommand())
            {
                count.Transaction = tx;
                count.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_session_items
                    WHERE session_id=$id AND state='ACTIVE';
                    """;
                count.Parameters.AddWithValue("$id", sessionId);
                if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) != 0)
                    throw new InvalidOperationException(
                        "Tisch enthält offene Positionen. Abschluss muss über den Kassen-/Zahlungsweg erfolgen.");
            }

            var now = DateTimeOffset.UtcNow.ToString("O");
            await using (var update = c.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE restaurant_sessions
                    SET state='CLOSED',
                        closed_at=$now,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id AND version=$version;
                    """;
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", sessionId);
                update.Parameters.AddWithValue("$version", expectedVersion);
                if (await update.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            await AppendEventAsync(
                c, tx, sessionId, "TISCH_LEER_GESCHLOSSEN",
                actor, deviceId, "{}", now, ct);

            var result = await ReadSessionAsync(c, tx, sessionId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static async Task<RestaurantTableSession> ReadLiveSessionForUpdateAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string sessionId,
        long expectedVersion,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT id,table_id,opened_at,updated_at,closed_at,state,
                   opened_by,assigned_waiter,guest_count,note,version
            FROM restaurant_sessions
            WHERE id=$id
              AND version=$version
              AND state IN ('OPEN','CHECK_REQUESTED');
            """;
        q.Parameters.AddWithValue("$id", sessionId);
        q.Parameters.AddWithValue("$version", expectedVersion);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException(
                "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
        return ReadSession(r);
    }

    private static async Task<RestaurantTableSession> ReadSessionAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string sessionId,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT id,table_id,opened_at,updated_at,closed_at,state,
                   opened_by,assigned_waiter,guest_count,note,version
            FROM restaurant_sessions
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", sessionId);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Tischvorgang nicht gefunden.");
        return ReadSession(r);
    }

    private static RestaurantTableSession ReadSession(SqliteDataReader r)
    {
        var state = r.GetString(5) switch
        {
            "OPEN" => RestaurantTableSessionState.Open,
            "CHECK_REQUESTED" => RestaurantTableSessionState.CheckRequested,
            "CLOSED" => RestaurantTableSessionState.Closed,
            _ => RestaurantTableSessionState.Cancelled
        };

        return new RestaurantTableSession(
            r.GetString(0),
            r.GetInt64(1),
            DateTimeOffset.Parse(r.GetString(2)),
            DateTimeOffset.Parse(r.GetString(3)),
            r.IsDBNull(4) ? null : DateTimeOffset.Parse(r.GetString(4)),
            state,
            r.GetString(6),
            r.GetString(7),
            r.GetInt32(8),
            r.GetString(9),
            r.GetInt64(10));
    }

    private static async Task AppendEventAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string sessionId,
        string eventType,
        string actor,
        string deviceId,
        string payloadJson,
        string createdAt,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT INTO restaurant_session_events(
                session_id,event_type,actor,device_id,created_at,payload_json)
            VALUES($session,$type,$actor,$device,$created,$payload);
            """;
        q.Parameters.AddWithValue("$session", sessionId);
        q.Parameters.AddWithValue("$type", eventType);
        q.Parameters.AddWithValue("$actor", actor ?? "");
        q.Parameters.AddWithValue("$device", deviceId ?? "");
        q.Parameters.AddWithValue("$created", createdAt);
        q.Parameters.AddWithValue("$payload", payloadJson ?? "");
        await q.ExecuteNonQueryAsync(ct);
    }
}
