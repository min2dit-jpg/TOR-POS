using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantReservation(
    string Id,
    DateTimeOffset ReservationAt,
    int DurationMinutes,
    int GuestCount,
    string CustomerName,
    string Phone,
    string Note,
    long? TableId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CreatedBy,
    string UpdatedBy,
    long Version);

public sealed class RestaurantReservationService
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;

    public RestaurantReservationService(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public Task<RestaurantReservation> CreateAsync(
        DateTimeOffset reservationAt,
        int durationMinutes,
        int guestCount,
        string customerName,
        string phone,
        string note,
        long? tableId,
        string actor,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.Reservierungen);

        customerName = (customerName ?? "").Trim();
        phone = (phone ?? "").Trim();
        note = (note ?? "").Trim();
        actor = (actor ?? "").Trim();

        if (customerName.Length is < 2 or > 160)
            throw new ArgumentException("Kundenname muss 2 bis 160 Zeichen haben.", nameof(customerName));
        if (phone.Length > 80)
            throw new ArgumentException("Telefonnummer ist zu lang.", nameof(phone));
        if (note.Length > 1000)
            throw new ArgumentException("Reservierungsnotiz ist zu lang.", nameof(note));
        if (durationMinutes is < 15 or > 1440)
            throw new ArgumentOutOfRangeException(nameof(durationMinutes));
        if (guestCount is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(guestCount));
        if (actor.Length == 0)
            throw new ArgumentException("Bediener fehlt.", nameof(actor));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            if (tableId is long table)
                await EnsureActiveTableAsync(c, tx, table, ct);

            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    INSERT INTO restaurant_reservations(
                        id,reservation_at,duration_minutes,guest_count,
                        customer_name,phone,note,table_id,status,
                        created_at,updated_at,created_by,updated_by,version)
                    VALUES(
                        $id,$at,$duration,$guests,
                        $name,$phone,$note,$table,'BOOKED',
                        $now,$now,$actor,$actor,1);
                    """;
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue("$at", reservationAt.ToUniversalTime().ToString("O"));
                q.Parameters.AddWithValue("$duration", durationMinutes);
                q.Parameters.AddWithValue("$guests", guestCount);
                q.Parameters.AddWithValue("$name", customerName);
                q.Parameters.AddWithValue("$phone", phone);
                q.Parameters.AddWithValue("$note", note);
                q.Parameters.AddWithValue("$table", tableId is long value ? value : DBNull.Value);
                q.Parameters.AddWithValue("$now", now.ToString("O"));
                q.Parameters.AddWithValue("$actor", actor);
                await q.ExecuteNonQueryAsync(ct);
            }

            var result = await ReadAsync(c, tx, id, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public Task<IReadOnlyList<RestaurantReservation>> ListAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.Reservierungen);

        if (to <= from)
            throw new ArgumentException("Reservierungszeitraum ist ungültig.");

        return IoQueue.RunAsync<IReadOnlyList<RestaurantReservation>>(async () =>
        {
            var result = new List<RestaurantReservation>();
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,reservation_at,duration_minutes,guest_count,
                       customer_name,phone,note,table_id,status,
                       created_at,updated_at,created_by,updated_by,version
                FROM restaurant_reservations
                WHERE reservation_at >= $from
                  AND reservation_at < $to
                ORDER BY reservation_at,customer_name,id;
                """;
            q.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O"));
            q.Parameters.AddWithValue("$to", to.ToUniversalTime().ToString("O"));

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                result.Add(Read(r));

            return result;
        });
    }

    public Task<RestaurantReservation> AssignTableAsync(
        string reservationId,
        long expectedVersion,
        long? tableId,
        string actor,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.Reservierungen);

        reservationId = (reservationId ?? "").Trim();
        actor = (actor ?? "").Trim();
        if (reservationId.Length == 0)
            throw new ArgumentException("Reservierung fehlt.", nameof(reservationId));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (actor.Length == 0)
            throw new ArgumentException("Bediener fehlt.", nameof(actor));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();

            if (tableId is long table)
                await EnsureActiveTableAsync(c, tx, table, ct);

            var now = DateTimeOffset.UtcNow.ToString("O");
            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE restaurant_reservations
                    SET table_id=$table,
                        updated_at=$now,
                        updated_by=$actor,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND status='BOOKED';
                    """;
                q.Parameters.AddWithValue("$table", tableId is long value ? value : DBNull.Value);
                q.Parameters.AddWithValue("$now", now);
                q.Parameters.AddWithValue("$actor", actor);
                q.Parameters.AddWithValue("$id", reservationId);
                q.Parameters.AddWithValue("$version", expectedVersion);
                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Reservierung wurde zwischenzeitlich geändert oder ist nicht mehr offen.");
            }

            var result = await ReadAsync(c, tx, reservationId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    public Task<RestaurantReservation> SetStatusAsync(
        string reservationId,
        long expectedVersion,
        string status,
        string actor,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.Reservierungen);

        reservationId = (reservationId ?? "").Trim();
        status = (status ?? "").Trim().ToUpperInvariant();
        actor = (actor ?? "").Trim();

        if (reservationId.Length == 0)
            throw new ArgumentException("Reservierung fehlt.", nameof(reservationId));
        if (expectedVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (status is not ("SEATED" or "CANCELLED" or "NO_SHOW" or "COMPLETED"))
            throw new ArgumentOutOfRangeException(nameof(status));
        if (actor.Length == 0)
            throw new ArgumentException("Bediener fehlt.", nameof(actor));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow.ToString("O");

            await using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = """
                    UPDATE restaurant_reservations
                    SET status=$status,
                        updated_at=$now,
                        updated_by=$actor,
                        version=version+1
                    WHERE id=$id
                      AND version=$version
                      AND status='BOOKED';
                    """;
                q.Parameters.AddWithValue("$status", status);
                q.Parameters.AddWithValue("$now", now);
                q.Parameters.AddWithValue("$actor", actor);
                q.Parameters.AddWithValue("$id", reservationId);
                q.Parameters.AddWithValue("$version", expectedVersion);
                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Reservierung wurde zwischenzeitlich geändert oder ist nicht mehr offen.");
            }

            var result = await ReadAsync(c, tx, reservationId, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static async Task EnsureActiveTableAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        long tableId,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT COUNT(*)
            FROM restaurant_tables
            WHERE id=$id AND is_active=1;
            """;
        q.Parameters.AddWithValue("$id", tableId);
        if (Convert.ToInt32(await q.ExecuteScalarAsync(ct)) != 1)
            throw new InvalidOperationException(
                "Reservierungstisch ist nicht vorhanden oder deaktiviert.");
    }

    private static async Task<RestaurantReservation> ReadAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        string id,
        CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT id,reservation_at,duration_minutes,guest_count,
                   customer_name,phone,note,table_id,status,
                   created_at,updated_at,created_by,updated_by,version
            FROM restaurant_reservations
            WHERE id=$id;
            """;
        q.Parameters.AddWithValue("$id", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Reservierung nicht gefunden.");
        return Read(r);
    }

    private static RestaurantReservation Read(SqliteDataReader r) =>
        new(
            r.GetString(0),
            DateTimeOffset.Parse(r.GetString(1)),
            r.GetInt32(2),
            r.GetInt32(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.IsDBNull(7) ? null : r.GetInt64(7),
            r.GetString(8),
            DateTimeOffset.Parse(r.GetString(9)),
            DateTimeOffset.Parse(r.GetString(10)),
            r.GetString(11),
            r.GetString(12),
            r.GetInt64(13));
}
