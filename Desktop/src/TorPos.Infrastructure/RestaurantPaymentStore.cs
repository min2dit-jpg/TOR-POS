using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Applies a prepared Restaurant payment inside the same SQLite transaction
/// that commits the fiscal sale. This prevents a completed payment from
/// leaving the table positions apparently unpaid after a crash.
/// </summary>
internal static class RestaurantPaymentStore
{
    public static async Task<DateTimeOffset?> ApplyCommittedSaleAsync(
        SqliteConnection c,
        SqliteTransaction tx,
        CheckoutSnapshot snapshot,
        long saleId,
        CancellationToken ct)
    {
        await using var reservation = c.CreateCommand();
        reservation.Transaction = tx;
        reservation.CommandText = """
            SELECT session_id,expected_session_version,state
            FROM restaurant_payment_reservations
            WHERE operation_id=$operation;
            """;
        reservation.Parameters.AddWithValue("$operation", snapshot.OperationId);

        string? sessionId = null;
        long expectedSessionVersion = 0;
        string? state = null;

        await using (var r = await reservation.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                sessionId = r.GetString(0);
                expectedSessionVersion = r.GetInt64(1);
                state = r.GetString(2);
            }
        }

        // Normal Einzelhandel/Gastro checkout: no Restaurant reservation.
        if (sessionId is null)
            return null;

        if (state == "APPLIED")
        {
            await using var appliedStart = c.CreateCommand();
            appliedStart.Transaction = tx;
            appliedStart.CommandText = """
                SELECT MIN(started_at)
                FROM restaurant_bestellungen
                WHERE session_id=$session;
                """;
            appliedStart.Parameters.AddWithValue("$session", sessionId);
            var appliedRaw = await appliedStart.ExecuteScalarAsync(ct);
            return appliedRaw is null || appliedRaw == DBNull.Value
                ? null
                : DateTimeOffset.Parse(Convert.ToString(appliedRaw)!);
        }

        if (state != "PREPARED")
            throw new InvalidOperationException(
                "Restaurant-Zahlungsreservierung ist nicht mehr aktiv.");

        var reservedTotal = 0L;
        var reservations = new List<(long ItemId,long QuantityMilli,long AmountCents)>();

