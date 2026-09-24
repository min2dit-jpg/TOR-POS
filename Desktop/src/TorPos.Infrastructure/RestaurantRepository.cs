using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantItemMutationResult(
    RestaurantSessionItem Item,
    bool Created);

public sealed record RestaurantTableLiveSummary(
    long TableId,
    string TableName,
    bool IsOpen,
    string SessionId,
    long SessionVersion,
    string Waiter,
    int GuestCount,
    long OpenTotalCents);

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

    public async Task<IReadOnlyList<RestaurantTableLiveSummary>> ListLiveTableSummariesAsync(
        CancellationToken ct = default)
    {
        var result =
            new List<RestaurantTableLiveSummary>();

        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT
                t.id,
                t.display_name,
                s.id,
                s.version,
                s.assigned_waiter,
                s.guest_count,
                COALESCE(
                    SUM(
                        CASE
                            WHEN i.id IS NULL THEN 0
                            ELSE CAST(
                                ROUND(
                                    (i.quantity_milli * i.unit_price_cents) / 1000.0
                                ) AS INTEGER)
                        END
                    ),
                    0
                ) AS open_total_cents
            FROM restaurant_tables t
            LEFT JOIN restaurant_sessions s
              ON s.table_id=t.id
             AND s.state IN ('OPEN','CHECK_REQUESTED')
            LEFT JOIN restaurant_session_items i
              ON i.session_id=s.id
             AND i.state='ACTIVE'
            WHERE t.is_active=1
            GROUP BY
                t.id,t.display_name,t.area_id,t.sort_order,
                s.id,s.version,s.assigned_waiter,s.guest_count
            ORDER BY t.area_id,t.sort_order,t.id;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var isOpen = !r.IsDBNull(2);
            result.Add(
                new RestaurantTableLiveSummary(
                    r.GetInt64(0),
                    r.GetString(1),
                    isOpen,
                    isOpen ? r.GetString(2) : "",
                    isOpen ? r.GetInt64(3) : 0,
                    isOpen ? r.GetString(4) : "",
                    isOpen ? r.GetInt32(5) : 0,
                    r.GetInt64(6)));
        }

        return result;
    }

    public async Task<RestaurantTable?> GetTableAsync(
        long tableId,
        CancellationToken ct = default)
    {
        if (tableId <= 0)
            return null;

        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,area_id,code,display_name,seats,sort_order,is_active,version
            FROM restaurant_tables
            WHERE id=$id
            LIMIT 1;
            """;
        q.Parameters.AddWithValue("$id", tableId);

        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            return null;

        return new RestaurantTable(
            r.GetInt64(0),
            r.GetInt64(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetInt32(6) != 0,
            r.GetInt64(7));
    }

    public async Task<RestaurantTableSession> OpenTableAsync(
        long tableId,
        string operatorName,
        int guestCount = 1,
        string note = "",
        string deviceId = "",
        CancellationToken ct = default)
    {
        operatorName = (operatorName ?? "").Trim();
        note = (note ?? "").Trim();
        if (tableId <= 0) throw new ArgumentOutOfRangeException(nameof(tableId));
        if (operatorName.Length == 0) throw new ArgumentException("Bediener fehlt.", nameof(operatorName));
        if (guestCount is < 1 or > 999) throw new ArgumentOutOfRangeException(nameof(guestCount));
        if (note.Length > 500) throw new ArgumentException("Tischnotiz ist zu lang.", nameof(note));

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
                        $operator,$operator,$guests,$note,1);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$table", tableId);
                insert.Parameters.AddWithValue("$now", now);
                insert.Parameters.AddWithValue("$operator", operatorName);
                insert.Parameters.AddWithValue("$guests", guestCount);
                insert.Parameters.AddWithValue("$note", note);
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
                note,
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

    public async Task<RestaurantTableSession> UpdateSessionDetailsAsync(
        string sessionId,
        long expectedVersion,
        int guestCount,
        string note,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        note = (note ?? "").Trim();
        actor = (actor ?? "").Trim();

        if (sessionId.Length == 0)
            throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (guestCount is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(guestCount));
        if (note.Length > 500)
            throw new ArgumentException("Tischnotiz ist zu lang.", nameof(note));

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
                    SET guest_count=$guests,
                        note=$note,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND state='OPEN';
                    """;
                q.Parameters.AddWithValue("$guests", guestCount);
                q.Parameters.AddWithValue("$note", note);
                q.Parameters.AddWithValue("$now", now);
                q.Parameters.AddWithValue("$id", sessionId);
                q.Parameters.AddWithValue("$version", expectedVersion);

                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            await AppendEventAsync(
                c,
                tx,
                sessionId,
                "TISCHDETAILS_GEAENDERT",
                actor,
                deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    guestCount,
                    note
                }),
                now,
                ct);

            var result = await ReadSessionAsync(
                c,
                tx,
                sessionId,
                ct);

            await tx.CommitAsync(ct);
            return result;
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
                      AND state='OPEN';
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

    public async Task<RestaurantSessionItem> CancelItemAsync(
        string sessionId,
        long expectedSessionVersion,
        long sessionItemId,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (sessionId.Length == 0)
            throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedSessionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
        if (sessionItemId <= 0)
            throw new ArgumentOutOfRangeException(nameof(sessionItemId));

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var lockSession = c.CreateCommand())
            {
                lockSession.Transaction = tx;
                lockSession.CommandText = """
                    UPDATE restaurant_sessions
                    SET updated_at=$now,
                        version=version+1
                    WHERE id=$session
                      AND version=$version
                      AND state='OPEN';
                    """;
                lockSession.Parameters.AddWithValue("$now", now);
                lockSession.Parameters.AddWithValue("$session", sessionId);
                lockSession.Parameters.AddWithValue("$version", expectedSessionVersion);
                if (await lockSession.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Tischvorgang wurde zwischenzeitlich geändert. Ansicht aktualisieren.");
            }

            RestaurantSessionItem item;
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT id,session_id,line_token,product_id,product_name,variant_name,
                           quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                           added_by,added_at,version
                    FROM restaurant_session_items
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE';
                    """;
                read.Parameters.AddWithValue("$item", sessionItemId);
                read.Parameters.AddWithValue("$session", sessionId);
                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException(
                        "Restaurant-Position ist nicht mehr offen.");

                item = new RestaurantSessionItem(
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
                    r.GetInt64(13));
            }

            await using (var cancel = c.CreateCommand())
            {
                cancel.Transaction = tx;
                cancel.CommandText = """
                    UPDATE restaurant_session_items
                    SET state='CANCELLED',
                        version=version+1
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE';
                    """;
                cancel.Parameters.AddWithValue("$item", sessionItemId);
                cancel.Parameters.AddWithValue("$session", sessionId);
                if (await cancel.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Restaurant-Position wurde zwischenzeitlich geändert.");
            }

            await AppendEventAsync(
                c,
                tx,
                sessionId,
                "POSITION_STORNIERT",
                actor,
                deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    sessionItemId,
                    item.ProductId,
                    item.ProductName,
                    item.QuantityMilli,
                    item.LineTotalCents
                }),
                now,
                ct);

            await tx.CommitAsync(ct);
            return item with { State = RestaurantSessionItemState.Cancelled };
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
        var mutation = await AddItemWithLineTokenAsync(
            sessionId,
            expectedSessionVersion,
            product,
            quantity,
            operatorName,
            Guid.NewGuid().ToString("N"),
            deviceId,
            ct);

        return mutation.Item;
    }

    public async Task<RestaurantItemMutationResult> AddItemWithLineTokenAsync(
        string sessionId,
        long expectedSessionVersion,
        Product product,
        decimal quantity,
        string operatorName,
        string lineToken,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        operatorName = (operatorName ?? "").Trim();
        lineToken = (lineToken ?? "").Trim();

        if (sessionId.Length == 0)
            throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedSessionVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
        if (product.Id <= 0)
            throw new ArgumentException("Artikel fehlt.", nameof(product));
        if (product.IsWeighted || product.IsCombo || product.Variants.Count > 0)
            throw new InvalidOperationException(
                "Dieser Artikel benötigt einen erweiterten Restaurant-Snapshot und ist in dieser Foundation noch gesperrt.");
        if (quantity <= 0m)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        if (lineToken.Length is < 8 or > 160)
            throw new ArgumentException(
                "Restaurant-Zeilenkennung ist ungültig.",
                nameof(lineToken));

        var quantityMilli =
            (long)Math.Round(
                quantity * 1000m,
                MidpointRounding.AwayFromZero);

        if (quantityMilli <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        var effectiveVatRate = ImHausVat.Effective(
            product.VatRate,
            imHaus: true,
            product.ImHausApplicable);

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var existing = c.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = """
                    SELECT id,session_id,line_token,product_id,product_name,variant_name,
                           quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                           added_by,added_at,version
                    FROM restaurant_session_items
                    WHERE line_token=$token
                    LIMIT 1;
                    """;
                existing.Parameters.AddWithValue("$token", lineToken);

                await using var r = await existing.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var item = new RestaurantSessionItem(
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
                        Enum.Parse<RestaurantSessionItemState>(
                            r.GetString(10),
                            ignoreCase: true),
                        r.GetString(11),
                        DateTimeOffset.Parse(r.GetString(12)),
                        r.GetInt64(13));

                    if (!string.Equals(
                            item.SessionId,
                            sessionId,
                            StringComparison.Ordinal) ||
                        item.ProductId != product.Id ||
                        item.QuantityMilli != quantityMilli)
                    {
                        throw new InvalidOperationException(
                            "Restaurant-Zeilenkennung wurde bereits für eine andere Position verwendet.");
                    }

                    await tx.CommitAsync(ct);
                    return new RestaurantItemMutationResult(
                        item,
                        Created: false);
                }
            }

            var now = DateTimeOffset.UtcNow.ToString("O");

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
                        fiscal_state,added_by,added_at,version)
                    VALUES(
                        $session,$token,$product,$name,'',
                        $quantity,$price,$vat,$pfand,'ACTIVE',
                        'PENDING',$operator,$now,1)
                    RETURNING id;
                    """;
                insert.Parameters.AddWithValue("$session", sessionId);
                insert.Parameters.AddWithValue("$token", lineToken);
                insert.Parameters.AddWithValue("$product", product.Id);
                insert.Parameters.AddWithValue("$name", product.Name);
                insert.Parameters.AddWithValue("$quantity", quantityMilli);
                insert.Parameters.AddWithValue(
                    "$price",
                    product.BasePriceCents + product.PfandCents);
                insert.Parameters.AddWithValue("$vat", effectiveVatRate);
                insert.Parameters.AddWithValue("$pfand", product.PfandCents);
                insert.Parameters.AddWithValue("$operator", operatorName);
                insert.Parameters.AddWithValue("$now", now);
                itemId = Convert.ToInt64(
                    await insert.ExecuteScalarAsync(ct));
            }

            await AppendEventAsync(
                c,
                tx,
                sessionId,
                "POSITION_HINZUGEFUEGT",
                operatorName,
                deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    lineToken,
                    productId = product.Id,
                    productName = product.Name,
                    quantityMilli,
                    unitPriceCents =
                        product.BasePriceCents + product.PfandCents
                }),
                now,
                ct);

            await tx.CommitAsync(ct);

            return new RestaurantItemMutationResult(
                new RestaurantSessionItem(
                    itemId,
                    sessionId,
                    lineToken,
                    product.Id,
                    product.Name,
                    "",
                    quantityMilli,
                    product.BasePriceCents + product.PfandCents,
                    effectiveVatRate,
                    product.PfandCents,
                    RestaurantSessionItemState.Active,
                    operatorName,
                    DateTimeOffset.Parse(now),
                    1),
                Created: true);
        });
    }

    public async Task<bool> DiscardPendingItemAsync(
        string sessionId,
        long sessionItemId,
        string actor,
        string deviceId = "",
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();

        if (sessionId.Length == 0 || sessionItemId <= 0)
            return false;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            long productId;
            string productName;
            long quantityMilli;

            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT product_id,product_name,quantity_milli
                    FROM restaurant_session_items
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE'
                      AND fiscal_state='PENDING';
                    """;
                read.Parameters.AddWithValue("$item", sessionItemId);
                read.Parameters.AddWithValue("$session", sessionId);

                await using var r = await read.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                {
                    await tx.CommitAsync(ct);
                    return false;
                }

                productId = r.GetInt64(0);
                productName = r.GetString(1);
                quantityMilli = r.GetInt64(2);
            }

            await using (var delete = c.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = """
                    DELETE FROM restaurant_session_items
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE'
                      AND fiscal_state='PENDING';
                    """;
                delete.Parameters.AddWithValue("$item", sessionItemId);
                delete.Parameters.AddWithValue("$session", sessionId);

                if (await delete.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new InvalidOperationException(
                        "Ungesicherte Restaurant-Position wurde zwischenzeitlich geändert.");
                }
            }

            await using (var touch = c.CreateCommand())
            {
                touch.Transaction = tx;
                touch.CommandText = """
                    UPDATE restaurant_sessions
                    SET updated_at=$now,
                        version=version+1
                    WHERE id=$session
                      AND state='OPEN';
                    """;
                touch.Parameters.AddWithValue("$now", now);
                touch.Parameters.AddWithValue("$session", sessionId);
                await touch.ExecuteNonQueryAsync(ct);
            }

            await AppendEventAsync(
                c,
                tx,
                sessionId,
                "POSITION_FISKAL_VERWORFEN",
                actor,
                deviceId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    sessionItemId,
                    productId,
                    productName,
                    quantityMilli
                }),
                now,
                ct);

            await tx.CommitAsync(ct);
            return true;
        });
    }

    public async Task<RestaurantSessionItem?> GetItemAsync(
        string sessionId,
        long sessionItemId,
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0 || sessionItemId <= 0)
            return null;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,session_id,line_token,product_id,product_name,variant_name,
                       quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                       added_by,added_at,version
                FROM restaurant_session_items
                WHERE id=$item
                  AND session_id=$session
                LIMIT 1;
                """;
            q.Parameters.AddWithValue("$item", sessionItemId);
            q.Parameters.AddWithValue("$session", sessionId);

            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            return new RestaurantSessionItem(
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
                Enum.Parse<RestaurantSessionItemState>(
                    r.GetString(10),
                    ignoreCase: true),
                r.GetString(11),
                DateTimeOffset.Parse(r.GetString(12)),
                r.GetInt64(13));
        });
    }

    public async Task<RestaurantSessionItem?> GetItemByLineTokenAsync(
        string lineToken,
        CancellationToken ct = default)
    {
        lineToken = (lineToken ?? "").Trim();
        if (lineToken.Length == 0)
            return null;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,session_id,line_token,product_id,product_name,variant_name,
                       quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                       added_by,added_at,version
                FROM restaurant_session_items
                WHERE line_token=$token
                LIMIT 1;
                """;
            q.Parameters.AddWithValue("$token", lineToken);

            await using var r = await q.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;

            return new RestaurantSessionItem(
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
                Enum.Parse<RestaurantSessionItemState>(
                    r.GetString(10),
                    ignoreCase: true),
                r.GetString(11),
                DateTimeOffset.Parse(r.GetString(12)),
                r.GetInt64(13));
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
                      AND state='OPEN';
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
                      AND state='OPEN';
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
              AND state='OPEN';
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

    public async Task<RestaurantCheckoutDraft> BuildCheckoutDraftAsync(
        string sessionId,
        long expectedSessionVersion,
        IReadOnlyList<RestaurantSplitSelection> selections,
        CancellationToken ct = default)
    {
        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0) throw new ArgumentException("Tischvorgang fehlt.", nameof(sessionId));
        if (expectedSessionVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedSessionVersion));
        if (selections.Count == 0) throw new InvalidOperationException("Keine Position für die Zahlung ausgewählt.");

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            var session = await ReadLiveSessionForUpdateAsync(
                c, tx, sessionId, expectedSessionVersion, ct);

            if (session.State != RestaurantTableSessionState.Open)
                throw new InvalidOperationException("Tisch ist bereits in einem Zahlungs-/Abschlussvorgang.");

            var items = new List<RestaurantSessionItem>();
            foreach (var selection in selections)
            {
                await using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = """
                    SELECT id,session_id,line_token,product_id,product_name,variant_name,
                           quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                           added_by,added_at,version
                    FROM restaurant_session_items
                    WHERE id=$id AND session_id=$session AND state='ACTIVE';
                    """;
                q.Parameters.AddWithValue("$id", selection.SessionItemId);
                q.Parameters.AddWithValue("$session", sessionId);

                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException("Ausgewählte Tischposition ist nicht mehr offen.");

                var item = new RestaurantSessionItem(
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
                    r.GetInt64(13));

                if (selection.QuantityMilli <= 0 ||
                    selection.QuantityMilli > item.QuantityMilli)
                {
                    throw new InvalidOperationException("Ungültige Teilmenge für Splitrechnung.");
                }

                if (!string.IsNullOrWhiteSpace(item.VariantName))
                    throw new InvalidOperationException("Varianten werden in der Restaurant-Zahlung erst nach vollständigem Snapshot-Support freigegeben.");

                items.Add(item);
            }

            var quote = RestaurantSplitCalculator.ByItems(items, selections);
            var byId = quote.Lines.ToDictionary(x => x.SessionItemId);
            var cartLines = new List<CartLine>();

            foreach (var item in items)
            {
                var selected = selections.Single(x => x.SessionItemId == item.Id);
                var split = byId[item.Id];
                var qty = selected.QuantityMilli / 1000m;

                // The Restaurant foundation currently accepts only simple item
                // snapshots. Menu/variant/weighted snapshots will be enabled
                // only after their immutable component metadata is persisted.
                cartLines.Add(new CartLine
                {
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    VariantName = item.VariantName,
                    Quantity = qty,
                    Unit = "Stück",
                    UnitPriceCents = item.UnitPriceCents,
                    ListUnitPriceCents = item.UnitPriceCents,
                    VatRate = item.VatRate,
                    PfandCents = item.PfandCents
                });

                if (cartLines[^1].LineTotalCents != split.AmountCents)
                    throw new InvalidOperationException("Splitbetrag stimmt nicht mit der fiskalen Positionssumme überein.");
            }

            await tx.CommitAsync(ct);

            return new RestaurantCheckoutDraft(
                sessionId,
                expectedSessionVersion,
                Guid.NewGuid().ToString("N"),
                cartLines.ToArray(),
                selections.ToArray());
        });
    }

    public async Task<long> PreparePaymentReservationAsync(
        RestaurantCheckoutDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var active = c.CreateCommand())
            {
                active.Transaction = tx;
                active.CommandText = """
                    SELECT COUNT(*)
                    FROM restaurant_payment_reservations
                    WHERE session_id=$session AND state='PREPARED';
                    """;
                active.Parameters.AddWithValue("$session", draft.SessionId);
                if (Convert.ToInt32(await active.ExecuteScalarAsync(ct)) > 0)
                    throw new InvalidOperationException("Für diesen Tisch läuft bereits eine Zahlung.");
            }

            var session = await ReadLiveSessionForUpdateAsync(
                c, tx, draft.SessionId, draft.SessionVersion, ct);
            if (session.State != RestaurantTableSessionState.Open)
                throw new InvalidOperationException("Tisch ist bereits in einem Zahlungs-/Abschlussvorgang.");

            var quoteItems = new List<RestaurantSessionItem>();
            foreach (var selection in draft.Selections)
            {
                await using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = """
                    SELECT id,session_id,line_token,product_id,product_name,variant_name,
                           quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                           added_by,added_at,version
                    FROM restaurant_session_items
                    WHERE id=$id AND session_id=$session AND state='ACTIVE';
                    """;
                q.Parameters.AddWithValue("$id", selection.SessionItemId);
                q.Parameters.AddWithValue("$session", draft.SessionId);
                await using var r = await q.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException("Ausgewählte Tischposition ist nicht mehr offen.");

                quoteItems.Add(new RestaurantSessionItem(
                    r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
                    r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7),
                    Convert.ToDecimal(r.GetDouble(8)), r.GetInt64(9),
                    RestaurantSessionItemState.Active, r.GetString(11),
                    DateTimeOffset.Parse(r.GetString(12)), r.GetInt64(13)));
            }

            var quote = RestaurantSplitCalculator.ByItems(quoteItems, draft.Selections);
            if (quote.TotalCents != draft.TotalCents)
                throw new InvalidOperationException("Restaurant-Zahlbetrag wurde zwischenzeitlich verändert.");

            await using (var reserve = c.CreateCommand())
            {
                reserve.Transaction = tx;
                reserve.CommandText = """
                    INSERT INTO restaurant_payment_reservations(
                        operation_id,session_id,expected_session_version,state,
                        created_at,updated_at,sale_id)
                    VALUES($operation,$session,$version,'PREPARED',$now,$now,NULL);
                    """;
                reserve.Parameters.AddWithValue("$operation", draft.OperationId);
                reserve.Parameters.AddWithValue("$session", draft.SessionId);
                reserve.Parameters.AddWithValue("$version", draft.SessionVersion + 1);
                reserve.Parameters.AddWithValue("$now", now);
                await reserve.ExecuteNonQueryAsync(ct);
            }

            foreach (var line in quote.Lines)
            {
                await using var q = c.CreateCommand();
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO restaurant_payment_reservation_items(
                        operation_id,session_item_id,quantity_milli,amount_cents)
                    VALUES($operation,$item,$quantity,$amount);
                    """;
                q.Parameters.AddWithValue("$operation", draft.OperationId);
                q.Parameters.AddWithValue("$item", line.SessionItemId);
                q.Parameters.AddWithValue("$quantity", line.QuantityMilli);
                q.Parameters.AddWithValue("$amount", line.AmountCents);
                await q.ExecuteNonQueryAsync(ct);
            }

            await using (var lockSession = c.CreateCommand())
            {
                lockSession.Transaction = tx;
                lockSession.CommandText = """
                    UPDATE restaurant_sessions
                    SET state='CHECK_REQUESTED',
                        updated_at=$now,
                        version=version+1
                    WHERE id=$id AND version=$version AND state='OPEN';
                    """;
                lockSession.Parameters.AddWithValue("$now", now);
                lockSession.Parameters.AddWithValue("$id", draft.SessionId);
                lockSession.Parameters.AddWithValue("$version", draft.SessionVersion);
                if (await lockSession.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException("Tisch wurde zwischenzeitlich geändert. Zahlung nicht gestartet.");
            }

            await AppendEventAsync(
                c, tx, draft.SessionId, "ZAHLUNG_VORBEREITET",
                "", Environment.MachineName,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    draft.OperationId,
                    totalCents = draft.TotalCents
                }),
                now, ct);

            await tx.CommitAsync(ct);
            return draft.SessionVersion + 1;
        });
    }

    public async Task<bool> HasPreparedPaymentReservationAsync(
        string operationId,
        CancellationToken ct = default)
    {
        operationId = (operationId ?? "").Trim();
        if (operationId.Length == 0) return false;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT COUNT(*)
                FROM restaurant_payment_reservations
                WHERE operation_id=$operation AND state='PREPARED';
                """;
            q.Parameters.AddWithValue("$operation", operationId);
            return Convert.ToInt32(await q.ExecuteScalarAsync(ct)) == 1;
        });
    }

    public async Task CancelPaymentReservationAsync(
        string operationId,
        CancellationToken ct = default)
    {
        operationId = (operationId ?? "").Trim();
        if (operationId.Length == 0) return;

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            string? sessionId = null;
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT session_id
                    FROM restaurant_payment_reservations
                    WHERE operation_id=$operation AND state='PREPARED';
                    """;
                read.Parameters.AddWithValue("$operation", operationId);
                sessionId = (string?)await read.ExecuteScalarAsync(ct);
            }

            if (sessionId is null)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            await using (var cancel = c.CreateCommand())
            {
                cancel.Transaction = tx;
                cancel.CommandText = """
                    UPDATE restaurant_payment_reservations
                    SET state='CANCELLED',updated_at=$now
                    WHERE operation_id=$operation AND state='PREPARED';
                    """;
                cancel.Parameters.AddWithValue("$now", now);
                cancel.Parameters.AddWithValue("$operation", operationId);
                await cancel.ExecuteNonQueryAsync(ct);
            }

            await using (var unlock = c.CreateCommand())
            {
                unlock.Transaction = tx;
                unlock.CommandText = """
                    UPDATE restaurant_sessions
                    SET state='OPEN',updated_at=$now,version=version+1
                    WHERE id=$session AND state='CHECK_REQUESTED';
                    """;
                unlock.Parameters.AddWithValue("$now", now);
                unlock.Parameters.AddWithValue("$session", sessionId);
                await unlock.ExecuteNonQueryAsync(ct);
            }

            await AppendEventAsync(
                c, tx, sessionId, "ZAHLUNG_ABGEBROCHEN",
                "", Environment.MachineName,
                System.Text.Json.JsonSerializer.Serialize(new { operationId }),
                now, ct);

            await tx.CommitAsync(ct);
        });
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
