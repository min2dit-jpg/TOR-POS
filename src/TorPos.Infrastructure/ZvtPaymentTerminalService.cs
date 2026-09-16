using Portalum.Zvt;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// German-market card terminal integration through the manufacturer-neutral
/// ZVT cash-register protocol. TOR currently activates TCP/IP transport.
/// No PAN, track data, PIN or CVV is stored by this service.
/// </summary>
public sealed class ZvtPaymentTerminalService : IPaymentTerminalService
{
    private readonly ISettingsRepository _settings;
    private readonly IAuditLog _audit;
    private readonly ICheckoutJournal _journal;

    public ZvtPaymentTerminalService(
        ISettingsRepository settings,
        IAuditLog audit,
        ICheckoutJournal journal)
    {
        _settings = settings;
        _audit = audit;
        _journal = journal;
    }

    public IReadOnlyList<PaymentTerminalProfile> Profiles { get; } =
        new[]
        {
            new PaymentTerminalProfile(
                "AUTO_ZVT",
                "Herstellerunabhängig",
                "ZVT-fähiges Terminal",
                "ZVT über TCP/IP",
                "TOR AKTIV",
                "Bevorzugter Universalmodus. Terminal/Netzbetreiber muss ZVT TCP/IP freischalten."),

            new PaymentTerminalProfile(
                "INGENICO_ZVT",
                "Ingenico",
                "AXIUM / Desk / Lane / weitere ZVT-Modelle",
                "ZVT über TCP/IP",
                "TOR AKTIV",
                "ZVT muss in der Payment-Applikation bzw. beim Netzbetreiber aktiviert sein."),

            new PaymentTerminalProfile(
                "CCV_ZVT",
                "CCV",
                "Pad Next / Pad / Q25 / weitere ZVT-Modelle",
                "ZVT über TCP/IP",
                "TOR AKTIV",
                "CCV dokumentiert ZVT und O.P.I.; TOR nutzt in dieser Version ZVT TCP/IP."),

            new PaymentTerminalProfile(
                "VERIFONE_ZVT",
                "Verifone / TeleCash",
                "ZVT-fähige Verifone-Terminals",
                "ZVT über TCP/IP",
                "TOR AKTIV",
                "TeleCash dokumentiert ZVT über TCP/IP, COM und USB; TOR nutzt aktuell TCP/IP."),

            new PaymentTerminalProfile(
                "OTHER_ZVT",
                "Weitere Hersteller",
                "ZVT-fähige Payment-Terminals",
                "ZVT über TCP/IP",
                "TEST ERFORDERLICH",
                "Kompatibel, wenn die konkrete Terminalsoftware des Netzbetreibers ZVT bereitstellt."),

            new PaymentTerminalProfile(
                "PAX_PROVIDER_ZVT",
                "PAX",
                "A-Serie / providerabhängig",
                "ZVT falls vom Netzbetreiber bereitgestellt",
                "TEST ERFORDERLICH",
                "Nicht pauschal für jedes PAX-Gerät zugesagt; Payment-App und Netzbetreiber entscheiden."),

            new PaymentTerminalProfile(
                "SUMUP",
                "SumUp",
                "SumUp Terminal / Reader",
                "Herstellerspezifische Integration",
                "NICHT ZVT-VERIFIZIERT",
                "Nicht als universelles ZVT-Terminal freigegeben; separater SumUp-Adapter wäre erforderlich."),

            new PaymentTerminalProfile(
                "STRIPE",
                "Stripe Terminal",
                "WisePOS E / S700/S710 / Verifone Reader",
                "Stripe Terminal API",
                "SEPARATER ADAPTER",
                "Stripe nutzt SDK/server-driven API; nicht über den TOR-ZVT-Adapter."),

            new PaymentTerminalProfile(
                "ADYEN",
                "Adyen",
                "Adyen Payment Terminals",
                "Adyen Terminal API / nexo",
                "SEPARATER ADAPTER",
                "Lokale oder Cloud-Terminal-API; nicht über den TOR-ZVT-Adapter.")
        };

