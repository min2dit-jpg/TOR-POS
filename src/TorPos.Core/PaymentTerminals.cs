namespace TorPos.Core;

public sealed record PaymentTerminalProfile(
    string Id,
    string Manufacturer,
    string Family,
    string Integration,
    string TorStatus,
    string Notes);

public sealed record PaymentTerminalProbeResult(
    bool Success,
    string State,
    string Message,
    string Endpoint);

/// <summary>
/// Financial interpretation of the terminal result.
/// This is intentionally separate from the durable checkout safety state.
/// </summary>
public enum PaymentTerminalOutcome
{
    None,
    Approved,
    Declined,
    Cancelled,
    NotSent,
    Unknown
}

/// <summary>
/// How a durable checkout state was resolved. A terminal result and a later
/// human reconciliation are different facts and are therefore stored apart.
/// </summary>
public enum CheckoutResolution
{
    None,
    AutoApproved,
    AutoNotCharged,
    ManualPaid,
    ManualNotCharged
}

public static class PaymentOutcomeCodec
{
    public static string ToStorage(
        PaymentTerminalOutcome value) =>
        value switch
        {
            PaymentTerminalOutcome.Approved => "APPROVED",
            PaymentTerminalOutcome.Declined => "DECLINED",
            PaymentTerminalOutcome.Cancelled => "CANCELLED",
            PaymentTerminalOutcome.NotSent => "NOT_SENT",
            PaymentTerminalOutcome.Unknown => "UNKNOWN",
            _ => "NONE"
        };

    public static PaymentTerminalOutcome ParseOutcome(
        string? value) =>
        (value ?? "").Trim().ToUpperInvariant() switch
        {
            "APPROVED" => PaymentTerminalOutcome.Approved,
            "DECLINED" => PaymentTerminalOutcome.Declined,
            "CANCELLED" => PaymentTerminalOutcome.Cancelled,
            "NOT_SENT" => PaymentTerminalOutcome.NotSent,
            "UNKNOWN" => PaymentTerminalOutcome.Unknown,
            _ => PaymentTerminalOutcome.None
        };

    public static string ToStorage(
        CheckoutResolution value) =>
        value switch
        {
            CheckoutResolution.AutoApproved => "AUTO_APPROVED",
            CheckoutResolution.AutoNotCharged => "AUTO_NOT_CHARGED",
            CheckoutResolution.ManualPaid => "MANUAL_PAID",
            CheckoutResolution.ManualNotCharged => "MANUAL_NOT_CHARGED",
            _ => "NONE"
        };

    public static CheckoutResolution ParseResolution(
        string? value) =>
        (value ?? "").Trim().ToUpperInvariant() switch
        {
            "AUTO_APPROVED" => CheckoutResolution.AutoApproved,
            "AUTO_NOT_CHARGED" => CheckoutResolution.AutoNotCharged,
            "MANUAL_PAID" => CheckoutResolution.ManualPaid,
            "MANUAL_NOT_CHARGED" => CheckoutResolution.ManualNotCharged,
            _ => CheckoutResolution.None
        };
}

/// <summary>
/// Conservative classifier for a payment command that returned normally but
/// was not successful. Only explicit cancellation/decline wording can close
/// the checkout as NOT_CHARGED. Generic failures remain UNKNOWN.
/// </summary>
public static class PaymentTerminalOutcomeClassifier
{
    private static readonly string[] CancellationMessageMarkers =
    [
        "TRANSACTION CANCELLED",
        "TRANSACTION CANCELED",
        "CANCELLED BY USER",
        "CANCELED BY USER",
        "USER CANCELLED",
        "USER CANCELED",
        "USER ABORTED",
        "ABORTED BY USER",
        "VORGANG VOM BENUTZER ABGEBROCHEN",
        "VOM BENUTZER ABGEBROCHEN",
        "VORGANG ABGEBROCHEN",
        "ABBRUCH DURCH BENUTZER",
        "ABBRUCH DURCH KUNDE"
    ];

