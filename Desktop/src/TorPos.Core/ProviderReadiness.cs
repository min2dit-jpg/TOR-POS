namespace TorPos.Core;

/// <summary>What a provider can do in principle - not whether it may do it now.</summary>
[Flags]
public enum ProviderCapability
{
    None = 0,
    TseSigning = 1 << 0,
    TseExport = 1 << 1,
    CardPayment = 1 << 2,
    CardRefund = 1 << 3,
    DigitalReceiptQr = 1 << 4,
    DigitalReceiptPdf = 1 << 5,
    DigitalReceiptEmail = 1 << 6,
    DigitalReceiptDownload = 1 << 7,
    FiscalNotification = 1 << 8
}

public enum ProviderHealth
{
    Unknown,
    Healthy,
    Degraded,
    Unavailable
}

/// <summary>
/// The state of one payment, TSE, digital-receipt or notification provider,
/// kept as separate facts. "Connected" is not "fiscally approved": a provider
/// that answers (<see cref="Reachable"/>) but has not passed its release gate
/// (<see cref="Validated"/>) must never be treated as usable for a fiscal
/// step. Provider-specific behaviour lives behind the provider interfaces
/// (<see cref="ITseProvider"/>, <see cref="IPaymentTerminalService"/>,
/// <see cref="IDigitalReceiptProvider"/>, <see cref="IFiscalNotificationProvider"/>),
/// not in MainWindow or the checkout.
/// </summary>
public sealed record ProviderReadiness(
    string ProviderId,
    ProviderCapability Capability,
    bool Configured,
    bool Reachable,
    bool Validated,
    ProviderHealth Health,
    string Message = "")
{
    /// <summary>All four facts must hold; none implies another.</summary>
    public bool Usable => Configured && Reachable && Validated && Health == ProviderHealth.Healthy;

    public bool Can(ProviderCapability capability) =>
        capability != ProviderCapability.None && (Capability & capability) == capability;

    /// <summary>German operator text. Never says "bereit" unless <see cref="Usable"/>.</summary>
    public string StatusText =>
        !Configured ? "Nicht eingerichtet"
        : !Reachable ? "Nicht erreichbar"
        : !Validated ? "Verbunden, aber nicht fiskal freigegeben"
        : Health != ProviderHealth.Healthy ? "Verbunden, Zustand eingeschränkt"
        : "Bereit";

    public static ProviderReadiness NotConfigured(string providerId, ProviderCapability capability, string message = "") =>
        new(providerId, capability, false, false, false, ProviderHealth.Unknown, message);
}
