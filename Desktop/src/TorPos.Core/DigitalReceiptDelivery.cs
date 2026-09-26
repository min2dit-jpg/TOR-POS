namespace TorPos.Core;

/// <summary>How the customer receives the receipt of a completed sale.</summary>
public enum ReceiptDeliveryChannel
{
    Paper,
    QrCode,
    Pdf,
    Email,
    DownloadLink
}

/// <summary>
/// Where the sale stands fiscally when a receipt is asked for. A receipt is
/// only the presentation of a Vorgang that is already final: signed by the
/// TSE, or completed under a documented TSE outage.
/// </summary>
public enum ReceiptFiscalState
{
    NotCompleted,
    Signed,
    DocumentedOutage
}

public sealed record ReceiptDeliveryPlan(
    FiscalComplianceProfile Profile,
    ReceiptDeliveryChannel DefaultChannel,
    IReadOnlyList<ReceiptDeliveryChannel> Offered);

public sealed record ReceiptDeliveryResult(
    ReceiptDeliveryChannel Channel,
    bool Delivered,
    string Message,
    DigitalReceiptPublication? Publication = null);

/// <summary>
/// One way to hand out a digital receipt (TOR Cloud QR link, PDF, e-mail, ...).
/// A provider presents a finished document; it never changes the sale, its TSE
/// data or any fiscal record, and queues nothing for later.
/// </summary>
public interface IDigitalReceiptProvider
{
    string ProviderId { get; }
    ReceiptDeliveryChannel Channel { get; }
    Task<ProviderReadiness> GetReadinessAsync(CancellationToken ct = default);
    Task<ReceiptDeliveryResult> DeliverAsync(DigitalReceiptDocument document, string reference, CancellationToken ct = default);
}

/// <summary>
/// Decides which receipt channels are offered and when paper is printed. The
/// obligations come from the <see cref="FiscalComplianceProfile"/> in force,
/// so the 2028 digital-first behaviour needs no change here once enacted.
/// </summary>
public static class ReceiptDeliveryPolicy
{
    public const string NotCompletedMessage =
        "TSE nicht verfügbar. Vorgang kann derzeit nicht fiskal abgeschlossen werden - es wird kein Beleg ausgegeben.";

    public static ReceiptDeliveryPlan Plan(
        FiscalComplianceProfile profile,
        ReceiptFiscalState fiscalState,
        IEnumerable<ReceiptDeliveryChannel> availableDigitalChannels)
    {
        if (fiscalState == ReceiptFiscalState.NotCompleted)
            throw new InvalidOperationException(NotCompletedMessage);

        var digital = availableDigitalChannels
            .Where(x => x != ReceiptDeliveryChannel.Paper)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var offered = new List<ReceiptDeliveryChannel>(digital);
        // Paper stays possible under every rule set: required today, "on
        // request" under the planned law, and always the fallback.
        offered.Add(ReceiptDeliveryChannel.Paper);

        var preferred = !profile.PaperReceiptRequired && profile.DigitalReceiptRequired && digital.Count > 0
            ? (digital.Contains(ReceiptDeliveryChannel.QrCode) ? ReceiptDeliveryChannel.QrCode : digital[0])
            : ReceiptDeliveryChannel.Paper;

        return new ReceiptDeliveryPlan(profile, preferred, offered);
    }

    /// <summary>
    /// Paper is printed when the customer chose it, when the chosen digital
    /// channel did not confirm delivery, or when the rule set requires paper
    /// and the customer has not accepted a digital receipt. The customer is
    /// never left without a receipt.
    /// </summary>
    public static bool MustPrintPaper(ReceiptDeliveryChannel chosen, ReceiptDeliveryResult? digitalResult)
    {
        if (chosen == ReceiptDeliveryChannel.Paper)
            return true;
        return digitalResult is null || !digitalResult.Delivered || digitalResult.Channel != chosen;
    }
}

/// <summary>
/// What may be encoded in a receipt QR code or download link: exactly one
/// acceptable receipt link (<see cref="DigitalReceiptLink.IsAcceptable"/>),
/// whose only variable part is a 256-bit opaque token. No customer data, no
/// TSE secret, no authentication token, no fiscal payload.
/// </summary>
public static class QrReceiptPayload
{
    public const int MaxLength = 512;

    public static bool IsSafe(string? payload)
    {
        if (string.IsNullOrEmpty(payload) || payload.Length > MaxLength)
            return false;
        if (payload.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return false;
        return DigitalReceiptLink.IsAcceptable(payload);
    }

    public static string Require(string? payload) =>
        IsSafe(payload)
            ? payload!
            : throw new InvalidOperationException("Der Bon-Link ist für einen QR-Code nicht zulässig (nur HTTPS-Link mit Bon-Token, ohne weitere Daten).");
}