    private static readonly string[] DeclineMessageMarkers =
    [
        "PAYMENT DECLINED",
        "TRANSACTION DECLINED",
        "DECLINED BY HOST",
        "AUTHORIZATION DECLINED",
        "AUTHORIZATION DENIED",
        "PAYMENT DENIED",
        "PAYMENT REJECTED",
        "TRANSACTION REJECTED",
        "ZAHLUNG ABGELEHNT",
        "TRANSAKTION ABGELEHNT",
        "NICHT GENEHMIGT",
        "NOT APPROVED"
    ];

    public static PaymentTerminalOutcome ClassifyCompletedFailure(
        string? commandState,
        string? errorMessage,
        string? terminalMessage)
    {
        var state =
            (commandState ?? "")
            .Trim()
            .ToUpperInvariant();

        // Exact command states may be classified. Generic Error/Failure is
        // deliberately NOT interpreted as no charge.
        if (state is
            "CANCELLED" or
            "CANCELED" or
            "USER_CANCELLED" or
            "USER_CANCELED")
        {
            return PaymentTerminalOutcome.Cancelled;
        }

        if (state is
            "DECLINED" or
            "DENIED" or
            "REJECTED")
        {
            return PaymentTerminalOutcome.Declined;
        }

        var messages =
            string.Join(
                " | ",
                new[]
                {
                    errorMessage,
                    terminalMessage
                }
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            .ToUpperInvariant();

        if (CancellationMessageMarkers.Any(
                marker => messages.Contains(
                    marker,
                    StringComparison.Ordinal)))
        {
            return PaymentTerminalOutcome.Cancelled;
        }

        if (DeclineMessageMarkers.Any(
                marker => messages.Contains(
                    marker,
                    StringComparison.Ordinal)))
        {
            return PaymentTerminalOutcome.Declined;
        }

        return PaymentTerminalOutcome.Unknown;
    }
}

public sealed record PaymentTerminalPaymentResult(
    bool Success,
    string State,
    string Message,
    int? TerminalId = null,
    int? TerminalReceiptNumber = null,
    int? TraceNumber = null,
    string CardName = "",
    PaymentTerminalOutcome Outcome = PaymentTerminalOutcome.Unknown,
    bool RequestSubmitted = false,
    string OutcomeCode = "");

public interface IPaymentTerminalService
{
    IReadOnlyList<PaymentTerminalProfile> Profiles { get; }

    Task<PaymentTerminalProbeResult> ProbeAsync(
        CancellationToken ct = default);

    Task<PaymentTerminalProbeResult> RegisterAsync(
        CancellationToken ct = default);

    /// <summary>
    /// ZVT End-of-Day (06 50): asks the terminal to close and transfer its
    /// own stored daily turnover to the host/acquirer. This is the
    /// terminal's own batch settlement, entirely separate from TOR's own
    /// Z-Bericht/Kassenabschluss - it reconciles what the card network
    /// actually settles, not TOR's own fiscal records.
    /// </summary>
    Task<PaymentTerminalProbeResult> EndOfDayAsync(
        CancellationToken ct = default);

    Task<PaymentTerminalPaymentResult> PayAsync(
        long amountCents,
        string operationId,
        CancellationToken ct = default);

    /// <summary>
    /// R102: a manual credit ("Gutschrift") for a card-payment BON
    /// STORNO/Teilretoure - the customer presents their card again and the
    /// terminal is instructed to credit them the given amount. Unlike
    /// ReversalAsync (which cancels a specific, terminal-tracked original
    /// transaction and real ZVT terminals typically only honor same-day,
    /// often only the most recent one), this has no time limit and no
    /// dependency on the original transaction still being in the
    /// terminal's own memory - it works exactly like TOR's existing
    /// BAR-Storno's "anytime" semantics, just with a card presented
    /// instead of cash handed back.
    /// </summary>
    Task<PaymentTerminalPaymentResult> RefundAsync(
        long amountCents,
        string operationId,
        CancellationToken ct = default);
}