    public async Task<PaymentTerminalProbeResult> ProbeAsync(
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(ct);

        if (!cfg.Enabled)
            return new PaymentTerminalProbeResult(
                false,
                "DEAKTIVIERT",
                "Kartenterminal-Integration ist nicht aktiviert.",
                cfg.Endpoint);

        var validation = Validate(cfg);
        if (validation is not null)
            return new PaymentTerminalProbeResult(
                false,
                "KONFIGURATION_FEHLER",
                validation,
                cfg.Endpoint);

        using var communication =
            new TcpNetworkDeviceCommunication(cfg.IpAddress, cfg.Port);

        try
        {
            var connectTask = communication.ConnectAsync();
            var finished = await Task.WhenAny(
                connectTask,
                Task.Delay(TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds), ct));

            if (finished != connectTask)
            {
                return await SaveProbeAsync(
                    cfg,
                    false,
                    "TIMEOUT",
                    $"Keine TCP-Verbindung innerhalb von {cfg.ConnectTimeoutSeconds}s.",
                    ct);
            }

            var connected = await connectTask;
            return await SaveProbeAsync(
                cfg,
                connected,
                connected ? "TCP_OK" : "NICHT_ERREICHBAR",
                connected
                    ? $"TCP-Verbindung zum ZVT-Terminal hergestellt: {cfg.Endpoint}"
                    : $"ZVT-Terminal nicht erreichbar: {cfg.Endpoint}",
                ct);
        }
        catch (Exception ex)
        {
            return await SaveProbeAsync(
                cfg,
                false,
                "FEHLER",
                $"Verbindung fehlgeschlagen: {ex.Message}",
                ct);
        }
    }

    public async Task<PaymentTerminalProbeResult> RegisterAsync(
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(ct);
        var validation = Validate(cfg);

        if (validation is not null)
            return new PaymentTerminalProbeResult(
                false,
                "KONFIGURATION_FEHLER",
                validation,
                cfg.Endpoint);

        using var communication =
            new TcpNetworkDeviceCommunication(cfg.IpAddress, cfg.Port);

        try
        {
            using var connectTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(ct);

            connectTimeout.CancelAfter(
                TimeSpan.FromSeconds(
                    cfg.ConnectTimeoutSeconds));

            var connectTask =
                communication.ConnectAsync();

            var connectFinished =
                await Task.WhenAny(
                    connectTask,
                    Task.Delay(
                        TimeSpan.FromSeconds(
                            cfg.ConnectTimeoutSeconds),
                        connectTimeout.Token));

            if (connectFinished != connectTask ||
                !await connectTask)
            {
                return await SaveProbeAsync(
                    cfg,
                    false,
                    "NICHT_ERREICHBAR",
                    $"ZVT-Terminal nicht innerhalb von {cfg.ConnectTimeoutSeconds}s erreichbar: {cfg.Endpoint}",
                    ct);
            }

            using var zvt = new ZvtClient(communication);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds));

            var registration = await zvt.RegistrationAsync(
                new RegistrationConfig(),
                timeout.Token);

            var success =
                registration.State == CommandResponseState.Successful;

            return await SaveProbeAsync(
                cfg,
                success,
                success ? "ZVT_ANGEMELDET" : registration.State.ToString().ToUpperInvariant(),
                success
                    ? "ZVT-Anmeldung erfolgreich."
                    : $"ZVT-Anmeldung fehlgeschlagen: {registration.ErrorMessage ?? registration.State.ToString()}",
                ct);
        }
        catch (OperationCanceledException)
        {
            return await SaveProbeAsync(
                cfg,
                false,
                "TIMEOUT",
                "ZVT-Anmeldung wurde wegen Zeitüberschreitung beendet.",
                ct);
        }
        catch (Exception ex)
        {
            return await SaveProbeAsync(
                cfg,
                false,
                "FEHLER",
                $"ZVT-Anmeldung fehlgeschlagen: {ex.Message}",
                ct);
        }
    }

    // R94: ZVT End-of-Day (06 50) - the terminal's own daily batch
    // settlement, unrelated to TOR's own Z-Bericht/Kassenabschluss. Mirrors
    // RegisterAsync's connect/timeout/audit shape exactly, since this is
    // likewise a single connect-and-send-one-command operation, not a
    // payment - no checkout journal, no fiscal gate involved.
    public async Task<PaymentTerminalProbeResult> EndOfDayAsync(
        CancellationToken ct = default)
    {
        var cfg = await LoadConfigAsync(ct);
        var validation = Validate(cfg);

        if (!cfg.Enabled)
            return new PaymentTerminalProbeResult(
                false,
                "DEAKTIVIERT",
                "Kartenterminal-Integration ist nicht aktiviert.",
                cfg.Endpoint);

        if (validation is not null)
            return new PaymentTerminalProbeResult(
                false,
                "KONFIGURATION_FEHLER",
                validation,
                cfg.Endpoint);

        using var communication =
            new TcpNetworkDeviceCommunication(cfg.IpAddress, cfg.Port);

        try
        {
            var connectTask = communication.ConnectAsync();
            var connectFinished = await Task.WhenAny(
                connectTask,
                Task.Delay(TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds), ct));

            if (connectFinished != connectTask || !await connectTask)
            {
                return await SaveEndOfDayAsync(
                    cfg,
                    false,
                    "NICHT_ERREICHBAR",
                    $"ZVT-Terminal nicht innerhalb von {cfg.ConnectTimeoutSeconds}s erreichbar: {cfg.Endpoint}",
                    ct);
            }

            using var zvt = new ZvtClient(communication);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds));

            var response = await zvt.EndOfDayAsync(timeout.Token);
            var success = response.State == CommandResponseState.Successful;

            return await SaveEndOfDayAsync(
                cfg,
                success,
                success ? "TAGESABSCHLUSS_OK" : response.State.ToString().ToUpperInvariant(),
                success
                    ? "Terminal-Tagesabschluss ausgelöst; Ergebnis liegt beim Netzbetreiber/auf dem Terminalbeleg."
                    : $"Terminal-Tagesabschluss fehlgeschlagen: {response.ErrorMessage ?? response.State.ToString()}",
                ct);
        }
        catch (OperationCanceledException)
        {
            return await SaveEndOfDayAsync(
                cfg,
                false,
                "TIMEOUT",
                "Terminal-Tagesabschluss wurde wegen Zeitüberschreitung beendet.",
                ct);
        }
        catch (Exception ex)
        {
            return await SaveEndOfDayAsync(
                cfg,
                false,
                "FEHLER",
                $"Terminal-Tagesabschluss fehlgeschlagen: {ex.Message}",
                ct);
        }
    }

    public async Task<PaymentTerminalPaymentResult> PayAsync(
        long amountCents,
        string operationId,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireProduction();
        var operation = (await _journal.GetOpenAsync()).SingleOrDefault(x => x.Snapshot.OperationId == operationId);
        // R101: the charge amount is the snapshot's CARD portion, not
        // necessarily its whole total - for a Mixed sale these differ.
        if (operation is null || operation.State != "PREPARED" || operation.Snapshot.EffectiveCardPortionCents != amountCents)
            throw new InvalidOperationException("Zahlungsauftrag fehlt, ist bereits gesendet oder hat einen anderen Betrag.");
        var submitted = false;

        // Declared here (not inside the try block) so that a failure while
        // persisting the terminal's own response can still record whatever
        // trace/receipt/terminal-id evidence the terminal already reported,
        // instead of losing it the moment control reaches a catch block.
        int? terminalId = null;
        int? terminalReceipt = null;
        int? traceNumber = null;
        string cardName = "";
        string lastTerminalMessage = "";
        if (amountCents <= 0)
            return new PaymentTerminalPaymentResult(
                false,
                "INVALID_AMOUNT",
                "Kartenzahlungsbetrag muss größer als 0 sein.",
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "INVALID_AMOUNT");

        var cfg = await LoadConfigAsync(ct);
        var validation = Validate(cfg);

        if (!cfg.Enabled)
            return new PaymentTerminalPaymentResult(
                false,
                "DEAKTIVIERT",
                "Kartenterminal-Integration ist nicht aktiviert.",
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "DISABLED");

        if (validation is not null)
            return new PaymentTerminalPaymentResult(
                false,
                "KONFIGURATION_FEHLER",
                validation,
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "CONFIG_ERROR");

        var correlation = operationId;
        await _audit.WriteAsync(
            "SYSTEM",
            "CARD_TERMINAL_START",
            "PAYMENT_TERMINAL",
            correlation,
            $"protocol=ZVT_TCP; endpoint={cfg.Endpoint}; amount_cents={amountCents}",
            ct);

        using var communication =
            new TcpNetworkDeviceCommunication(cfg.IpAddress, cfg.Port);

        try
        {
            var connectTask =
                communication.ConnectAsync();

            var connectFinished =
                await Task.WhenAny(
                    connectTask,
                    Task.Delay(
                        TimeSpan.FromSeconds(
                            cfg.ConnectTimeoutSeconds),
                        ct));

            if (connectFinished != connectTask ||
                !await connectTask)
            {
                await WritePaymentAuditAsync(
                    correlation,
                    false,
                    "CONNECT_TIMEOUT",
                    amountCents,
                    cfg,
                    "",
                    ct);

                return new PaymentTerminalPaymentResult(
                    false,
                    "NICHT_ERREICHBAR",
                    $"Kartenterminal nicht innerhalb von {cfg.ConnectTimeoutSeconds}s erreichbar: {cfg.Endpoint}",
                    Outcome: PaymentTerminalOutcome.NotSent,
                    RequestSubmitted: false,
                    OutcomeCode: "CONNECT_TIMEOUT");
            }

            using var zvt = new ZvtClient(communication);

            zvt.IntermediateStatusInformationReceived += message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                    lastTerminalMessage = message;
            };

            zvt.StatusInformationReceived += status =>
            {
                terminalId = status.TerminalIdentifier > 0
                    ? status.TerminalIdentifier
                    : terminalId;

                terminalReceipt = status.ReceiptNumber > 0
                    ? status.ReceiptNumber
                    : terminalReceipt;

                traceNumber = status.TraceNumber > 0
                    ? status.TraceNumber
                    : traceNumber;

                // CardName is safe for reconciliation; PAN/track/PIN/CVV are never persisted.
                if (!string.IsNullOrWhiteSpace(status.CardName))
                    cardName = status.CardName;

                if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
                    lastTerminalMessage = status.ErrorMessage;
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds));

            if (cfg.RegisterBeforePayment)
            {
                var registration = await zvt.RegistrationAsync(
                    new RegistrationConfig(),
                    timeout.Token);

                if (registration.State != CommandResponseState.Successful)
                {
                    var registrationMessage =
                        registration.ErrorMessage ?? registration.State.ToString();

                    await WritePaymentAuditAsync(
                        correlation, false, "REGISTRATION_FAILED",
                        amountCents, cfg, registrationMessage, ct);

                    return new PaymentTerminalPaymentResult(
                        false,
                        "ZVT_ANMELDUNG_FEHLER",
                        $"Terminal-Anmeldung fehlgeschlagen: {registrationMessage}",
                        Outcome: PaymentTerminalOutcome.NotSent,
                        RequestSubmitted: false,
                        OutcomeCode: "REGISTRATION_FAILED");
                }
            }

            var amount = amountCents / 100m;

            // Durable BEFORE the external payment call. From this point on a
            // timeout/exception must be treated as potentially charged.
            await _journal.MarkTerminalSubmittedAsync(
                operationId,
                "ZVT payment command admitted");

            submitted = true;

            var response =
                await zvt.PaymentAsync(
                    amount,
                    timeout.Token);

            var success =
                response.State ==
                CommandResponseState.Successful;

            var rawState =
                response.State.ToString();

            var rawError =
                response.ErrorMessage ?? "";

            var outcome =
                success
                    ? PaymentTerminalOutcome.Approved
                    : PaymentTerminalOutcomeClassifier
                        .ClassifyCompletedFailure(
                            rawState,
                            rawError,
                            lastTerminalMessage);

            var targetState =
                outcome switch
                {
                    PaymentTerminalOutcome.Approved =>
                        "APPROVED",

                    PaymentTerminalOutcome.Declined or
                    PaymentTerminalOutcome.Cancelled =>
                        "NOT_CHARGED",

                    _ =>
                        "UNKNOWN"
                };

            var journalEvidence =
                $"terminal_id={terminalId}; " +
                $"receipt={terminalReceipt}; " +
                $"trace={traceNumber}; " +
                $"state={rawState}; " +
                $"outcome={PaymentOutcomeCodec.ToStorage(outcome)}";

            var message =
                outcome switch
                {
                    PaymentTerminalOutcome.Approved =>
                        "Kartenzahlung vom Terminal bestätigt.",

                    PaymentTerminalOutcome.Declined =>
                        "Kartenzahlung ausdrücklich abgelehnt. " +
                        "Keine Belastung bestätigt; Bon bleibt offen.",

                    PaymentTerminalOutcome.Cancelled =>
                        "Kartenzahlung ausdrücklich abgebrochen. " +
                        "Keine Belastung bestätigt; Bon bleibt offen.",

                    _ =>
                        "Zahlungsstatus unklar. Terminal prüfen; nicht erneut kassieren."
                };

            await _journal.TransitionTerminalAsync(
                operationId,
                "SENT",
                targetState,
                journalEvidence,
                outcome,
                requestSubmitted: true,
                terminalCode: rawState,
                terminalMessage:
                    string.IsNullOrWhiteSpace(rawError)
                        ? lastTerminalMessage
                        : rawError);

            await WritePaymentAuditAsync(
                correlation,
                success,
                rawState,
                amountCents,
                cfg,
                $"outcome={PaymentOutcomeCodec.ToStorage(outcome)}; " +
                $"terminal_id={terminalId}; receipt={terminalReceipt}; " +
                $"trace={traceNumber}; card={cardName}",
                ct);

            return new PaymentTerminalPaymentResult(
                success,
                outcome switch
                {
                    PaymentTerminalOutcome.Approved => "APPROVED",
                    PaymentTerminalOutcome.Declined => "DECLINED",
                    PaymentTerminalOutcome.Cancelled => "CANCELLED",
                    _ => "UNGEKLAERT"
                },
                message,
                terminalId,
                terminalReceipt,
                traceNumber,
                cardName,
                Outcome: outcome,
                RequestSubmitted: true,
                OutcomeCode: rawState);
        }
        catch (OperationCanceledException)
        {
            var evidence =
                $"terminal_id={terminalId}; receipt={terminalReceipt}; " +
                $"trace={traceNumber}; card={cardName}";

            if (submitted)
            {
                await TryMarkUnknownAsync(
                    operationId,
                    "TIMEOUT",
                    $"Zeitüberschreitung nach Start der Kartenzahlung. {evidence}");
            }

            await WritePaymentAuditAsync(
                correlation,
                false,
                "TIMEOUT",
                amountCents,
                cfg,
                $"outcome={(submitted ? "UNKNOWN" : "NOT_SENT")}; {evidence}",
                CancellationToken.None);

            return new PaymentTerminalPaymentResult(
                false,
                submitted
                    ? "UNGEKLAERT_TIMEOUT"
                    : "NOT_SENT",
                submitted
                    ? "Zeitüberschreitung nach Start der Kartenzahlung. " +
                      "Der Zahlungsstatus ist NICHT sicher. Nicht automatisch erneut kassieren. " +
                      "Zuerst Terminalbeleg / Trace / Netzbetreiber prüfen."
                    : "Kartenzahlung wurde vor dem Zahlungsauftrag abgebrochen. Keine Belastung gestartet.",
                Outcome:
                    submitted
                        ? PaymentTerminalOutcome.Unknown
                        : PaymentTerminalOutcome.NotSent,
                RequestSubmitted: submitted,
                OutcomeCode: "TIMEOUT");
        }
        catch (Exception ex)
        {
            var evidence =
                $"terminal_id={terminalId}; receipt={terminalReceipt}; " +
                $"trace={traceNumber}; card={cardName}";

            if (submitted)
            {
                await TryMarkUnknownAsync(
                    operationId,
                    ex.GetType().Name,
                    $"Fehler nach Start der Kartenzahlung. {evidence}");
            }

            await WritePaymentAuditAsync(
                correlation,
                false,
                "ERROR",
                amountCents,
                cfg,
                $"outcome={(submitted ? "UNKNOWN" : "NOT_SENT")}; type={ex.GetType().Name}; {evidence}",
                CancellationToken.None);

            return new PaymentTerminalPaymentResult(
                false,
                submitted
                    ? "UNGEKLAERT"
                    : "NOT_SENT",
                submitted
                    ? "Zahlungsstatus unklar. Nicht erneut kassieren; Terminal prüfen."
                    : $"Kartenterminal-Fehler vor Zahlungsstart: {ex.Message}",
                Outcome:
                    submitted
                        ? PaymentTerminalOutcome.Unknown
                        : PaymentTerminalOutcome.NotSent,
                RequestSubmitted: submitted,
                OutcomeCode: ex.GetType().Name);
        }
    }

    // R102: RefundAsync deliberately does NOT touch _journal/checkout_operations
    // - there is no journal row for a BON STORNO/Teilretoure refund the way
    // there is for an original checkout, so this is a simpler, self-contained
    // connect->command->classify->audit shell (not a deduplicated copy of
    // PayAsync's journal-integrated state machine, on purpose - keeping this
    // separate means the already-tested original checkout/PayAsync path is
    // untouched by this new capability).
    public async Task<PaymentTerminalPaymentResult> RefundAsync(
        long amountCents,
        string operationId,
        CancellationToken ct = default)
    {
        FiscalRelease.RequireProduction();

        if (amountCents <= 0)
            return new PaymentTerminalPaymentResult(
                false,
                "INVALID_AMOUNT",
                "Erstattungsbetrag muss größer als 0 sein.",
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "INVALID_AMOUNT");

        var cfg = await LoadConfigAsync(ct);
        var validation = Validate(cfg);

        if (!cfg.Enabled)
            return new PaymentTerminalPaymentResult(
                false,
                "DEAKTIVIERT",
                "Kartenterminal-Integration ist nicht aktiviert.",
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "DISABLED");

        if (validation is not null)
            return new PaymentTerminalPaymentResult(
                false,
                "KONFIGURATION_FEHLER",
                validation,
                Outcome: PaymentTerminalOutcome.NotSent,
                RequestSubmitted: false,
                OutcomeCode: "CONFIG_ERROR");

        var correlation = operationId;
        await _audit.WriteAsync(
            "SYSTEM",
            "CARD_TERMINAL_REFUND_START",
            "PAYMENT_TERMINAL",
            correlation,
            $"protocol=ZVT_TCP; endpoint={cfg.Endpoint}; amount_cents={amountCents}",
            ct);

        using var communication =
            new TcpNetworkDeviceCommunication(cfg.IpAddress, cfg.Port);

        int? terminalId = null;
        int? terminalReceipt = null;
        int? traceNumber = null;
        string cardName = "";
        string lastTerminalMessage = "";
        var submitted = false;

        try
        {
            var connectTask = communication.ConnectAsync();
            var connectFinished = await Task.WhenAny(
                connectTask,
                Task.Delay(TimeSpan.FromSeconds(cfg.ConnectTimeoutSeconds), ct));

            if (connectFinished != connectTask || !await connectTask)
            {
                await WritePaymentAuditAsync(
                    correlation, false, "CONNECT_TIMEOUT", amountCents, cfg, "", ct);

                return new PaymentTerminalPaymentResult(
                    false,
                    "NICHT_ERREICHBAR",
                    $"Kartenterminal nicht innerhalb von {cfg.ConnectTimeoutSeconds}s erreichbar: {cfg.Endpoint}",
                    Outcome: PaymentTerminalOutcome.NotSent,
                    RequestSubmitted: false,
                    OutcomeCode: "CONNECT_TIMEOUT");
            }

            using var zvt = new ZvtClient(communication);

            zvt.IntermediateStatusInformationReceived += message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                    lastTerminalMessage = message;
            };

            zvt.StatusInformationReceived += status =>
            {
                terminalId = status.TerminalIdentifier > 0 ? status.TerminalIdentifier : terminalId;
                terminalReceipt = status.ReceiptNumber > 0 ? status.ReceiptNumber : terminalReceipt;
                traceNumber = status.TraceNumber > 0 ? status.TraceNumber : traceNumber;
                if (!string.IsNullOrWhiteSpace(status.CardName))
                    cardName = status.CardName;
                if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
                    lastTerminalMessage = status.ErrorMessage;
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(cfg.CommandTimeoutSeconds));

            if (cfg.RegisterBeforePayment)
            {
                var registration = await zvt.RegistrationAsync(new RegistrationConfig(), timeout.Token);
                if (registration.State != CommandResponseState.Successful)
                {
                    var registrationMessage = registration.ErrorMessage ?? registration.State.ToString();
                    await WritePaymentAuditAsync(
                        correlation, false, "REGISTRATION_FAILED", amountCents, cfg, registrationMessage, ct);

                    return new PaymentTerminalPaymentResult(
                        false,
                        "ZVT_ANMELDUNG_FEHLER",
                        $"Terminal-Anmeldung fehlgeschlagen: {registrationMessage}",
                        Outcome: PaymentTerminalOutcome.NotSent,
                        RequestSubmitted: false,
                        OutcomeCode: "REGISTRATION_FAILED");
                }
            }

            var amount = amountCents / 100m;
            submitted = true;

            var response = await zvt.RefundAsync(amount, timeout.Token);

            var success = response.State == CommandResponseState.Successful;
            var rawState = response.State.ToString();
            var rawError = response.ErrorMessage ?? "";

            var outcome = success
                ? PaymentTerminalOutcome.Approved
                : PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(rawState, rawError, lastTerminalMessage);

            var message = outcome switch
            {
                PaymentTerminalOutcome.Approved => "Kartenerstattung vom Terminal bestätigt.",
                PaymentTerminalOutcome.Declined => "Kartenerstattung ausdrücklich abgelehnt. Keine Gutschrift bestätigt.",
                PaymentTerminalOutcome.Cancelled => "Kartenerstattung ausdrücklich abgebrochen. Keine Gutschrift bestätigt.",
                _ => "Erstattungsstatus unklar. Terminalbeleg prüfen, bevor der Vorgang wiederholt wird."
            };

            await WritePaymentAuditAsync(
                correlation,
                success,
                rawState,
                amountCents,
                cfg,
                $"outcome={PaymentOutcomeCodec.ToStorage(outcome)}; terminal_id={terminalId}; " +
                $"receipt={terminalReceipt}; trace={traceNumber}; card={cardName}",
                ct);

            return new PaymentTerminalPaymentResult(
                success,
                outcome switch
                {
                    PaymentTerminalOutcome.Approved => "APPROVED",
                    PaymentTerminalOutcome.Declined => "DECLINED",
                    PaymentTerminalOutcome.Cancelled => "CANCELLED",
                    _ => "UNGEKLAERT"
                },
                message,
                terminalId,
                terminalReceipt,
                traceNumber,
                cardName,
                Outcome: outcome,
                RequestSubmitted: true,
                OutcomeCode: rawState);
        }
        catch (OperationCanceledException)
        {
            var evidence = $"terminal_id={terminalId}; receipt={terminalReceipt}; trace={traceNumber}; card={cardName}";
            await WritePaymentAuditAsync(
                correlation, false, "TIMEOUT", amountCents, cfg,
                $"outcome={(submitted ? "UNKNOWN" : "NOT_SENT")}; {evidence}", CancellationToken.None);

            return new PaymentTerminalPaymentResult(
                false,
                submitted ? "UNGEKLAERT_TIMEOUT" : "NOT_SENT",
                submitted
                    ? "Zeitüberschreitung nach Start der Kartenerstattung. Status NICHT sicher - Terminalbeleg/Trace prüfen, bevor der Vorgang wiederholt wird."
                    : "Kartenerstattung wurde vor dem Erstattungsauftrag abgebrochen. Keine Gutschrift gestartet.",
                Outcome: submitted ? PaymentTerminalOutcome.Unknown : PaymentTerminalOutcome.NotSent,
                RequestSubmitted: submitted,
                OutcomeCode: "TIMEOUT");
        }
        catch (Exception ex)
        {
            var evidence = $"terminal_id={terminalId}; receipt={terminalReceipt}; trace={traceNumber}; card={cardName}";
            await WritePaymentAuditAsync(
                correlation, false, "ERROR", amountCents, cfg,
                $"outcome={(submitted ? "UNKNOWN" : "NOT_SENT")}; type={ex.GetType().Name}; {evidence}", CancellationToken.None);

            return new PaymentTerminalPaymentResult(
                false,
                submitted ? "UNGEKLAERT" : "NOT_SENT",
                submitted
                    ? "Erstattungsstatus unklar. Terminal prüfen, bevor der Vorgang wiederholt wird."
                    : $"Kartenterminal-Fehler vor Erstattungsstart: {ex.Message}",
                Outcome: submitted ? PaymentTerminalOutcome.Unknown : PaymentTerminalOutcome.NotSent,
                RequestSubmitted: submitted,
                OutcomeCode: ex.GetType().Name);
        }
    }

    private async Task TryMarkUnknownAsync(
        string id,
        string code,
        string message)
    {
        try
        {
            await _journal.TransitionTerminalAsync(
                id,
                "SENT",
                "UNKNOWN",
                message,
                PaymentTerminalOutcome.Unknown,
                requestSubmitted: true,
                terminalCode: code,
                terminalMessage: message);
        }
        catch (Exception ex)
        {
            // SENT/APPROVED/NOT_CHARGED remains durable.
            // Never retry the external payment from here. Still record that the
            // UNKNOWN transition itself failed, so a later audit trail doesn't
            // silently disagree with the actual checkout_operations.state.
            try
            {
                await _audit.WriteAsync(
                    "SYSTEM",
                    "CARD_TERMINAL_UNKNOWN_WRITE_FAILED",
                    "PAYMENT_TERMINAL",
                    id,
                    $"code={code}; write_error={ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // Audit itself unavailable; the journal row's own state remains authoritative.
            }
        }
    }

    private async Task<TerminalConfig> LoadConfigAsync(CancellationToken ct)
    {
        var values = await _settings.LoadAllAsync(ct);

        return new TerminalConfig(
            values.GetValueOrDefault("payment.terminal.enabled") == "true",
            values.GetValueOrDefault("payment.terminal.protocol") ?? "ZVT_TCP",
            values.GetValueOrDefault("payment.terminal.vendor") ?? "AUTO_ZVT",
            values.GetValueOrDefault("payment.terminal.model") ?? "",
            values.GetValueOrDefault("payment.terminal.ip") ?? "",
            ParseInt(values.GetValueOrDefault("payment.terminal.port"), 20007),
            ParseBoundedInt(values.GetValueOrDefault("payment.terminal.connect_timeout_seconds"), 5, 1, 30),
            ParseBoundedInt(values.GetValueOrDefault("payment.terminal.command_timeout_seconds"), 120, 10, 300),
            values.GetValueOrDefault("payment.terminal.register_before_payment") != "false");
    }

    private static string? Validate(TerminalConfig cfg)
    {
        if (!string.Equals(cfg.Protocol, "ZVT_TCP", StringComparison.OrdinalIgnoreCase))
            return "Diese TOR-Version unterstützt produktiv nur ZVT über TCP/IP.";

        if (string.IsNullOrWhiteSpace(cfg.IpAddress))
            return "IP-Adresse des Kartenterminals fehlt.";

        if (cfg.Port is < 1 or > 65535)
            return "Ungültiger TCP-Port.";

        return null;
    }

    private async Task<PaymentTerminalProbeResult> SaveProbeAsync(
        TerminalConfig cfg,
        bool success,
        string state,
        string message,
        CancellationToken ct)
    {
        await _settings.SaveManyAsync(
            new Dictionary<string,string>
            {
                ["payment.terminal.last_test"] =
                    DateTimeOffset.Now.ToString("O"),
                ["payment.terminal.last_status"] = state,
                ["payment.terminal.last_error"] =
                    success ? "" : message
            },
            ct);

        await _audit.WriteAsync(
            "SYSTEM",
            "PAYMENT_TERMINAL_PROBE",
            "PAYMENT_TERMINAL",
            cfg.Vendor,
            $"success={success}; state={state}; endpoint={cfg.Endpoint}",
            ct);

        return new PaymentTerminalProbeResult(
            success,
            state,
            message,
            cfg.Endpoint);
    }

    private async Task<PaymentTerminalProbeResult> SaveEndOfDayAsync(
        TerminalConfig cfg,
        bool success,
        string state,
        string message,
        CancellationToken ct)
    {
        await _settings.SaveManyAsync(
            new Dictionary<string,string>
            {
                ["payment.terminal.end_of_day.last_at"] =
                    DateTimeOffset.Now.ToString("O"),
                ["payment.terminal.end_of_day.last_status"] = state,
                ["payment.terminal.end_of_day.last_error"] =
                    success ? "" : message
            },
            ct);

        await _audit.WriteAsync(
            "SYSTEM",
            "PAYMENT_TERMINAL_END_OF_DAY",
            "PAYMENT_TERMINAL",
            cfg.Vendor,
            $"success={success}; state={state}; endpoint={cfg.Endpoint}",
            ct);

        return new PaymentTerminalProbeResult(
            success,
            state,
            message,
            cfg.Endpoint);
    }

    private async Task WritePaymentAuditAsync(
        string correlation,
        bool success,
        string state,
        long amountCents,
        TerminalConfig cfg,
        string safeDetails,
        CancellationToken ct)
    {
        try
        {
        await _audit.WriteAsync(
            "SYSTEM",
            success ? "CARD_TERMINAL_SUCCESS" : "CARD_TERMINAL_FAILED",
            "PAYMENT_TERMINAL",
            correlation,
            $"state={state}; protocol=ZVT_TCP; endpoint={cfg.Endpoint}; " +
            $"amount_cents={amountCents}; {safeDetails}",
            ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Payment audit failed: " + ex);
        }
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    // R29: corrupted or accidental timeout settings must never make the
    // cashier wait indefinitely. Values are kept inside deliberate bounds.
    private static int ParseBoundedInt(
        string? value,
        int fallback,
        int min,
        int max)
    {
        var parsed = ParseInt(value, fallback);
        return Math.Clamp(parsed, min, max);
    }

    private sealed record TerminalConfig(
        bool Enabled,
        string Protocol,
        string Vendor,
        string Model,
        string IpAddress,
        int Port,
        int ConnectTimeoutSeconds,
        int CommandTimeoutSeconds,
        bool RegisterBeforePayment)
    {
        public string Endpoint => $"{IpAddress}:{Port}";
    }
}
