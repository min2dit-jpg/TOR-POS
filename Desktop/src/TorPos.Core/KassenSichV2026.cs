namespace TorPos.Core;

/// <summary>
/// Executable KassenSichV 2026 checks for data TOR itself can verify.
///
/// These checks do NOT certify a TSE and do not replace physical acceptance.
/// They make the code refuse to describe an incomplete transaction/receipt as
/// compliant when mandatory § 2 / § 6 data are absent or internally
/// inconsistent.
/// </summary>
public static class KassenSichV2026
{
    public static IReadOnlyList<KassenSichVFinding> ValidateTransaction(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var result = new List<KassenSichVFinding>();

        Add(
            "KASSENSICHV_2_START",
            "§ 2 Vorgangsbeginn",
            sale.StartedAt is not null,
            sale.StartedAt is not null
                ? "Vorgangsbeginn gespeichert."
                : "Vorgangsbeginn fehlt.",
            result);

        Add(
            "KASSENSICHV_2_TYPE_DATA",
            "§ 2 Vorgangsart und Vorgangsdaten",
            sale.Lines.Count > 0 &&
            sale.Lines.All(x =>
                !string.IsNullOrWhiteSpace(x.ProductName) &&
                x.Quantity != 0),
            "Mindestens eine eindeutig bezeichnete Position mit Menge erforderlich.",
            result);

        var paymentSum =
            sale.EffectiveCashPortionCents +
            sale.EffectiveCardPortionCents;

        Add(
            "KASSENSICHV_2_PAYMENT",
            "§ 2 Zahlungsarten",
            paymentSum == sale.TotalCents,
            paymentSum == sale.TotalCents
                ? "BAR/UNBAR-Aufteilung entspricht dem Bonbetrag."
                : $"Zahlungsaufteilung {paymentSum} ct stimmt nicht mit Bonbetrag {sale.TotalCents} ct überein.",
            result);

        Add(
            "KASSENSICHV_2_REGISTER_SERIAL",
            "§ 2 Seriennummer Aufzeichnungssystem",
            !string.IsNullOrWhiteSpace(sale.TseClientId),
            "TSE-Client-ID/Kassen-Identität muss beim signierten Vorgang nachvollziehbar sein.",
            result);

        if (sale.TseOutage)
        {
            // A real outage may lack TSE-generated values. The outage itself
            // must remain explicit; invented transaction/signature values are
            // more dangerous than missing ones.
            Add(
                "KASSENSICHV_2_TSE_OUTAGE",
                "§ 2 TSE-Ausfall",
                string.IsNullOrWhiteSpace(sale.TseTransactionNumber) &&
                string.IsNullOrWhiteSpace(sale.TseSignatureCounter) &&
                string.IsNullOrWhiteSpace(sale.TseSignature),
                "Bei TSE-Ausfall dürfen keine erfundenen TSE-Werte gespeichert werden.",
                result);

            return result;
        }

        Add(
            "KASSENSICHV_2_TXN",
            "§ 2 Transaktionsnummer",
            !string.IsNullOrWhiteSpace(sale.TseTransactionNumber),
            "TSE-Transaktionsnummer fehlt.",
            result);

        Add(
            "KASSENSICHV_2_END",
            "§ 2 Vorgangsende",
            sale.TseLogTime is not null,
            "TSE-Vorgangsende fehlt.",
            result);

        Add(
            "KASSENSICHV_2_TSE_SERIAL",
            "§ 2 TSE-Seriennummer",
            !string.IsNullOrWhiteSpace(sale.TseSerialNumber),
            "TSE-Seriennummer fehlt.",
            result);

        Add(
            "KASSENSICHV_2_SIGNATURE_COUNTER",
            "§ 2 Signaturzähler",
            long.TryParse(
                sale.TseSignatureCounter,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var counter) &&
            counter > 0,
            "Signaturzähler fehlt oder ist nicht positiv.",
            result);

        Add(
            "KASSENSICHV_2_VERIFICATION",
            "§ 2 Prüfwert",
            !string.IsNullOrWhiteSpace(sale.TseSignature),
            "Prüfwert/Signatur fehlt.",
            result);

        return result;
    }

    public static IReadOnlyList<KassenSichVFinding> ValidateReceipt(
        ReceiptPrintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var result = new List<KassenSichVFinding>();

        Add(
            "KASSENSICHV_6_COMPANY",
            "§ 6 Unternehmername und Anschrift",
            !string.IsNullOrWhiteSpace(job.CompanyName) &&
            !string.IsNullOrWhiteSpace(job.CompanyAddress),
            "Firma und vollständige Anschrift müssen auf dem Beleg stehen.",
            result);

        Add(
            "KASSENSICHV_6_ITEMS",
            "§ 6 Menge und Art der Leistung",
            job.Lines.Count > 0 &&
            job.Lines.All(x =>
                !string.IsNullOrWhiteSpace(x.ProductName) &&
                x.Quantity != 0),
            "Beleg braucht Menge und Art jeder Position.",
            result);

        var taxSummary = VatSummaryCalculator.Compute(
            job.Lines,
            job.DiscountCents);

        Add(
            "KASSENSICHV_6_TAX_TOTAL",
            "§ 6 Entgelt und Steuer",
            taxSummary.Sum(x => x.GrossCents) == job.TotalCents,
            "MwSt.-Gruppen müssen exakt auf den Beleg-Gesamtbetrag aufgehen.",
            result);

        var unsupported = new List<decimal>();
        foreach (var rate in taxSummary.Select(x => x.Rate).Distinct())
        {
            try
            {
                _ = FiscalProcessData.TaxContainer(rate);
            }
            catch (UnsupportedVatRateException)
            {
                unsupported.Add(rate);
            }
        }

        Add(
            "KASSENSICHV_6_VAT",
            "§ 6 Steuersatz",
            unsupported.Count == 0,
            unsupported.Count == 0
                ? "Alle Steuersätze sind einem zulässigen TSE-Steuercontainer zugeordnet."
                : "Nicht unterstützte Steuersätze: " +
                  string.Join(", ", unsupported.Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            result);

        if (!job.FiscalTestMode)
        {
            var missing = FiscalReceiptFields.Missing(
                job.CompanyName,
                job.CompanyAddress,
                job.EasSerial,
                job.TseOutage,
                job.TseSerial,
                job.TseTransactionNumber,
                job.SignatureCounter > 0,
                job.VerificationValue,
                job.ProcessStart is not null,
                job.ProcessEnd is not null);

            Add(
                "KASSENSICHV_6_FISCAL_FIELDS",
                "§ 6 TSE-Belegfelder",
                missing.Count == 0,
                missing.Count == 0
                    ? "Pflichtfelder vollständig."
                    : "Fehlende Belegfelder: " + string.Join(", ", missing),
                result);
        }

        return result;
    }

    public static bool IsReady(
        IEnumerable<KassenSichVFinding> findings) =>
        findings.All(x => x.Ready);

    private static void Add(
        string code,
        string requirement,
        bool ready,
        string detail,
        ICollection<KassenSichVFinding> target) =>
        target.Add(new KassenSichVFinding(
            code,
            requirement,
            ready,
            detail));
}

public sealed record KassenSichVFinding(
    string Code,
    string Requirement,
    bool Ready,
    string Detail);