        await using (var items = c.CreateCommand())
        {
            items.Transaction = tx;
            items.CommandText = """
                SELECT session_item_id,quantity_milli,amount_cents
                FROM restaurant_payment_reservation_items
                WHERE operation_id=$operation
                ORDER BY session_item_id;
                """;
            items.Parameters.AddWithValue("$operation", snapshot.OperationId);

            await using var r = await items.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var row = (
                    r.GetInt64(0),
                    r.GetInt64(1),
                    r.GetInt64(2));
                reservations.Add(row);
                reservedTotal += row.Item3;
            }
        }

        if (reservations.Count == 0 ||
            reservedTotal != snapshot.TotalCents)
        {
            throw new InvalidOperationException(
                "Restaurant-Zahlbetrag stimmt nicht mit der Checkout-Reservierung überein.");
        }

        await using (var sessionCheck = c.CreateCommand())
        {
            sessionCheck.Transaction = tx;
            sessionCheck.CommandText = """
                SELECT COUNT(*)
                FROM restaurant_sessions
                WHERE id=$session
                  AND version=$version
                  AND state='CHECK_REQUESTED';
                """;
            sessionCheck.Parameters.AddWithValue("$session", sessionId);
            sessionCheck.Parameters.AddWithValue("$version", expectedSessionVersion);
            if (Convert.ToInt32(await sessionCheck.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException(
                    "Restaurant-Tisch wurde während der Zahlung verändert.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var reserved in reservations)
        {
            long currentQuantity;
            long currentLineTotalCents;
            long currentPaidCents;
            await using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = """
                    SELECT quantity_milli,line_total_cents,paid_cents
                    FROM restaurant_session_items
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE';
                    """;
                read.Parameters.AddWithValue("$item", reserved.ItemId);
                read.Parameters.AddWithValue("$session", sessionId);
                await using var row = await read.ExecuteReaderAsync(ct);
                if (!await row.ReadAsync(ct))
                    throw new InvalidOperationException(
                        "Restaurant-Zahlungsposition ist nicht mehr offen.");
                currentQuantity = row.GetInt64(0);
                currentLineTotalCents = row.GetInt64(1);
                currentPaidCents = row.GetInt64(2);
            }

            var remainingCents =
                currentLineTotalCents >= 0
                    ? currentLineTotalCents - currentPaidCents
                    : reserved.AmountCents;

            if (remainingCents < 0 ||
                reserved.AmountCents <= 0 ||
                reserved.AmountCents > remainingCents)
            {
                throw new InvalidOperationException(
                    "Restaurant-Zahlbetrag überschreitet den offenen Positionsbetrag.");
            }

            if (reserved.QuantityMilli <= 0 ||
                reserved.QuantityMilli > currentQuantity)
            {
                throw new InvalidOperationException(
                    "Restaurant-Zahlungsmenge ist nicht mehr verfügbar.");
            }

            await using var update = c.CreateCommand();
            update.Transaction = tx;

            if (reserved.QuantityMilli == currentQuantity)
            {
                update.CommandText = """
                    UPDATE restaurant_session_items
                    SET state='PAID',
                        line_total_cents=$paidAmount,
                        paid_cents=0,
                        version=version+1
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE'
                      AND quantity_milli=$expected;
                    """;
            }
            else
            {
                update.CommandText = """
                    UPDATE restaurant_session_items
                    SET quantity_milli=quantity_milli-$paid,
                        paid_cents=paid_cents+$paidAmount,
                        version=version+1
                    WHERE id=$item
                      AND session_id=$session
                      AND state='ACTIVE'
                      AND quantity_milli=$expected;
                    """;
                update.Parameters.AddWithValue("$paid", reserved.QuantityMilli);
            }

            update.Parameters.AddWithValue("$item", reserved.ItemId);
            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$expected", currentQuantity);
            update.Parameters.AddWithValue("$paidAmount", reserved.AmountCents);

            if (await update.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException(
                    "Restaurant-Zahlungsposition wurde parallel verändert.");

            if (reserved.QuantityMilli < currentQuantity)
            {
                await using var paidSlice = c.CreateCommand();
                paidSlice.Transaction = tx;
                paidSlice.CommandText = """
                    INSERT INTO restaurant_session_items(
                        session_id,line_token,product_id,product_name,variant_name,
                        quantity_milli,unit_price_cents,vat_rate,pfand_cents,state,
                        added_by,added_at,version,line_total_cents,paid_cents)
                    SELECT
                        session_id,$token,product_id,product_name,variant_name,
                        $paid,unit_price_cents,vat_rate,pfand_cents,'PAID',
                        added_by,$now,1,$paidAmount,0
                    FROM restaurant_session_items
                    WHERE id=$item AND session_id=$session;
                    """;
                paidSlice.Parameters.AddWithValue("$token", Guid.NewGuid().ToString("N"));
                paidSlice.Parameters.AddWithValue("$paid", reserved.QuantityMilli);
                paidSlice.Parameters.AddWithValue("$paidAmount", reserved.AmountCents);
                paidSlice.Parameters.AddWithValue("$now", now);
                paidSlice.Parameters.AddWithValue("$item", reserved.ItemId);
                paidSlice.Parameters.AddWithValue("$session", sessionId);
                if (await paidSlice.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Bezahlte Restaurant-Teilmenge konnte nicht historisiert werden.");
            }
        }

        int openCount;
        await using (var open = c.CreateCommand())
        {
            open.Transaction = tx;
            open.CommandText = """
                SELECT COUNT(*)
                FROM restaurant_session_items
                WHERE session_id=$session AND state='ACTIVE';
                """;
            open.Parameters.AddWithValue("$session", sessionId);
            openCount = Convert.ToInt32(await open.ExecuteScalarAsync(ct));
        }

        await using (var completeSession = c.CreateCommand())
        {
            completeSession.Transaction = tx;
            completeSession.CommandText = openCount == 0
                ? """
                    UPDATE restaurant_sessions
                    SET state='CLOSED',
                        closed_at=$now,
                        updated_at=$now,
                        version=version+1
                    WHERE id=$session
                      AND version=$version
                      AND state='CHECK_REQUESTED';
                    """
                : """
                    UPDATE restaurant_sessions
                    SET state='OPEN',
                        updated_at=$now,
                        version=version+1
                    WHERE id=$session
                      AND version=$version
                      AND state='CHECK_REQUESTED';
                    """;
            completeSession.Parameters.AddWithValue("$now", now);
            completeSession.Parameters.AddWithValue("$session", sessionId);
            completeSession.Parameters.AddWithValue("$version", expectedSessionVersion);
            if (await completeSession.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException(
                    "Restaurant-Tisch konnte nach Zahlung nicht aktualisiert werden.");
        }

        await using (var applied = c.CreateCommand())
        {
            applied.Transaction = tx;
            applied.CommandText = """
                UPDATE restaurant_payment_reservations
                SET state='APPLIED',
                    sale_id=$sale,
                    updated_at=$now
                WHERE operation_id=$operation
                  AND state='PREPARED';
                """;
            applied.Parameters.AddWithValue("$sale", saleId);
            applied.Parameters.AddWithValue("$now", now);
            applied.Parameters.AddWithValue("$operation", snapshot.OperationId);
            if (await applied.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException(
                    "Restaurant-Zahlungsreservierung konnte nicht abgeschlossen werden.");
        }

        await using (var evt = c.CreateCommand())
        {
            evt.Transaction = tx;
            evt.CommandText = """
                INSERT INTO restaurant_session_events(
                    session_id,event_type,actor,device_id,created_at,payload_json)
                VALUES($session,'ZAHLUNG_ANGEWENDET',$actor,$device,$now,$payload);
                """;
            evt.Parameters.AddWithValue("$session", sessionId);
            evt.Parameters.AddWithValue("$actor", snapshot.OperatorName ?? "");
            evt.Parameters.AddWithValue("$device", Environment.MachineName);
            evt.Parameters.AddWithValue("$now", now);
            evt.Parameters.AddWithValue(
                "$payload",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    snapshot.OperationId,
                    saleId,
                    totalCents = snapshot.TotalCents,
                    tableClosed = openCount == 0
                }));
            await evt.ExecuteNonQueryAsync(ct);
        }

        await using var start = c.CreateCommand();
        start.Transaction = tx;
        start.CommandText = """
            SELECT MIN(started_at)
            FROM restaurant_bestellungen
            WHERE session_id=$session;
            """;
        start.Parameters.AddWithValue("$session", sessionId);
        var rawStart = await start.ExecuteScalarAsync(ct);
        return rawStart is null || rawStart == DBNull.Value
            ? null
            : DateTimeOffset.Parse(Convert.ToString(rawStart)!);
    }
}
