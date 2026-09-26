namespace TorPos.Core;

public sealed record PaymentTerminalProfile(
    string Id,
    string Manufacturer,
    string Family,
    string Integration,
    string TorStatus,
    string Notes,
    string Protocol = "ZVT_TCP",
    bool ProductionReady = false,
    bool RequiresNetworkEndpoint = false,
    int DefaultPort = 0,
    string SetupHint = "");

/// <summary>
/// R167: one reviewed catalogue for the terminal picker. A brand name alone
/// never implies compatibility: each entry states the actual integration
/// contract TOR can use on Windows. ZVT-backed profiles are immediately
/// routable through the existing production-safe ZVT adapter; proprietary
/// providers remain fail-closed until their documented partner/API adapter
/// is configured.
/// </summary>
public static class PaymentTerminalProfiles
{
    public static IReadOnlyList<PaymentTerminalProfile> All { get; } =
    [
        new(
            "AUTO_ZVT",
            "Universal / ZVT",
            "ZVT-fähiges Terminal",
            "ZVT über TCP/IP",
            "TOR AKTIV",
            "Empfohlen, wenn der Netzbetreiber ZVT freigeschaltet hat.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "IP-Adresse und ZVT-Port am Terminal ablesen; beide Geräte müssen im selben erreichbaren Netzwerk sein."),

        new(
            "PAYONE_ZVT",
            "PAYONE",
            "PAYONE Terminals mit aktivierter Kassenschnittstelle",
            "ZVT über TCP/IP",
            "TOR AKTIV",
            "PAYONE dokumentiert ZVT/OPI für die Kassenanbindung. Entscheidend sind IP-Adresse, Port und die am Terminal aktivierte ZVT-Schnittstelle.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "Am PAYONE-Terminal ZVT aktivieren bzw. vom Provider freischalten lassen, danach IP-Adresse und Port in TOR eintragen."),

        new(
            "CCV_ZVT",
            "CCV",
            "CCV Integrated / ZVT-fähige CCV-Terminals",
            "ZVT über TCP/IP",
            "TOR AKTIV",
            "CCV unterstützt für integrierte Kassensysteme ZVT und OPI. TOR verwendet den ZVT-TCP/IP-Weg.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "ZVT im CCV-Terminal bzw. beim Netzbetreiber aktivieren und IP/Port übernehmen."),

        new(
            "SPARKASSE_ZVT",
            "Sparkasse / S-Händlerservice",
            "Klassisches ZVT-fähiges Kartenterminal",
            "ZVT über TCP/IP",
            "TOR AKTIV · MODELL PRÜFEN",
            "Für klassische, über S-Händlerservice/PAYONE bereitgestellte ZVT-Terminals. S-POS App und S-POS Cube sind hiervon ausdrücklich nicht umfasst.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "Beim S-Händlerservice nach 'Kassenanbindung/ZVT' fragen. Nur ZVT-fähige Hardware als dieses Profil einrichten."),

        new(
            "INGENICO_ZVT",
            "Ingenico / Netzbetreiber",
            "Desk / Move / Lane / weitere ZVT-Modelle",
            "ZVT über TCP/IP",
            "TOR AKTIV · PROVIDERABHÄNGIG",
            "Die konkrete Payment-Applikation des Netzbetreibers muss ZVT bereitstellen.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "ZVT durch den Netzbetreiber aktivieren lassen; danach IP-Adresse und Port übernehmen."),

        new(
            "VERIFONE_ZVT",
            "Verifone / Netzbetreiber",
            "ZVT-fähige Verifone-Terminals",
            "ZVT über TCP/IP",
            "TOR AKTIV · PROVIDERABHÄNGIG",
            "Nur verwenden, wenn die installierte Terminalsoftware ZVT freigibt.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "Kassenprotokoll ZVT am Terminal/Provider aktivieren und Netzwerkdaten eintragen."),

        new(
            "PAX_PROVIDER_ZVT",
            "PAX / Netzbetreiber",
            "A-Serie / providerabhängig",
            "ZVT, falls durch Payment-App/Netzbetreiber bereitgestellt",
            "TOR AKTIV · ZVT MUSS BESTÄTIGT SEIN",
            "PAX-Hardware allein garantiert kein ZVT. Die installierte Payment-App und der Netzbetreiber entscheiden.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "Vor Aktivierung beim Anbieter ausdrücklich nach ZVT-Kassenanbindung fragen."),

        new(
            "OTHER_ZVT",
            "Weitere Hersteller",
            "ZVT-fähiges Payment-Terminal",
            "ZVT über TCP/IP",
            "TOR AKTIV · TEST ERFORDERLICH",
            "Herstellerunabhängiger Rückfallweg für ein nachweislich ZVT-fähiges Terminal.",
            "ZVT_TCP",
            ProductionReady: true,
            RequiresNetworkEndpoint: true,
            DefaultPort: 20007,
            SetupHint: "ZVT-Unterstützung, IP-Adresse und Port beim Terminalanbieter bestätigen lassen."),

        new(
            "SUMUP_CLOUD",
            "SumUp",
            "Solo / Cloud-API-fähiger Reader",
            "SumUp Readers Cloud API",
            "TOR GERÄTETEST VORHANDEN",
            "TOR besitzt bereits einen getrennten SumUp-Geräte-/Pairingtest. Der produktive Verkaufsadapter bleibt getrennt vom ZVT-Weg und wird nur mit gültigen SumUp-Zugangsdaten aktiviert.",
            "SUMUP_CLOUD",
            ProductionReady: false,
            SetupHint: "Merchant Code, API-Key und gekoppelten Reader verwenden. Kein IP-/ZVT-Port erforderlich."),

        new(
            "MYPOS_EPOS",
            "myPOS",
            "Carbon / Ultra / Sigma / Flex",
            "myPOS ePOS REST API",
            "ADAPTER VORBEREITET · PARTNERFREIGABE NÖTIG",
            "myPOS stellt für Desktop-Systeme eine ePOS API bereit. Produktiv benötigt TOR eine myPOS-Partnerintegration plus Händlerfreigabe.",
            "MYPOS_EPOS",
            ProductionReady: false,
            SetupHint: "Nach TOR-myPOS-Partnerfreigabe verbindet der Händler sein Konto; Terminal-ID wird anschließend automatisch auswählbar."),

        new(
            "MYPOS_DOTNET",
            "myPOS",
            "Go2 / Sigma und unterstützte Geräte",
            "myPOS .NET Slave SDK",
            "SDK-ADAPTER VORBEREITET",
            "Für lokale Windows-Anbindung über das offizielle myPOS .NET SDK; Hersteller-SDK/Integrationsfreigabe muss bereitgestellt werden.",
            "MYPOS_DOTNET",
            ProductionReady: false,
            SetupHint: "Gerät in Cash-Register/Slave-Modus versetzen. SDK-Paket und Gerätemodell müssen zur TOR-Version passen."),

        new(
            "READYPAY_API",
            "ready2order / readyPay",
            "readyMini / readyGo / readyTab im Terminalmodus",
            "ready2order Terminal Transaction API",
            "API-ADAPTER VORBEREITET · FREIGABE PRÜFEN",
            "Die öffentliche API kann readyPay-Terminaltransaktionen starten, verlangt dabei aber invoiceData. TOR aktiviert diesen Weg erst, wenn ein terminal-only Ablauf ohne doppelte Belegerzeugung vertraglich/technisch geklärt ist.",
            "READYPAY_API",
            ProductionReady: false,
            SetupHint: "readyPay für den Account freischalten und Gerät in Terminalmodus koppeln. TOR verhindert bis zur Freigabe doppelte Fiskal-/Belegerzeugung."),

        new(
            "ZETTLE_SDK",
            "PayPal Zettle (iZettle)",
            "Zettle Reader",
            "Zettle Payments SDK",
            "WINDOWS-DIREKTADAPTER NICHT VERFÜGBAR",
            "Zettle erlaubt Kartenzahlungen über die offiziellen Android/iOS Payments SDKs; die REST APIs lösen keine Kartenleser-Zahlung auf Windows aus.",
            "ZETTLE_SDK",
            ProductionReady: false,
            SetupHint: "Für TOR Windows ist ein offizieller Desktop-/Partnerweg nötig; bis dahin kein automatisches Abbuchen vortäuschen."),

        new(
            "FLATPAY_PARTNER",
            "Flatpay",
            "Flatpay Payment Terminal / PAX-basierte Geräte",
            "Flatpay Partner-Schnittstelle",
            "PARTNERZUGANG ERFORDERLICH",
            "Flatpay veröffentlicht derzeit keine für TOR nutzbare offene externe Windows-Kassenterminal-API. Die Adapterstelle ist vorbereitet, bleibt aber fail-closed.",
            "FLATPAY_PARTNER",
            ProductionReady: false,
            SetupHint: "Flatpay muss für TOR eine dokumentierte ECR/Partner-Schnittstelle freigeben. Ohne diese Freigabe wird keine automatische Zahlung gestartet.")
    ];

    /// <summary>
    /// O-19: returned for a stored profile id TOR does not know. It is never
    /// production-ready and speaks no protocol, so the ZVT adapter refuses it
    /// instead of charging a card through an assumed AUTO_ZVT profile.
    /// </summary>
    public static PaymentTerminalProfile Unknown { get; } = new(
        "UNKNOWN",
        "Unbekanntes Profil",
        "Nicht erkannt",
        "Keine",
        "NICHT FREIGEGEBEN",
        "Das gespeicherte Terminal-Profil ist TOR nicht bekannt. Bitte unter Geräte → KARTENTERMINAL VERBINDEN neu auswählen.",
        "UNKNOWN",
        ProductionReady: false,
        SetupHint: "Terminal-Profil neu auswählen.");

    public static PaymentTerminalProfile Find(string? id)
    {
        var key = (id ?? "").Trim();
        // Nothing stored yet: the recommended default, as before.
        if (key.Length == 0)
            return All[0];
        key = key.ToUpperInvariant() switch
        {
            "ZVT" or "ZVT_TCP" or "GENERIC" or "STANDARD" => "AUTO_ZVT",
            "INGENICO" => "INGENICO_ZVT",
            "VERIFONE" => "VERIFONE_ZVT",
            "PAX" => "PAX_PROVIDER_ZVT",
            "OTHER" => "OTHER_ZVT",
            "SUMUP" => "SUMUP_CLOUD",
            "PAYONE" => "PAYONE_ZVT",
            "CCV" => "CCV_ZVT",
            "SPARKASSE" or "S_HAENDLERSERVICE" => "SPARKASSE_ZVT",
            "READYPAY" or "READYMINI" => "READYPAY_API",
            "IZETTLE" or "ZETTLE" => "ZETTLE_SDK",
            "FLATPAY" => "FLATPAY_PARTNER",
            "MYPOS" => "MYPOS_EPOS",
            _ => key
        };

        return All.FirstOrDefault(x =>
            string.Equals(x.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? Unknown;
    }

    public static bool UsesZvt(string? id) =>
        string.Equals(Find(id).Protocol, "ZVT_TCP", StringComparison.OrdinalIgnoreCase);
}

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
