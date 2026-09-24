using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Local authority for Self Order table identity and active table-session
/// capabilities. The static QR token only identifies a table. It is never
/// sufficient to authorize an order without a currently open session
/// capability.
/// </summary>
public sealed class RestaurantSelfOrderService
{
    private readonly SqliteDatabase _db;
    private readonly RestaurantRepository _restaurant;
    private readonly RestaurantEntitlementService _entitlements;

    public RestaurantSelfOrderService(
        SqliteDatabase db,
        RestaurantRepository restaurant,
        RestaurantEntitlementService entitlements)
    {
        _db = db;
        _restaurant = restaurant;
        _entitlements = entitlements;
    }

    public async Task<RestaurantSelfOrderTableQrIssue> RotateTableQrAsync(
        long tableId,
        string actor,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.QrTischbestellung);

        actor = (actor ?? "").Trim();
        if (actor.Length == 0)
            throw new ArgumentException(
                "Bediener fehlt.",
                nameof(actor));

        var table = await _restaurant.GetTableAsync(
            tableId,
            ct);

        if (table is null || !table.IsActive)
        {
            throw new InvalidOperationException(
                "Tisch ist nicht vorhanden oder deaktiviert.");
        }

        var secret =
            RestaurantSelfOrderSecurity.CreateTableQrSecret();
        var rotatedAt = DateTimeOffset.UtcNow;

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_self_order_tables(
                    table_id,token_hash,enabled,rotated_at,rotated_by)
                VALUES($table,$hash,1,$at,$actor)
                ON CONFLICT(table_id) DO UPDATE SET
                    token_hash=excluded.token_hash,
                    enabled=1,
                    rotated_at=excluded.rotated_at,
                    rotated_by=excluded.rotated_by;
                """;
            q.Parameters.AddWithValue("$table", tableId);
            q.Parameters.AddWithValue(
                "$hash",
                secret.TokenHash);
            q.Parameters.AddWithValue(
                "$at",
                rotatedAt.ToString("O"));
            q.Parameters.AddWithValue("$actor", actor);
            await q.ExecuteNonQueryAsync(ct);
        });

        return new RestaurantSelfOrderTableQrIssue(
            tableId,
            secret.PublicToken,
            rotatedAt);
    }

    public async Task<RestaurantSelfOrderSessionCapabilityIssue> ActivateSessionAsync(
        string sessionId,
        RestaurantSelfOrderApprovalMode approvalMode,
        string actor,
        TimeSpan? lifetime = null,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.QrTischbestellung);

        sessionId = (sessionId ?? "").Trim();
        actor = (actor ?? "").Trim();

        if (sessionId.Length == 0)
            throw new ArgumentException(
                "Tischvorgang fehlt.",
                nameof(sessionId));
        if (actor.Length == 0)
            throw new ArgumentException(
                "Bediener fehlt.",
                nameof(actor));
        if (!Enum.IsDefined(approvalMode))
            throw new ArgumentOutOfRangeException(
                nameof(approvalMode));

        var ttl = lifetime ?? TimeSpan.FromHours(8);
        if (ttl < TimeSpan.FromMinutes(5) ||
            ttl > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                "Self-Order-Sitzung muss zwischen 5 Minuten und 24 Stunden gültig sein.");
        }

        var session =
            await _restaurant.GetSessionAsync(
                sessionId,
                ct)
            ?? throw new InvalidOperationException(
                "Tischvorgang wurde nicht gefunden.");

        if (session.State !=
            RestaurantTableSessionState.Open)
        {
            throw new InvalidOperationException(
                "Self Order kann nur für einen offenen Tischvorgang aktiviert werden.");
        }

        var enabled = false;
        await using (var c = _db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT enabled
                FROM restaurant_self_order_tables
                WHERE table_id=$table
                LIMIT 1;
                """;
            q.Parameters.AddWithValue(
                "$table",
                session.TableId);

