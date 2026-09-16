using Microsoft.Data.Sqlite;
namespace TorPos.Infrastructure;

/// <summary>
/// R124: the IMBISS pickup number (Abholnummer) counts per SERVICE PERIOD - from
/// one Tagesabschluss to the next - instead of per calendar day.
///
/// Before this, every counter was keyed by the Berlin calendar date
/// ("pickup.20260916"), so an imbiss open past midnight watched its queue jump
/// from 087 back to 001 in the middle of service, while orders numbered 080-087
/// were still waiting at the counter. The audit (finding İ1) listed this next to
/// the Kassensturz/Z-Bericht day boundary that R117 fixed; this uses exactly the
/// same boundary R117 settled on: the most recent row in daily_closings, which
/// the Z-Abschluss writes.
///
/// A till whose operator skips the Tagesabschluss would otherwise count up
/// forever, so the number wraps from 999 back to 001 - the three digits every
/// screen and ticket already prints ({PickupNumber:000}). Reaching 999 orders
/// without a single closing while order 001 of that same run is still open is
/// not a realistic collision.
///
/// All three allocation sites (direct sale, accepted order, simulation) used to
/// carry their own copy of the counter SQL; they now share NextAsync, so the
/// rule cannot drift between them.
/// </summary>
public static class PickupSequence
{
    public const long MaxNumber = 999;

    /// <summary>
    /// The current service period: the id of the most recent daily closing, or
    /// 0 before the first one ever happened.
    /// </summary>
    public static async Task<long> CurrentPeriodAsync(SqliteConnection c, SqliteTransaction? tx, CancellationToken ct = default)
    {
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT COALESCE(MAX(id),0) FROM daily_closings;";
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Allocates the next number inside the caller's transaction, so it commits
    /// or rolls back together with the sale or order it belongs to.
    /// <paramref name="scope"/> keeps real, training and simulation numbers apart:
    /// "" (real), "training." or "test.".
    /// </summary>
    public static async Task<long> NextAsync(SqliteConnection c, SqliteTransaction tx, string scope, CancellationToken ct = default)
    {
        var period = await CurrentPeriodAsync(c, tx, ct);
        await using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            INSERT OR IGNORE INTO app_sequence(key,value) VALUES($key,0);
            UPDATE app_sequence
               SET value = CASE WHEN value >= $max THEN 1 ELSE value + 1 END
             WHERE key=$key;
            SELECT value FROM app_sequence WHERE key=$key;
            """;
        q.Parameters.AddWithValue("$key", $"pickup.{scope}period.{period}");
        q.Parameters.AddWithValue("$max", MaxNumber);
        return Convert.ToInt64(await q.ExecuteScalarAsync(ct));
    }

    public static Task<long> NextSimulationAsync(SqliteDatabase db, bool training) => IoQueue.RunAsync(async () =>
    {
        await using var c = db.OpenConnection();
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
        var value = await NextAsync(c, tx, training ? "training." : "test.");
        await tx.CommitAsync();
        return value;
    });
}
