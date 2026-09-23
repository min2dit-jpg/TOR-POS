using TorPos.Core;
using TorPos.Infrastructure;

internal sealed class CountingDirectCloudTseClient : IDirectCloudTseClient
{
    public bool IsConfigured => true;
    public int Calls { get; private set; }

    public Task<TseProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(
            new TseProbeResult(
                TseConnectionState.Ready,
                "SHOULD_NOT_BE_CALLED"));
    }

    public Task<TseTransactionResult> StartTransactionAsync(
        TseTransactionStartRequest request,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(
            new TseTransactionResult(
                true,
                "SHOULD_NOT_BE_CALLED",
                1,
                1,
                DateTimeOffset.UtcNow,
                "FAKE",
                "FAKE"));
    }

    public Task<TseTransactionResult> UpdateTransactionAsync(
        TseTransactionUpdateRequest request,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(
            new TseTransactionResult(
                true,
                "SHOULD_NOT_BE_CALLED",
                request.TransactionNumber,
                2,
                DateTimeOffset.UtcNow,
                "FAKE",
                "FAKE"));
    }

    public Task<TseTransactionResult> FinishTransactionAsync(
        TseTransactionFinishRequest request,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(
            new TseTransactionResult(
                true,
                "SHOULD_NOT_BE_CALLED",
                request.TransactionNumber,
                3,
                DateTimeOffset.UtcNow,
                "FAKE",
                "FAKE"));
    }

    public Task<TseExportResult> ExportAsync(
        string targetPath,
        CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(
            new TseExportResult(
                true,
                "SHOULD_NOT_BE_CALLED",
                targetPath));
    }
}
