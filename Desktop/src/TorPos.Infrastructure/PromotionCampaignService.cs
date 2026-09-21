using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record PromotionCreateRequest(
    string Name,
    int DiscountPercent,
    DateOnly StartDate,
    DateOnly EndDate,
    PromotionScope Scope,
    long TargetId,
    string TargetName);

public sealed class PromotionCampaignService
{
    private static readonly int[] AllowedPercents =
        [10, 15, 20, 25, 30, 40, 50];

    private readonly SqliteDatabase _db;

    public PromotionCampaignService(SqliteDatabase db)
    {
        _db = db;
    }

    /// <summary>
    /// R174: promotions follow the still-open operating/Z period rather than
    /// switching at 00:00 during an overnight service. The first persisted
    /// sale/order after the latest Z close defines that operating day's date.
    /// With no activity in the open period, the current local calendar date is
    /// used so an idle till does not inherit a stale promotion date forever.
    /// </summary>
    public async Task<DateOnly> GetBusinessDateAsync(
        CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();

            DateTimeOffset? lastClose = null;
            await using (var close = c.CreateCommand())
            {
                close.CommandText = """
                    SELECT period_to
                    FROM z_report_archive
                    ORDER BY z_number DESC
                    LIMIT 1;
                    """;
                var value = await close.ExecuteScalarAsync(ct);
                if (value is string text &&
                    DateTimeOffset.TryParse(text, out var parsed))
                {
                    lastClose = parsed;
                }
            }

            await using var activity = c.CreateCommand();
            activity.CommandText = lastClose is null
                ? """
                  SELECT at
                  FROM (
                      SELECT created_at AS at FROM sales
                      UNION ALL
                      SELECT created_at AS at FROM parked_receipts
                  )
                  WHERE at <> ''
                  ORDER BY julianday(at)
                  LIMIT 1;
                  """
                : """
                  SELECT at
                  FROM (
                      SELECT created_at AS at FROM sales
                      UNION ALL
                      SELECT created_at AS at FROM parked_receipts
                  )
                  WHERE at <> ''
                    AND julianday(at) > julianday($close)
                  ORDER BY julianday(at)
                  LIMIT 1;
                  """;

            if (lastClose is not null)
                activity.Parameters.AddWithValue("$close", lastClose.Value.ToString("O"));

            var first = await activity.ExecuteScalarAsync(ct);
            if (first is string firstText &&
                DateTimeOffset.TryParse(firstText, out var firstActivity))
            {
                return DateOnly.FromDateTime(firstActivity.LocalDateTime);
            }

            return DateOnly.FromDateTime(DateTime.Now);
        });
    }

    public async Task<IReadOnlyList<PromotionCampaign>> GetAllAsync(
        CancellationToken ct = default)
    {
        var businessDate = await GetBusinessDateAsync(ct);

        return await IoQueue.RunAsync(async () =>
        {
            var result = new List<PromotionCampaign>();

            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT
                    id,name,discount_percent,start_date,end_date,
                    scope_type,target_id,target_name,is_enabled,
                    created_by,created_at,updated_at
                FROM promotion_campaigns
                ORDER BY
                    CASE WHEN is_enabled=1
                              AND start_date <= $today
                              AND end_date >= $today
                         THEN 0
                         WHEN is_enabled=1 AND start_date > $today
                         THEN 1
                         ELSE 2 END,
                    start_date DESC,
                    id DESC;
                """;
            q.Parameters.AddWithValue(
                "$today",
                businessDate.ToString("yyyy-MM-dd"));

            await using var r = await q.ExecuteReaderAsync(ct);

            while (await r.ReadAsync(ct))
                result.Add(ReadCampaign(r));

            return (IReadOnlyList<PromotionCampaign>)result;
        });
    }

    public async Task<PromotionSnapshot?> GetBestForProductAsync(
        long productId,
        long categoryId,
        CancellationToken ct = default)
    {
        var businessDate = await GetBusinessDateAsync(ct);
        return await GetBestForProductAsync(
            productId,
            categoryId,
            businessDate,
            ct);
    }

    public async Task<PromotionSnapshot?> GetBestForProductAsync(
        long productId,
        long categoryId,
        DateOnly localDate,
        CancellationToken ct = default)
    {
        if (productId <= 0 || categoryId <= 0)
            return null;

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();

            // R71 precedence:
            // 1) Customer gets the highest percentage.
            // 2) On a tie: PRODUCT > CATEGORY > ALL.
            // 3) On another tie: most recently created campaign.
            q.CommandText = """
                SELECT
                    id,name,discount_percent,start_date,end_date,
                    scope_type,target_id
                FROM promotion_campaigns
                WHERE is_enabled=1
                  AND start_date <= $day
                  AND end_date >= $day
                  AND (
                       scope_type='ALL'
                       OR (scope_type='CATEGORY' AND target_id=$category)
                       OR (scope_type='PRODUCT' AND target_id=$product)
                  )
                ORDER BY
                    discount_percent DESC,
                    CASE scope_type
                        WHEN 'PRODUCT' THEN 3
                        WHEN 'CATEGORY' THEN 2
                        ELSE 1
                    END DESC,
                    id DESC
                LIMIT 1;
                """;

            q.Parameters.AddWithValue(
                "$day",
                localDate.ToString("yyyy-MM-dd"));
            q.Parameters.AddWithValue("$category", categoryId);
            q.Parameters.AddWithValue("$product", productId);

            await using var r = await q.ExecuteReaderAsync(ct);

            if (!await r.ReadAsync(ct))
                return null;

            return new PromotionSnapshot(
                PromotionId: r.GetInt64(0),
                Name: r.GetString(1),
                DiscountPercent: r.GetInt32(2),
                StartDate: r.GetString(3),
                EndDate: r.GetString(4),
                Scope: ParseScope(r.GetString(5)),
                TargetId: r.GetInt64(6));
        });
    }

    public async Task<long> CreateAsync(
        PromotionCreateRequest request,
        string actor,
        CancellationToken ct = default)
    {
        var name = (request.Name ?? "").Trim();
        actor = (actor ?? "").Trim();

        if (name.Length < 2)
            throw new InvalidOperationException(
                "Angebotsname ist erforderlich.");

        if (!AllowedPercents.Contains(request.DiscountPercent))
            throw new InvalidOperationException(
                "Erlaubte Angebotsrabatte: 10, 15, 20, 25, 30, 40 oder 50 Prozent.");

        if (request.EndDate < request.StartDate)
            throw new InvalidOperationException(
                "Das Enddatum liegt vor dem Startdatum.");

        if (actor.Length == 0)
            throw new InvalidOperationException(
                "Bediener fehlt.");

        if (request.Scope != PromotionScope.All &&
            request.TargetId <= 0)
        {
            throw new InvalidOperationException(
                "Für die gewählte Angebotsart fehlt das Ziel.");
        }

        var targetId =
            request.Scope == PromotionScope.All
                ? 0
                : request.TargetId;

        var targetName =
            request.Scope == PromotionScope.All
                ? "Alle Artikel"
                : (request.TargetName ?? "").Trim();

        return await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx =
                await c.BeginTransactionAsync(ct);

            var now = DateTimeOffset.Now.ToString("O");
            long id;

            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    INSERT INTO promotion_campaigns(
                        name,discount_percent,start_date,end_date,
                        scope_type,target_id,target_name,
                        is_enabled,created_by,created_at,updated_at)
                    VALUES(
                        $name,$percent,$start,$end,
                        $scope,$target,$targetName,
                        1,$actor,$created,$updated);
                    SELECT last_insert_rowid();
                    """;

                q.Parameters.AddWithValue("$name", name);
                q.Parameters.AddWithValue(
                    "$percent",
                    request.DiscountPercent);
                q.Parameters.AddWithValue(
                    "$start",
                    request.StartDate.ToString("yyyy-MM-dd"));
                q.Parameters.AddWithValue(
                    "$end",
                    request.EndDate.ToString("yyyy-MM-dd"));
                q.Parameters.AddWithValue(
                    "$scope",
                    ToDbScope(request.Scope));
                q.Parameters.AddWithValue(
                    "$target",
                    targetId);
                q.Parameters.AddWithValue(
                    "$targetName",
                    targetName);
                q.Parameters.AddWithValue(
                    "$actor",
                    actor);
                q.Parameters.AddWithValue(
                    "$created",
                    now);
                q.Parameters.AddWithValue(
                    "$updated",
                    now);

                id = Convert.ToInt64(
                    await q.ExecuteScalarAsync(ct));
            }

            await using (var audit = c.CreateCommand())
            {
                audit.Transaction = (SqliteTransaction)tx;
                audit.CommandText = """
                    INSERT INTO audit_log(
                        created_at,actor,event_type,
                        entity_type,entity_id,details)
                    VALUES(
                        $created,$actor,'PROMOTION_CREATED',
                        'PROMOTION',$id,$details);
                    """;

                audit.Parameters.AddWithValue(
                    "$created",
                    now);
                audit.Parameters.AddWithValue(
                    "$actor",
                    actor);
                audit.Parameters.AddWithValue(
                    "$id",
                    id.ToString());
                audit.Parameters.AddWithValue(
                    "$details",
                    JsonSerializer.Serialize(new
                    {
                        name,
                        discount_percent =
                            request.DiscountPercent,
                        start_date =
                            request.StartDate.ToString(
                                "yyyy-MM-dd"),
                        end_date =
                            request.EndDate.ToString(
                                "yyyy-MM-dd"),
                        scope =
                            ToDbScope(request.Scope),
                        target_id =
                            targetId,
                        target_name =
                            targetName
                    }));

                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return id;
        });
    }

    public async Task DisableAsync(
        long campaignId,
        string actor,
        string reason,
        CancellationToken ct = default)
    {
        if (campaignId <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(campaignId));

        actor = (actor ?? "").Trim();
        reason = (reason ?? "").Trim();

        if (actor.Length == 0)
            throw new InvalidOperationException(
                "Bediener fehlt.");

        if (reason.Length == 0)
            throw new InvalidOperationException(
                "Grund für Deaktivierung fehlt.");

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var tx =
                await c.BeginTransactionAsync(ct);

            string name;
            int percent;
            string start;
            string end;

            await using (var read = c.CreateCommand())
            {
                read.Transaction = (SqliteTransaction)tx;
                read.CommandText = """
                    SELECT
                        name,discount_percent,start_date,end_date
                    FROM promotion_campaigns
                    WHERE id=$id AND is_enabled=1;
                    """;
                read.Parameters.AddWithValue(
                    "$id",
                    campaignId);

                await using var r =
                    await read.ExecuteReaderAsync(ct);

                if (!await r.ReadAsync(ct))
                    throw new InvalidOperationException(
                        "Angebot wurde nicht gefunden oder ist bereits deaktiviert.");

                name = r.GetString(0);
                percent = r.GetInt32(1);
                start = r.GetString(2);
                end = r.GetString(3);
            }

            var now = DateTimeOffset.Now.ToString("O");

            await using (var q = c.CreateCommand())
            {
                q.Transaction = (SqliteTransaction)tx;
                q.CommandText = """
                    UPDATE promotion_campaigns
                    SET is_enabled=0,
                        updated_at=$updated
                    WHERE id=$id
                      AND is_enabled=1;
                    """;

                q.Parameters.AddWithValue(
                    "$updated",
                    now);
                q.Parameters.AddWithValue(
                    "$id",
                    campaignId);

                if (await q.ExecuteNonQueryAsync(ct) != 1)
                    throw new InvalidOperationException(
                        "Angebot konnte nicht deaktiviert werden.");
            }

            await using (var audit = c.CreateCommand())
            {
                audit.Transaction = (SqliteTransaction)tx;
                audit.CommandText = """
                    INSERT INTO audit_log(
                        created_at,actor,event_type,
                        entity_type,entity_id,details)
                    VALUES(
                        $created,$actor,'PROMOTION_DISABLED',
                        'PROMOTION',$id,$details);
                    """;

                audit.Parameters.AddWithValue(
                    "$created",
                    now);
                audit.Parameters.AddWithValue(
                    "$actor",
                    actor);
                audit.Parameters.AddWithValue(
                    "$id",
                    campaignId.ToString());
                audit.Parameters.AddWithValue(
                    "$details",
                    JsonSerializer.Serialize(new
                    {
                        name,
                        discount_percent = percent,
                        start_date = start,
                        end_date = end,
                        reason
                    }));

                await audit.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }

    private static PromotionCampaign ReadCampaign(
        SqliteDataReader r) =>
        new(
            Id: r.GetInt64(0),
            Name: r.GetString(1),
            DiscountPercent: r.GetInt32(2),
            StartDate: DateOnly.ParseExact(
                r.GetString(3),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None),
            EndDate: DateOnly.ParseExact(
                r.GetString(4),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None),
            Scope: ParseScope(r.GetString(5)),
            TargetId: r.GetInt64(6),
            TargetName: r.GetString(7),
            IsEnabled: r.GetInt64(8) == 1,
            CreatedBy: r.GetString(9),
            CreatedAt: DateTimeOffset.Parse(
                r.GetString(10)),
            UpdatedAt: DateTimeOffset.Parse(
                r.GetString(11)));

    private static PromotionScope ParseScope(
        string value) =>
        value.Trim().ToUpperInvariant() switch
        {
            "CATEGORY" => PromotionScope.Category,
            "PRODUCT" => PromotionScope.Product,
            _ => PromotionScope.All
        };

    private static string ToDbScope(
        PromotionScope scope) =>
        scope switch
        {
            PromotionScope.Category => "CATEGORY",
            PromotionScope.Product => "PRODUCT",
            _ => "ALL"
        };
}
