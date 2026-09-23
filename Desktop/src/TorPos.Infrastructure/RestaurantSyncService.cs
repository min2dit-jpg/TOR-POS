using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record RestaurantSyncEvent(
    long EventId,
    string SessionId,
    string EventType,
    string Actor,
    string DeviceId,
    DateTimeOffset CreatedAt,
    string PayloadJson);

public sealed record RestaurantSyncBatch(
    long AfterEventId,
    long LastEventId,
    IReadOnlyList<RestaurantSyncEvent> Events);

public sealed class RestaurantSyncService
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantEntitlementService _entitlements;

    public RestaurantSyncService(
        SqliteDatabase db,
        RestaurantEntitlementService entitlements)
    {
        _db = db;
        _entitlements = entitlements;
    }

    public Task<RestaurantSyncBatch> GetEventsAfterAsync(
        long afterEventId,
        int limit = 200,
        CancellationToken ct = default)
    {
        _entitlements.Require(RestaurantFeature.MehrereKassen);

        if (afterEventId < 0)
            throw new ArgumentOutOfRangeException(nameof(afterEventId));

        limit = Math.Clamp(limit, 1, 500);

        return IoQueue.RunAsync(async () =>
        {
            var result = new List<RestaurantSyncEvent>(limit);

            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT id,session_id,event_type,actor,device_id,created_at,payload_json
                FROM restaurant_session_events
                WHERE id > $after
                ORDER BY id
                LIMIT $limit;
                """;
            q.Parameters.AddWithValue("$after", afterEventId);
            q.Parameters.AddWithValue("$limit", limit);

            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                result.Add(new RestaurantSyncEvent(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    DateTimeOffset.Parse(r.GetString(5)),
                    r.GetString(6)));
            }

            var last = result.Count == 0
                ? afterEventId
                : result[^1].EventId;

            return new RestaurantSyncBatch(
                afterEventId,
                last,
                result);
        });
    }
}