            var value = await q.ExecuteScalarAsync(ct);
            enabled =
                value is not null &&
                Convert.ToInt32(value) == 1;
        }

        if (!enabled)
        {
            throw new InvalidOperationException(
                "Für diesen Tisch wurde noch kein aktiver Self-Order-QR-Code erzeugt.");
        }

        var capability =
            RestaurantSelfOrderSecurity.CreateTableQrSecret();
        var publicSessionId =
            RestaurantSelfOrderSecurity.CreatePublicSessionId();
        var activatedAt = DateTimeOffset.UtcNow;
        var expiresAt = activatedAt.Add(ttl);
        var mode = approvalMode ==
            RestaurantSelfOrderApprovalMode.Automatic
                ? "AUTOMATIC"
                : "CONFIRMATION_REQUIRED";

        await IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO restaurant_self_order_sessions(
                    session_id,table_id,public_session_id,capability_hash,
                    approval_mode,activated_at,expires_at,closed_at,activated_by)
                VALUES(
                    $session,$table,$public,$hash,
                    $mode,$activated,$expires,NULL,$actor)
                ON CONFLICT(session_id) DO UPDATE SET
                    table_id=excluded.table_id,
                    public_session_id=excluded.public_session_id,
                    capability_hash=excluded.capability_hash,
                    approval_mode=excluded.approval_mode,
                    activated_at=excluded.activated_at,
                    expires_at=excluded.expires_at,
                    closed_at=NULL,
                    activated_by=excluded.activated_by;
                """;
            q.Parameters.AddWithValue(
                "$session",
                session.Id);
            q.Parameters.AddWithValue(
                "$table",
                session.TableId);
            q.Parameters.AddWithValue(
                "$public",
                publicSessionId);
            q.Parameters.AddWithValue(
                "$hash",
                capability.TokenHash);
            q.Parameters.AddWithValue(
                "$mode",
                mode);
            q.Parameters.AddWithValue(
                "$activated",
                activatedAt.ToString("O"));
            q.Parameters.AddWithValue(
                "$expires",
                expiresAt.ToString("O"));
            q.Parameters.AddWithValue(
                "$actor",
                actor);
            await q.ExecuteNonQueryAsync(ct);
        });

        return new RestaurantSelfOrderSessionCapabilityIssue(
            session.Id,
            session.TableId,
            publicSessionId,
            capability.PublicToken,
            approvalMode,
            expiresAt);
    }

    public async Task<RestaurantSelfOrderCapabilityValidation> ValidateOrderCapabilityAsync(
        string tablePublicToken,
        string publicSessionId,
        string capabilitySecret,
        CancellationToken ct = default)
    {
        if (!_entitlements.IsEnabled(
                RestaurantFeature.QrTischbestellung))
        {
            return RestaurantSelfOrderCapabilityValidation.Invalid;
        }

        tablePublicToken = (tablePublicToken ?? "").Trim();
        publicSessionId = (publicSessionId ?? "").Trim();
        capabilitySecret = (capabilitySecret ?? "").Trim();

        if (tablePublicToken.Length == 0 ||
            publicSessionId.Length == 0 ||
            capabilitySecret.Length == 0)
        {
            return RestaurantSelfOrderCapabilityValidation.Invalid;
        }

        string sessionId;
        long tableId;
        string tableHash;
        string capabilityHash;
        string mode;

        await using (var c = _db.OpenReadConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT
                    so.session_id,
                    so.table_id,
                    t.token_hash,
                    so.capability_hash,
                    so.approval_mode
                FROM restaurant_self_order_sessions so
                JOIN restaurant_self_order_tables t
                  ON t.table_id=so.table_id
                 AND t.enabled=1
                JOIN restaurant_sessions s
                  ON s.id=so.session_id
                 AND s.table_id=so.table_id
                 AND s.state='OPEN'
                WHERE so.public_session_id=$public
                  AND so.closed_at IS NULL
                  AND so.expires_at >= $now
                LIMIT 1;
                """;
            q.Parameters.AddWithValue(
                "$public",
                publicSessionId);
            q.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O"));

            await using var r =
                await q.ExecuteReaderAsync(ct);

            if (!await r.ReadAsync(ct))
            {
                return RestaurantSelfOrderCapabilityValidation.Invalid;
            }

            sessionId = r.GetString(0);
            tableId = r.GetInt64(1);
            tableHash = r.GetString(2);
            capabilityHash = r.GetString(3);
            mode = r.GetString(4);
        }

        if (!RestaurantSelfOrderSecurity.VerifyToken(
                tablePublicToken,
                tableHash) ||
            !RestaurantSelfOrderSecurity.VerifyToken(
                capabilitySecret,
                capabilityHash))
        {
            return RestaurantSelfOrderCapabilityValidation.Invalid;
        }

        return new RestaurantSelfOrderCapabilityValidation(
            true,
            sessionId,
            tableId,
            string.Equals(
                mode,
                "AUTOMATIC",
                StringComparison.Ordinal)
                ? RestaurantSelfOrderApprovalMode.Automatic
                : RestaurantSelfOrderApprovalMode.ConfirmationRequired);
    }

    public Task CloseSessionAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        _entitlements.Require(
            RestaurantFeature.QrTischbestellung);

        sessionId = (sessionId ?? "").Trim();
        if (sessionId.Length == 0)
            throw new ArgumentException(
                "Tischvorgang fehlt.",
                nameof(sessionId));

        return IoQueue.RunAsync(async () =>
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE restaurant_self_order_sessions
                SET closed_at=$now
                WHERE session_id=$session
                  AND closed_at IS NULL;
                """;
            q.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O"));
            q.Parameters.AddWithValue(
                "$session",
                sessionId);
            await q.ExecuteNonQueryAsync(ct);
        });
    }
}
