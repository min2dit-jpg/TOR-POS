using System.Globalization;
using System.Text.Json;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>One waiter's line of the Kellnerabrechnung.</summary>
public sealed record RestaurantWaiterSettlementRow(
    string Waiter,
    int SaleCount,
    long SalesCents,
    long StornoReturnCents,
    long CashCents,
    long CardCents,
    int OpenTables,
    long OpenTablesCents,
    int CancelledPositions,
    long CancelledPositionsCents)
{
    /// <summary>Turnover after Storno/Retoure - equals Bar + Karte, as on the X report.</summary>
    public long NetCents => SalesCents - StornoReturnCents;
}

public sealed record RestaurantWaiterSettlement(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<RestaurantWaiterSettlementRow> Rows)
{
    public long TotalCashCents => Rows.Sum(x => x.CashCents);
    public long TotalCardCents => Rows.Sum(x => x.CardCents);
}

/// <summary>
/// R-5.1: Kellnerabrechnung - read only. For the open Z period (from the last
/// Tagesabschluss, like the X report) it shows per person
///   * the Bons they cashed: sales, Storno/Retoure, Bar and Karte - with the
///     X report's rules (R101 split, R141 reversals taken off their payment type),
///     attributed to the operator recorded with the sale (sale_operators);
///   * the tables still open under their name (restaurant_sessions.assigned_waiter)
///     and the value of their open positions;
///   * the positions they cancelled on a table (POSITION_STORNIERT events).
/// It opens only read connections and writes nothing - no fiscal table, no
/// payment record, no audit. Training receipts live in their own tables and
/// are not part of it.
/// </summary>
public sealed class RestaurantWaiterSettlementService
{
    private readonly SqliteDatabase _db;

    public RestaurantWaiterSettlementService(SqliteDatabase db) => _db = db;

