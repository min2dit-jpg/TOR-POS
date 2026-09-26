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
    /// <summary>
    /// O-3: how long a successful (Ready) probe may stand in for the re-probe
    /// before a transaction. Only Ready results are kept; every failure clears
    /// the cache, and certificate expiry and TSE generation are still checked
    /// against the cached device at the current time. A TSE removed within
    /// this window fails at StartTransaction and is documented as an outage.
    /// </summary>
    public static readonly TimeSpan ReadyProbeLifetime = TimeSpan.FromSeconds(30);

    private readonly ITseProvider _provider;
    private readonly ITseOutageRepository _outages;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly object _probeLock = new();
    private TseProbeResult? _readyProbe;
    private long _readyProbeAt;

    public TseFailSafeService(
        ITseProvider provider,
        ITseOutageRepository outages,
        IAuditLog audit,
        TimeProvider? time = null)
    {
        _provider = provider;
        _outages = outages;
        _audit = audit;
        _time = time ?? TimeProvider.System;
    }

    private void RememberProbe(TseProbeResult result)
    {
        lock (_probeLock)
        {
            if (result.State == TseConnectionState.Ready)
            {
                _readyProbe = result;
                _readyProbeAt = _time.GetTimestamp();
            }
            else
            {
                _readyProbe = null;
            }
        }
    }

    /// <summary>Drops the cached probe; the next transaction probes the TSE again.</summary>
    public void ForgetProbe()
    {
        lock (_probeLock)
            _readyProbe = null;
    }

    private async Task<TseProbeResult> ProbeForTransactionAsync(CancellationToken ct)
    {
        lock (_probeLock)
        {
            if (_readyProbe is not null &&
                _time.GetElapsedTime(_readyProbeAt) < ReadyProbeLifetime)
            {
                return _readyProbe;
            }
        }

        var probe = await _provider.ProbeAsync(ct);
        RememberProbe(probe);
        return probe;
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
        ForgetProbe();
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
                    result.Device?.CertificateExpiresAtUtc,
                    DateTimeOffset.UtcNow);

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

            RememberProbe(result);

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
            ForgetProbe();
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
            // Certificate safety is independent of acceptance paperwork.
            // Re-probe the actually connected TSE before every transaction;
            // O-3: a Ready probe younger than ReadyProbeLifetime is reused.
            var releaseProbe = await ProbeForTransactionAsync(ct);
            var releaseCertificate =
                TseCertificatePolicy.Evaluate(
                    releaseProbe.Device?.CertificateExpiresAtUtc,
                    DateTimeOffset.UtcNow);

            if (releaseCertificate.State == TseCertificateState.Expired)
            {
                ForgetProbe();
                var reason = releaseCertificate.Message;

                await _outages.OpenAsync(reason, actor, ct);
                await _audit.WriteAsync(
                    actor,
                    "TSE_CERTIFICATE_EXPIRED",
                    "TSE",
                    releaseProbe.Device?.SerialNumber ?? "",
                    reason,
                    ct);

                return (
                    new TseTransactionResult(false, reason),
                    true);
            }

            // Runtime device safety must not depend on release-paperwork flags.
            // A failed re-probe or an unknown physical generation is rejected
            // before calling the signing provider, even while qualification
            // evidence is still incomplete.
            if (releaseProbe.State != TseConnectionState.Ready)
            {
                var reason =
                    "TSE vor Transaktionsstart nicht betriebsbereit: " +
                    releaseProbe.Message;

                await _outages.OpenAsync(reason, actor, ct);
                await _audit.WriteAsync(
                    actor,
                    "TSE_REPROBE_NOT_READY",
                    "TSE",
                    releaseProbe.Device?.SerialNumber ?? "",
                    reason,
                    ct);

                return (
                    new TseTransactionResult(false, reason),
                    true);
            }

            TseProviderDescriptor? releaseProvider = null;
            try
            {
                releaseProvider = TseProviderCatalog.Get(_provider.ProviderId);
            }
            catch
            {
                // Provider identity is handled by the existing release gate
                // and provider implementation. Do not guess a transport here.
            }

            var requiresPhysicalGeneration =
                releaseProvider is not null &&
                (releaseProvider.Transport == TseProviderTransport.HardwareSdk ||
                 releaseProvider.Transport == TseProviderTransport.LocalMiddleware);

            if (requiresPhysicalGeneration &&
                FiscalRelease.DetectPhysicalTseGeneration(releaseProbe.Device) ==
                    PhysicalTseGeneration.Unknown)
            {
                const string reason =
                    "TSE-Generation nicht eindeutig erkannt; Signierung wird fail-closed verweigert.";

                await _outages.OpenAsync(reason, actor, ct);
                await _audit.WriteAsync(
                    actor,
                    "TSE_GENERATION_UNKNOWN",
                    "TSE",
                    releaseProbe.Device?.SerialNumber ?? "",
                    reason,
                    ct);

                return (
                    new TseTransactionResult(false, reason),
                    true);
            }

            if (FiscalRelease.CommonQualificationsValidated)
            {
                var releaseAllowed =
                    FiscalRelease.EnabledForProvider(
                        _provider.ProviderId,
                        releaseProbe.Device);

                if (!releaseAllowed)
                {
                    var missing = FiscalRelease.MissingQualificationsForProvider(
                            _provider.ProviderId,
                            releaseProbe.Device)
                        .ToList();

                    var reason =
                        "TSE-Produktionsfreigabe gesperrt: " +
                        string.Join(", ", missing);

                    await _outages.OpenAsync(reason, actor, ct);
                    await _audit.WriteAsync(
                        actor,
                        "TSE_RELEASE_GATE_BLOCKED",
                        "TSE",
                        releaseProbe.Device?.SerialNumber ?? "",
                        reason,
                        ct);

                    return (
                        new TseTransactionResult(false, reason),
                        true);
                }
            }

            var result =
                await _provider.StartTransactionAsync(
                    request,
                    ct);

            if (result.Success)
            {
                await _outages.CloseOpenAsync(actor, ct);
                return (result, false);
            }

            ForgetProbe();
            await _outages.OpenAsync(
                result.Message,
                actor,
                ct);

            return (result, true);
        }
        catch (Exception ex)
        {
            ForgetProbe();
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

            ForgetProbe();
            await _outages.OpenAsync(
                result.Message,
                actor,
                ct);

            return (result, true);
        }
        catch (Exception ex)
        {
            ForgetProbe();
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
