using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantKitchenBoardItem(
    long SessionItemId,
    string SessionId,
    string TableName,
    string ProductName,
    decimal Quantity,
    string Waiter,
    string Note,
    string Status,
    string Station,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt);

public sealed record RestaurantKitchenJob(
    string Id,
    string SessionId,
    long? SessionItemId,
    string Action,
    string Station,
    string PrinterName,
    string PayloadJson,
    string State,
    int Attempts,
    string LastError,
    DateTimeOffset CreatedAt);

public sealed record RestaurantKitchenCancellationAlert(
    string JobId,
    long SessionItemId,
    string SessionId,
    string TableName,
    string ProductName,
    decimal Quantity,
    string Waiter,
    string Station,
    DateTimeOffset CreatedAt);

public sealed class RestaurantKitchenOutbox
{
    private readonly SqliteDatabase _db;

    public RestaurantKitchenOutbox(SqliteDatabase db)
    {
        _db = db;
    }

    public Task<string> EnqueueNewItemAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string station = "",
        CancellationToken ct = default) =>
        EnqueueNewItemCoreAsync(
            session,
            item,
            tableName,
            actor,
            station,
            jobId: null,
            ct);

    public Task<string> EnqueueNewItemIdempotentAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string jobId,
        string station = "",
        CancellationToken ct = default)
    {
        jobId = (jobId ?? "").Trim();
        if (jobId.Length is < 8 or > 160)
            throw new ArgumentException(
                "Kitchen-Job-ID ist ungültig.",
                nameof(jobId));

        return EnqueueNewItemCoreAsync(
            session,
            item,
            tableName,
            actor,
            station,
            jobId,
            ct);
    }

    private async Task<string> EnqueueNewItemCoreAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string station,
        string? jobId,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "RESTAURANT_KITCHEN",
            action = "NEW",
            sessionId = session.Id,
            tableId = session.TableId,
            tableName,
            waiter = session.AssignedWaiter,
            guestCount = session.GuestCount,
            note = session.Note,
            itemId = item.Id,
            item.ProductName,
            item.VariantName,
            item.QuantityMilli,
            item.UnitPriceCents,
            actor,
            station = KitchenStations.Normalize(station)
        });

        return await EnqueueAsync(
            session.Id,
            item.Id,
            "NEW",
            station: KitchenStations.Normalize(station),
            printerName: "",
            payload,
            jobId,
            ct);
    }

    public Task<string> EnqueueCancellationAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string station = "",
        CancellationToken ct = default) =>
        EnqueueCancellationCoreAsync(
            session,
            item,
            tableName,
            actor,
            station,
            jobId: null,
            ct);

    public Task<string> EnqueueCancellationIdempotentAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string jobId,
        string station = "",
        CancellationToken ct = default)
    {
        jobId = (jobId ?? "").Trim();
        if (jobId.Length is < 8 or > 160)
            throw new ArgumentException(
                "Kitchen-Job-ID ist ungültig.",
                nameof(jobId));

        return EnqueueCancellationCoreAsync(
            session,
            item,
            tableName,
            actor,
            station,
            jobId,
            ct);
    }

    private async Task<string> EnqueueCancellationCoreAsync(
        RestaurantTableSession session,
        RestaurantSessionItem item,
        string tableName,
        string actor,
        string station,
        string? jobId,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "RESTAURANT_KITCHEN",
            action = "CANCEL",
            sessionId = session.Id,
            tableId = session.TableId,
            tableName,
            waiter = session.AssignedWaiter,
            note = session.Note,
            itemId = item.Id,
            item.ProductName,
            item.VariantName,
            item.QuantityMilli,
            actor,
            station = KitchenStations.Normalize(station)
        });

        return await EnqueueAsync(
            session.Id,
            item.Id,
            "CANCEL",
            station: KitchenStations.Normalize(station),
            printerName: "",
            payload,
            jobId,
            ct);
    }

    public async Task<string> EnqueueNoteAsync(
        RestaurantTableSession session,
        string tableName,
        string actor,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = "RESTAURANT_KITCHEN",
            action = "NOTE",
            sessionId = session.Id,
            tableId = session.TableId,
            tableName,
            waiter = session.AssignedWaiter,
            guestCount = session.GuestCount,
            note = session.Note,
            itemId = 0L,
            ProductName = "TISCHNOTIZ AKTUALISIERT",
            VariantName = "",
            QuantityMilli = 1000L,
            UnitPriceCents = 0L,
            actor,
            station = KitchenStations.None
        });

        return await EnqueueAsync(
            session.Id,
            null,
            "NOTE",
            station: KitchenStations.None,
            printerName: "",
            payload,
            ct);
    }

    public async Task<IReadOnlyList<RestaurantKitchenJob>> PendingAsync(
        CancellationToken ct = default)
    {
        var result = new List<RestaurantKitchenJob>();
        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT id,session_id,session_item_id,action,station,printer_name,
                   payload_json,state,attempts,last_error,created_at
            FROM restaurant_kitchen_jobs
            WHERE state='PENDING'
            ORDER BY created_at,id
            LIMIT 50;
            """;

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new RestaurantKitchenJob(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                r.GetString(6),
                r.GetString(7),
                r.GetInt32(8),
                r.GetString(9),
                DateTimeOffset.Parse(r.GetString(10))));
        }

        return result;
    }

    public Task MarkHandedOverAsync(
        string id,
        CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_kitchen_jobs
                SET state='HANDED_OVER',
                    handed_over_at=$now,
                    last_error=''
                WHERE id=$id AND state='PENDING';
                """;
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await q.ExecuteNonQueryAsync(ct);
        });

    public Task<bool> MarkFailedAttemptAsync(
        string id,
        string error,
        CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            int attempts;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE restaurant_kitchen_jobs
                    SET attempts=attempts+1,
                        last_error=$error
                    WHERE id=$id AND state='PENDING';

                    SELECT COALESCE(
                        (SELECT attempts FROM restaurant_kitchen_jobs WHERE id=$id),
                        0);
                    """;
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue(
                    "$error",
                    (error ?? "").Length > 500
                        ? error![..500]
                        : error ?? "");
                attempts = Convert.ToInt32(await q.ExecuteScalarAsync(ct));
            }

            var failed = attempts >= 5;
            if (failed)
            {
                await using var park = c.CreateCommand();
                park.Transaction = tx;
                park.CommandText = """
                    UPDATE restaurant_kitchen_jobs
                    SET state='FAILED'
                    WHERE id=$id AND state='PENDING';
                    """;
                park.Parameters.AddWithValue("$id", id);
                await park.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return failed;
        });

    public async Task<IReadOnlyList<RestaurantKitchenCancellationAlert>> CancellationAlertsAsync(
        string? station = null,
        CancellationToken ct = default)
    {
        var normalized = KitchenStations.Normalize(station);
        var result = new List<RestaurantKitchenCancellationAlert>();

        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT
                j.id,
                i.id,
                j.session_id,
                t.display_name,
                i.product_name,
                i.quantity_milli,
                s.assigned_waiter,
                j.station,
                j.created_at
            FROM restaurant_kitchen_jobs j
            JOIN restaurant_session_items i
              ON i.id=j.session_item_id
            JOIN restaurant_sessions s
              ON s.id=j.session_id
            JOIN restaurant_tables t
              ON t.id=s.table_id
            WHERE j.action='CANCEL'
              AND j.state='PENDING'
              AND ($station='' OR j.station=$station)
            ORDER BY j.created_at,j.id;
            """;
        q.Parameters.AddWithValue("$station", normalized);

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new RestaurantKitchenCancellationAlert(
                r.GetString(0),
                r.GetInt64(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetInt64(5) / 1000m,
                r.GetString(6),
                r.GetString(7),
                DateTimeOffset.Parse(r.GetString(8))));
        }

        return result;
    }

    public async Task<IReadOnlyList<RestaurantKitchenBoardItem>> BoardAsync(
        string? station = null,
        CancellationToken ct = default)
    {
        var normalized = KitchenStations.Normalize(station);
        var result = new List<RestaurantKitchenBoardItem>();

        await using var c = _db.OpenReadConnection();
        await using var q = c.CreateCommand();
        q.CommandText = """
            SELECT
                i.id,
                i.session_id,
                t.display_name,
                i.product_name,
                i.quantity_milli,
                s.assigned_waiter,
                s.note,
                COALESCE(k.status,'OFFEN'),
                COALESCE(
                    (SELECT j.station
                     FROM restaurant_kitchen_jobs j
                     WHERE j.session_item_id=i.id
                       AND j.action='NEW'
                     ORDER BY j.created_at
                     LIMIT 1),
                    ''),
                i.added_at,
                COALESCE(k.updated_at,i.added_at)
            FROM restaurant_session_items i
            JOIN restaurant_sessions s ON s.id=i.session_id
            JOIN restaurant_tables t ON t.id=s.table_id
            LEFT JOIN restaurant_kitchen_status k
              ON k.session_item_id=i.id
            WHERE i.state='ACTIVE'
              AND ($station='' OR
                   COALESCE(
                       (SELECT j.station
                        FROM restaurant_kitchen_jobs j
                        WHERE j.session_item_id=i.id
                          AND j.action='NEW'
                        ORDER BY j.created_at
                        LIMIT 1),
                       '')=$station)
            ORDER BY
                CASE COALESCE(k.status,'OFFEN')
                    WHEN 'OFFEN' THEN 0
                    WHEN 'IN_ARBEIT' THEN 1
                    ELSE 2
                END,
                i.added_at,
                i.id;
            """;
        q.Parameters.AddWithValue("$station", normalized);

        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result.Add(new RestaurantKitchenBoardItem(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetInt64(4) / 1000m,
                r.GetString(5),
                r.GetString(6),
                r.GetString(7),
                r.GetString(8),
                DateTimeOffset.Parse(r.GetString(9)),
                DateTimeOffset.Parse(r.GetString(10))));
        }

        return result;
    }

    public Task SetItemStatusAsync(
        long sessionItemId,
        string status,
        string actor,
        CancellationToken ct = default)
    {
        status = (status ?? "").Trim().ToUpperInvariant();
        if (status is not ("OFFEN" or "IN_ARBEIT" or "FERTIG"))
            throw new ArgumentOutOfRangeException(nameof(status));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_kitchen_status(
                    session_item_id,status,updated_at,updated_by)
                VALUES($item,$status,$now,$actor)
                ON CONFLICT(session_item_id) DO UPDATE SET
                    status=excluded.status,
                    updated_at=excluded.updated_at,
                    updated_by=excluded.updated_by;
                """;
            q.Parameters.AddWithValue("$item", sessionItemId);
            q.Parameters.AddWithValue("$status", status);
            q.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue("$actor", actor ?? "");
            await q.ExecuteNonQueryAsync(ct);
        });
    }

    private Task<string> EnqueueAsync(
        string sessionId,
        long? sessionItemId,
        string action,
        string station,
        string printerName,
        string payload,
        CancellationToken ct) =>
        EnqueueAsync(
            sessionId,
            sessionItemId,
            action,
            station,
            printerName,
            payload,
            requestedId: null,
            ct);

    private Task<string> EnqueueAsync(
        string sessionId,
        long? sessionItemId,
        string action,
        string station,
        string printerName,
        string payload,
        string? requestedId,
        CancellationToken ct)
    {
        return IoQueue.RunAsync(async () =>
        {
            var id = string.IsNullOrWhiteSpace(requestedId)
                ? Guid.NewGuid().ToString("N")
                : requestedId.Trim();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT OR IGNORE INTO restaurant_kitchen_jobs(
                        id,session_id,session_item_id,action,station,printer_name,
                        payload_json,state,attempts,last_error,created_at)
                    VALUES(
                        $id,$session,$item,$action,$station,$printer,
                        $payload,'PENDING',0,'',$created);
                    """;
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue("$session", sessionId);
                q.Parameters.AddWithValue(
                    "$item",
                    sessionItemId is long value ? value : DBNull.Value);
                q.Parameters.AddWithValue("$action", action);
                q.Parameters.AddWithValue("$station", station ?? "");
                q.Parameters.AddWithValue("$printer", printerName ?? "");
                q.Parameters.AddWithValue("$payload", payload);
                q.Parameters.AddWithValue("$created", now);
                await q.ExecuteNonQueryAsync(ct);
            }

            if (sessionItemId is long itemId && action == "NEW")
            {
                await using var status = c.CreateCommand();
                status.Transaction = tx;
                status.CommandText = """
                    INSERT INTO restaurant_kitchen_status(
                        session_item_id,status,updated_at,updated_by)
                    VALUES($item,'OFFEN',$now,'')
                    ON CONFLICT(session_item_id) DO NOTHING;
                    """;
                status.Parameters.AddWithValue("$item", itemId);
                status.Parameters.AddWithValue("$now", now);
                await status.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return id;
        });
    }
}