    public Task<RestaurantWaiterSettlement> BuildForOpenPeriodAsync(CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            var from = DateTimeOffset.MinValue;
            await using (var c = _db.OpenReadConnection())
            await using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT closed_at FROM daily_closings ORDER BY id DESC LIMIT 1;";
                if (await q.ExecuteScalarAsync(ct) is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                    from = parsed;
            }
            return await BuildCoreAsync(from, DateTimeOffset.Now, ct);
        });

    public Task<RestaurantWaiterSettlement> BuildAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (to < from) throw new ArgumentException("Zeitraum ungültig.");
        return IoQueue.RunAsync(() => BuildCoreAsync(from, to, ct));
    }

    private async Task<RestaurantWaiterSettlement> BuildCoreAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = new Dictionary<string, RestaurantWaiterSettlementRow>(StringComparer.OrdinalIgnoreCase);
        RestaurantWaiterSettlementRow Row(string waiter)
        {
            var key = string.IsNullOrWhiteSpace(waiter) ? "(ohne Bediener)" : waiter.Trim();
            return rows.TryGetValue(key, out var row) ? row : rows[key] = new(key, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        await using var c = _db.OpenReadConnection();

        // 1) Cashed Bons of the period, by the operator recorded with the sale.
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT COALESCE(o.operator_name,''),
                    COALESCE(SUM(CASE WHEN COALESCE(s.transaction_type,'SALE')='SALE' THEN 1 ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN COALESCE(s.transaction_type,'SALE')='SALE' THEN s.total_cents ELSE 0 END),0),
                    COALESCE(SUM(CASE WHEN s.transaction_type IN ('STORNO','RETURN') THEN s.total_cents ELSE 0 END),0),
                    COALESCE(SUM(
                        CASE WHEN COALESCE(s.transaction_type,'SALE')='SALE' THEN 1
                             WHEN s.transaction_type IN ('STORNO','RETURN') THEN -1 ELSE 0 END *
                        CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.cash_portion_cents
                             WHEN s.payment_method='CASH' THEN s.total_cents ELSE 0 END),0),
                    COALESCE(SUM(
                        CASE WHEN COALESCE(s.transaction_type,'SALE')='SALE' THEN 1
                             WHEN s.transaction_type IN ('STORNO','RETURN') THEN -1 ELSE 0 END *
                        CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.card_portion_cents
                             WHEN s.payment_method='CARD' THEN s.total_cents ELSE 0 END),0)
                FROM sales s
                LEFT JOIN sale_operators o ON o.sale_id=s.id
                WHERE s.created_at_utc >= $from AND s.created_at_utc <= $to
                GROUP BY COALESCE(o.operator_name,'');
                """;
            q.Parameters.AddWithValue("$from", UtcText(from));
            q.Parameters.AddWithValue("$to", UtcText(to));
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var row = Row(r.GetString(0));
                rows[row.Waiter] = row with
                {
                    SaleCount = row.SaleCount + Convert.ToInt32(r.GetInt64(1)),
                    SalesCents = row.SalesCents + r.GetInt64(2),
                    StornoReturnCents = row.StornoReturnCents + r.GetInt64(3),
                    CashCents = row.CashCents + r.GetInt64(4),
                    CardCents = row.CardCents + r.GetInt64(5)
                };
            }
        }

        // 2) Tables still open now, under their assigned waiter.
        if (await TableExistsAsync(c, "restaurant_sessions", ct))
        {
            var openBySession = new Dictionary<string, (string Waiter, long Cents)>(StringComparer.Ordinal);
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT s.id, s.assigned_waiter,
                           CASE
                             WHEN i.id IS NULL THEN 0
                             WHEN i.line_total_cents>=0
                               THEN i.line_total_cents-i.paid_cents
                             ELSE CAST(ROUND((i.quantity_milli*i.unit_price_cents)/1000.0) AS INTEGER)
                           END
                    FROM restaurant_sessions s
                    LEFT JOIN restaurant_session_items i
                      ON i.session_id=s.id
                     AND i.state='ACTIVE'
                    WHERE s.state IN ('OPEN','CHECK_REQUESTED');
                    """;
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var id = r.GetString(0);
                    var cents = r.IsDBNull(2) ? 0L : r.GetInt64(2);
                    var current = openBySession.GetValueOrDefault(id, (r.GetString(1), 0L));
                    openBySession[id] = (current.Item1, current.Item2 + cents);
                }
            }
            foreach (var (waiter, cents) in openBySession.Values)
            {
                var row = Row(waiter);
                rows[row.Waiter] = row with { OpenTables = row.OpenTables + 1, OpenTablesCents = row.OpenTablesCents + cents };
            }

            // 3) Positions cancelled on a table in the period, by who cancelled.
            await using (var q = c.CreateCommand())
            {
                // Coarse text pre-filter (one day of slack for any offset);
                // the exact instant is compared below.
                q.CommandText = """
                    SELECT actor, created_at, payload_json
                    FROM restaurant_session_events
                    WHERE event_type='POSITION_STORNIERT' AND created_at >= $fromDay;
                    """;
                q.Parameters.AddWithValue("$fromDay", from == DateTimeOffset.MinValue
                    ? ""
                    : from.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (!DateTimeOffset.TryParse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ||
                        at < from || at > to)
                        continue;
                    long cents;
                    try
                    {
                        using var payload = JsonDocument.Parse(r.GetString(2));
                        if (!payload.RootElement.TryGetProperty("LineTotalCents", out var total) ||
                            !total.TryGetInt64(out cents))
                            throw new InvalidOperationException(
                                $"Kellnerabrechnung: Storno-Daten für {r.GetString(0)} enthalten keinen gültigen Betrag.");
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidOperationException(
                            $"Kellnerabrechnung: Storno-Daten für {r.GetString(0)} sind beschädigt.",
                            ex);
                    }
                    var row = Row(r.GetString(0));
                    rows[row.Waiter] = row with
                    {
                        CancelledPositions = row.CancelledPositions + 1,
                        CancelledPositionsCents = row.CancelledPositionsCents + cents
                    };
                }
            }
        }

        return new RestaurantWaiterSettlement(
            from,
            to,
            rows.Values.OrderBy(x => x.Waiter, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string UtcText(DateTimeOffset value) =>
        value == DateTimeOffset.MinValue
            ? "0000"
            : value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<bool> TableExistsAsync(Microsoft.Data.Sqlite.SqliteConnection c, string name, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        q.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct)) > 0;
    }

    /// <summary>Plain text for printing or e-mail; German, like every Restaurant screen.</summary>
    public static IReadOnlyList<string> ToText(RestaurantWaiterSettlement settlement)
    {
        string Eur(long cents) => (cents / 100m).ToString("N2", CultureInfo.GetCultureInfo("de-DE")) + " €";
        var lines = new List<string>
        {
            "KELLNERABRECHNUNG",
            settlement.From == DateTimeOffset.MinValue
                ? $"Zeitraum: seit Beginn bis {settlement.To.ToLocalTime():dd.MM.yyyy HH:mm}"
                : $"Zeitraum: {settlement.From.ToLocalTime():dd.MM.yyyy HH:mm} – {settlement.To.ToLocalTime():dd.MM.yyyy HH:mm}",
            "Nur zur Information – ersetzt weder X- noch Z-Bericht.",
            ""
        };
        foreach (var row in settlement.Rows)
        {
            lines.Add(row.Waiter);
            lines.Add($"  Bons: {row.SaleCount} · Umsatz {Eur(row.SalesCents)} · Storno/Retoure {Eur(row.StornoReturnCents)}");
            lines.Add($"  Bar {Eur(row.CashCents)} · Karte {Eur(row.CardCents)}");
            lines.Add($"  Offene Tische: {row.OpenTables} · {Eur(row.OpenTablesCents)}");
            lines.Add($"  Stornierte Tischpositionen: {row.CancelledPositions} · {Eur(row.CancelledPositionsCents)}");
            lines.Add("");
        }
        lines.Add($"Summe Bar {Eur(settlement.TotalCashCents)} · Summe Karte {Eur(settlement.TotalCardCents)}");
        return lines;
    }
}
