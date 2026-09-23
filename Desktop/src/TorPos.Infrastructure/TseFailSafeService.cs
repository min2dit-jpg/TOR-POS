using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Central fail-safe wrapper for productive TSE operations.
///
/// Legal behavior:
/// - TSE failure is documented automatically.
/// - Failure of the TSE alone must not be treated like a successful signature.
/// - Higher layers may continue a sale in a controlled "TSE-Ausfall" mode,
///   but the receipt must be marked accordingly.
/// - On the first later successful TSE operation the open outage is closed.
///
/// This service does NOT silently fabricate TSE values.
/// </summary>
public sealed class TseFailSafeService
{
    private readonly ITseProvider _provider;
    private readonly ITseOutageRepository _outages;
    private readonly IAuditLog _audit;

    public TseFailSafeService(
        ITseProvider provider,
        ITseOutageRepository outages,
        IAuditLog audit)
    {
        _provider = provider;
        _outages = outages;
        _audit = audit;
    }

    /// <summary>
    /// R113: a sale/order that reaches signing while the TSE is not active or
    /// not configured is ITSELF an outage and must be documented like any
    /// other TSE failure. Before this, both signing services simply returned
    /// at that point: no outage row, no audit entry, and - because the sale's
    /// TseOutage flag stayed false - a printed receipt WITHOUT the legally
    /// required "TSE-AUSFALL" note. Silence was the bug, not the missing TSE.
    /// </summary>
    public async Task ReportUnavailableAsync(
        string reason,
        string actor = "SYSTEM",
        CancellationToken ct = default)
    {
        await _outages.OpenAsync(reason, actor, ct);

        await _audit.WriteAsync(
            actor,
            "TSE_UNAVAILABLE",
            "TSE",
            "",
            reason,
            ct);
    }

    /// <summary>
    /// R113: lets the cashier-facing layer show an open TSE outage. The
    /// repository method existed from the start but had no production caller,
    /// so an open outage was never visible anywhere in the running till.
    /// </summary>
    public Task<TseOutage?> GetOpenOutageAsync(CancellationToken ct = default) =>
        _outages.GetOpenAsync(ct);

    public async Task<TseProbeResult> ProbeAsync(
        string actor = "SYSTEM",
        CancellationToken ct = default)
    {
        try
        {
            var result =
                await _provider.ProbeAsync(ct);

            var certificate =
                TseCertificatePolicy.Evaluate(
                    result.Device?.CertificateValidUntil,
                    DateOnly.FromDateTime(DateTime.Now));

            if (certificate.State == TseCertificateState.Expired)
            {
                result = result with
                {
                    State = TseConnectionState.Error,
                    Message = certificate.Message
                };

                await _audit.WriteAsync(
                    actor,
                    "TSE_CERTIFICATE_EXPIRED",
                    "TSE",
                    result.Device?.SerialNumber ?? "",
                    certificate.Message,
                    ct);
            }

            if (result.State == TseConnectionState.Ready)
            {
                await _outages.CloseOpenAsync(actor, ct);
            }
            else if (result.State is
                     TseConnectionState.SdkMissing or
                     TseConnectionState.NotFound or
                     TseConnectionState.Error)
            {
                await _outages.OpenAsync(
                    result.Message,
                    actor,
                    ct);
            }

            return result;
        }
        catch (Exception ex)
        {
            await _outages.OpenAsync(
                $"{ex.GetType().Name}: {ex.Message}",
                actor,
                ct);

            await _audit.WriteAsync(
                actor,
                "TSE_PROBE_EXCEPTION",
                "TSE",
                "",
                ex.GetType().Name,
                ct);

            return new TseProbeResult(
                TseConnectionState.Error,
                $"TSE-Fehler: {ex.Message}");
        }
    }

    public async Task<(TseTransactionResult Result, bool TseOutage)>
        StartTransactionAsync(
            TseTransactionStartRequest request,
            string actor,
            CancellationToken ct = default)
    {
        try
        {
            var result =
                await _provider.StartTransactionAsync(
                    request,
                    ct);

            if (result.Success)
            {
                await _outages.CloseOpenAsync(actor, ct);
                return (result, false);
            }

            await _outages.OpenAsync(
                result.Message,
                actor,
                ct);

            return (result, true);
        }
        catch (Exception ex)
        {
            await _outages.OpenAsync(
                $"{ex.GetType().Name}: {ex.Message}",
                actor,
                ct);

            return (
                new TseTransactionResult(
                    false,
                    $"TSE-Ausfall: {ex.Message}"),
                true);
        }
    }

    public async Task<(TseTransactionResult Result, bool TseOutage)>
        FinishTransactionAsync(
            TseTransactionFinishRequest request,
            string actor,
            CancellationToken ct = default)
    {
        try
        {
            var result =
                await _provider.FinishTransactionAsync(
                    request,
                    ct);

            if (result.Success)
            {
                await _outages.CloseOpenAsync(actor, ct);
                return (result, false);
            }

            await _outages.OpenAsync(
                result.Message,
                actor,
                ct);

            return (result, true);
        }
        catch (Exception ex)
        {
            await _outages.OpenAsync(
                $"{ex.GetType().Name}: {ex.Message}",
                actor,
                ct);

            return (
                new TseTransactionResult(
                    false,
                    $"TSE-Ausfall: {ex.Message}"),
                true);
        }
    }
}
