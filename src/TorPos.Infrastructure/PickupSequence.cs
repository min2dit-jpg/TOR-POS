using System.Globalization;
namespace TorPos.Infrastructure;
public static class PickupSequence
{
    public static string BusinessDay(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at,TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")).ToString("yyyyMMdd",CultureInfo.InvariantCulture);
    public static Task<long> NextSimulationAsync(SqliteDatabase db, bool training, DateTimeOffset at) => IoQueue.RunAsync(async () => {
        using var c=db.OpenConnection();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT OR IGNORE INTO app_sequence(key,value) VALUES($key,0); UPDATE app_sequence SET value=value+1 WHERE key=$key; SELECT value FROM app_sequence WHERE key=$key;";
        q.Parameters.AddWithValue("$key",(training?"pickup.training.":"pickup.test.")+BusinessDay(at));
        var value=Convert.ToInt64(await q.ExecuteScalarAsync());tx.Commit();return value;
    });
}
