namespace TorPos.Infrastructure;

public sealed class FiskaltrustSignUnresolvedException : InvalidOperationException
{
    public FiskaltrustSignUnresolvedException(
        string receiptReference,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        ReceiptReference = receiptReference;
    }

    public string ReceiptReference { get; }
}

/// <summary>
/// Crash-safe Sign orchestration for the sandbox integration.
///
/// It never blindly repeats a request that may already have reached fiskaltrust.
/// SENT/UNKNOWN always go through ReceiptRequest recovery first. A null recovery
/// result remains unresolved and requires an explicit later decision.
/// </summary>
public sealed class FiskaltrustSignCoordinator
{
    private readonly IFiskaltrustMiddlewareClient _client;
    private readonly FiskaltrustSignJournal _journal;

    public FiskaltrustSignCoordinator(
        IFiskaltrustMiddlewareClient client,
        FiskaltrustSignJournal journal)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    public Task<FiskaltrustReceiptResponse> ExecuteAsync(
        FiskaltrustReceiptRequest request,
        CancellationToken ct = default) =>
        ExecuteAsync(request, "FINAL", ct);

    public async Task<FiskaltrustReceiptResponse> ExecuteAsync(
        FiskaltrustReceiptRequest request,
        string operationKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entry = await _journal.BeginAsync(request, operationKey, ct);

        if (entry.State == FiskaltrustSignState.Committed)
        {
            return entry.Response
                ?? throw new InvalidOperationException(
                    "COMMITTED fiskaltrust Journal-Eintrag ohne ReceiptResponse.");
        }

        if (entry.State is FiskaltrustSignState.Sent or FiskaltrustSignState.Unknown)
            return await RecoverExistingAsync(entry, ct);

        // PREPARED: no external effect is allowed before SENT is durable.
        await _journal.MarkSentAsync(entry.Id, ct);

        try
        {
            var response = await _client.SignAsync(entry.Request, ct);
            await _journal.MarkCommittedAsync(
                entry.Id,
                response,
                "Sign response received",
                ct);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Even a transport/deserialization error can happen after the
            // Middleware processed the receipt. Never infer "not signed".
            try
            {
                await _journal.MarkUnknownAsync(
                    entry.Id,
                    $"{ex.GetType().Name}: {ex.Message}",
                    CancellationToken.None);
            }
            catch
            {
                // SENT is already durable, which is itself a safe recovery
                // candidate. Do not mask the original ambiguous Sign error.
            }

            throw new FiskaltrustSignUnresolvedException(
                entry.ReceiptReference,
                "fiskaltrust Sign-Ergebnis ist unklar. Vor einem erneuten Senden muss ReceiptRequest-Recovery ausgeführt werden.",
                ex);
        }
    }

    public async Task<IReadOnlyList<string>> RecoverPendingAsync(
        CancellationToken ct = default)
    {
        var unresolved = new List<string>();

        foreach (var entry in await _journal.GetRecoveryCandidatesAsync(ct))
        {
            try
            {
                _ = await RecoverExistingAsync(entry, ct);
            }
            catch (FiskaltrustSignUnresolvedException)
            {
                unresolved.Add(entry.ReceiptReference);
            }
        }

        return unresolved;
    }

    private async Task<FiskaltrustReceiptResponse> RecoverExistingAsync(
        FiskaltrustSignJournalEntry entry,
        CancellationToken ct)
    {
        FiskaltrustReceiptResponse? recovered;
        try
        {
            recovered = await _client.RecoverAsync(entry.Request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new FiskaltrustSignUnresolvedException(
                entry.ReceiptReference,
                "fiskaltrust ReceiptRequest-Recovery fehlgeschlagen. Der Originalbeleg wird nicht erneut gesendet.",
                ex);
        }

        if (recovered is null)
        {
            throw new FiskaltrustSignUnresolvedException(
                entry.ReceiptReference,
                "fiskaltrust hat für diese cbReceiptReference noch keinen gespeicherten Beleg geliefert. Automatisches Neusenden bleibt gesperrt.");
        }

        await _journal.MarkCommittedAsync(
            entry.Id,
            recovered,
            "Recovered with ReceiptRequest",
            ct);

        return recovered;
    }
}
