using System.Globalization;

namespace TorPos.Core;

public readonly record struct VatGroupSummary(decimal Rate, long GrossCents, long TaxCents);

/// <summary>
/// The one correct way to compute a per-VAT-rate breakdown when a
/// whole-Bon manual discount (CartLine-level Angebot discounts are
/// already baked into UnitPriceCents/LineTotalCents, so only the
/// separate manual DiscountCents needs prorating here) applies: the
/// discount is spread proportionally across each VAT-rate group's own
/// gross total BEFORE computing VAT on it, never computed on the
/// pre-discount gross with the discount just shown as an unrelated extra
/// line - that would overstate VAT by exactly the discount's share of it.
/// The last group absorbs any rounding remainder so the groups always
/// sum to EXACTLY (subtotal - discountCents), never drifting a cent from
/// rounding each group independently. Shared by the printed receipt
/// (StarMcPrint3PrinterService.BuildTaxSummary) and the digital receipt
/// (DigitalReceiptDocument below) - originally duplicated by hand between the
/// two, which let the digital receipt's copy compute VAT on the
/// undiscounted gross while the printed one already did this correctly.
/// </summary>
public static class VatSummaryCalculator
{
    public static IReadOnlyList<VatGroupSummary> Compute(IEnumerable<CartLine> lines, long discountCents)
    {
        var subtotal = lines.Sum(x => x.LineTotalCents);
        // R149: returned deposit can make a receipt negative (ReceiptTotals).
        var targetGross = ReceiptTotals.Total(subtotal, discountCents);

        var groups = lines
            .SelectMany(MenuVatPolicy.LineAllocations)
            .GroupBy(x => x.VatRate)
            .OrderBy(x => x.Key)
            .Select(g => new { Rate = g.Key, OriginalGross = g.Sum(x => x.GrossCents) })
            .ToArray();

        if (groups.Length == 0)
            return Array.Empty<VatGroupSummary>();

        var factor = subtotal <= 0 ? 1m : targetGross / (decimal)subtotal;
        var result = new List<VatGroupSummary>(groups.Length);
        long assignedGross = 0;

        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var gross = i == groups.Length - 1
                ? targetGross - assignedGross
                : (long)Math.Round(group.OriginalGross * factor, MidpointRounding.AwayFromZero);

            assignedGross += gross;
            var divisor = 1m + group.Rate / 100m;
            var net = divisor <= 0m
                ? gross
                : (long)Math.Round(gross / divisor, MidpointRounding.AwayFromZero);

            result.Add(new VatGroupSummary(group.Rate, gross, gross - net));
        }

        return result;
    }
}

/// <summary>
/// R106: prorates a whole-Bon manual discount onto an arbitrary subset of
/// an original sale's amount (e.g. the lines being partially returned) -
/// the SAME underlying math as VatSummaryCalculator's per-VAT-group
/// proration, just applied to one scalar amount instead of several
/// groups. Deliberately a single shared helper: both
/// Infrastructure.RecordReturnAsync (the authoritative DB-side
/// computation) and MainWindow.OnPartialReturnClick (which must know the
/// SAME discounted amount before the card portion of it, so it charges
/// the terminal correctly) need this identical number - two independent
/// hand-written copies is exactly the mistake that let the R103 digital
/// receipt's VAT calculation silently diverge from the printed receipt's.
/// </summary>
public static class DiscountProration
{
    public static long Prorate(long rawSubsetAmountCents, long originalSubtotalCents, long originalTotalCents)
    {
        if (originalSubtotalCents <= 0) return rawSubsetAmountCents;
        var factor = originalTotalCents / (decimal)originalSubtotalCents;
        return (long)Math.Round(rawSubsetAmountCents * factor, MidpointRounding.AwayFromZero);
    }
}


/// <summary>
/// R179: allocates one partial return from the cumulative totals of all earlier
/// returns. Prorating every fragment independently can lose or create cents;
/// allocating F(previous + current) - F(previous) makes the fragments telescope
/// exactly to the original discounted payment and to its cash/card split.
/// </summary>
public sealed record CumulativeReturnAllocation(
    long RawCents,
    long DiscountCents,
    long TotalCents,
    long CashCents,
    long CardCents);

public static class CumulativeReturnProration
{
    public static CumulativeReturnAllocation Allocate(
        long previousRawCents,
        long currentRawCents,
        long previousTotalCents,
        long previousCashCents,
        long originalSubtotalCents,
        long originalTotalCents,
        long originalCashCents)
    {
        if (previousRawCents < 0 || currentRawCents < 0 ||
            previousTotalCents < 0 || previousCashCents < 0)
            throw new ArgumentOutOfRangeException(nameof(currentRawCents));
        if (originalSubtotalCents < 0 || originalTotalCents < 0 || originalCashCents < 0 ||
            originalTotalCents > originalSubtotalCents || originalCashCents > originalTotalCents)
            throw new ArgumentOutOfRangeException(nameof(originalTotalCents));

        var cumulativeRaw = checked(previousRawCents + currentRawCents);
        if (cumulativeRaw > originalSubtotalCents)
            throw new InvalidOperationException("Retoure überschreitet die ursprüngliche Zwischensumme.");

        var cumulativeTotal = DiscountProration.Prorate(
            cumulativeRaw,
            originalSubtotalCents,
            originalTotalCents);
        var currentTotal = cumulativeTotal - previousTotalCents;

        var cumulativeCash = originalTotalCents > 0
            ? (long)Math.Round(
                (decimal)cumulativeTotal * originalCashCents / originalTotalCents,
                MidpointRounding.AwayFromZero)
            : 0L;
        var currentCash = cumulativeCash - previousCashCents;
        var currentCard = currentTotal - currentCash;
        var currentDiscount = currentRawCents - currentTotal;

        if (currentTotal < 0 || currentDiscount < 0 || currentCash < 0 || currentCard < 0 ||
            cumulativeTotal > originalTotalCents || cumulativeCash > originalCashCents)
            throw new InvalidOperationException("Kumulative Retourenverteilung ist inkonsistent.");

        return new CumulativeReturnAllocation(
            currentRawCents,
            currentDiscount,
            currentTotal,
            currentCash,
            currentCard);
    }
}

/// <summary>
/// R145: the customer's digital receipt (TOR Digital Receipt Cloud).
///
/// AEAO zu § 146a: the receipt may be made available electronically once the
/// transaction is finished (Nr. 2.5.2), with the customer's consent, which needs
/// no particular form (Nr. 2.5.3), in a standardised data format that free
/// standard software can show - a QR code on a screen leading to it is expressly
/// allowed (Nr. 2.5.6) - and right after the end of the Vorgang (Nr. 2.5.7).
/// Showing it only on the till's own screen is not enough (Nr. 2.5.4).
///
/// The document is built from the very <see cref="ReceiptPrintJob"/> the paper
/// receipt is printed from, so paper and digital receipt cannot say different
/// things. It holds only what the receipt shows - no operator, no customer. TOR
/// Cloud renders page and PDF from it and deletes both when the link expires;
/// the fiscal records stay on the till. Until R145 the digital receipt was a
/// page served by the till itself, reachable only inside the shop's WiFi.
/// </summary>
public sealed record DigitalReceiptLine(string Name, string Quantity, long UnitPriceCents, long LineTotalCents, string VatRate, string Note);

public sealed record DigitalReceiptVatGroup(string Rate, long NetCents, long TaxCents, long GrossCents);

public sealed record DigitalReceiptPayment(string Label, long AmountCents);

public sealed record DigitalReceiptField(string Label, string Value);

public sealed record DigitalReceiptDocument(
    bool TestReceipt,
    string BusinessName,
    string BusinessAddress,
    string TaxNumber,
    string VatId,
    string ReceiptNumber,
    string IssuedAt,
    long PickupNumber,
    IReadOnlyList<DigitalReceiptLine> Lines,
    long SubtotalCents,
    long DiscountCents,
    long TotalCents,
    IReadOnlyList<DigitalReceiptVatGroup> Vat,
    IReadOnlyList<DigitalReceiptPayment> Payments,
    IReadOnlyList<DigitalReceiptField> Tse,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> MissingFields)
{
    /// <summary>The document format TOR Cloud accepts (Cloud/receipts.js).</summary>
    public const string Format = "TOR-DIGITALBON-1";

    public const string SerialLabel = "Seriennummer Kasse";
    public const string TseSerialLabel = "Seriennummer TSE";
    public const string TransactionLabel = "Transaktionsnummer";
    public const string SignatureCounterLabel = "Signaturzähler";
    public const string ProcessStartLabel = "Vorgangsbeginn";
    public const string ProcessEndLabel = "Vorgangsende";
    public const string OrderStartLabel = "Bestellbeginn";
    public const string VerificationValueLabel = "Prüfwert";
    public const string CompleteNote = "Elektronischer Beleg gem. § 6 KassenSichV.";
    public const string OutageNote = "TSE-Ausfall: Vorgang ohne TSE-Signatur. Der Ausfall ist im System protokolliert.";

    private const int MaxFooterNotes = 3;
    private const int MaxNoteLength = 300;

    public string Field(string label) => Tse.FirstOrDefault(x => x.Label == label)?.Value ?? "";

    public static DigitalReceiptDocument From(ReceiptPrintJob job, IReadOnlyList<DigitalReceiptPayment> payments)
    {
        var lines = job.Lines
            .Select(line => new DigitalReceiptLine(
                line.ProductName + (string.IsNullOrWhiteSpace(line.VariantName) ? "" : " · " + line.VariantName),
                line.IsWeighted
                    ? WeightedSales.QuantityLabel(line.Quantity)
                    : GermanFormat.Number(line.Quantity, "0.###"),
                line.UnitPriceCents,
                line.LineTotalCents,
                line.VatAllocations.Length > 1
                    ? "gemischt"
                    : GermanFormat.Number(
                        line.VatAllocations.Length == 1 ? line.VatAllocations[0].VatRate : line.VatRate,
                        "0.##"),
                (line.IsWeighted ? "Preis pro kg" : "") +
                (line.HasPromotion
                    ? (line.IsWeighted ? " · " : "") + $"Angebot: {line.PromotionName} -{line.PromotionPercent} %"
                    : "")))
            .ToArray();

        // R106: VAT on what was actually paid, the discount spread over the rates.
        var vat = VatSummaryCalculator.Compute(job.Lines, job.DiscountCents)
            .OrderByDescending(x => x.Rate)
            .Select(x => new DigitalReceiptVatGroup(GermanFormat.Number(x.Rate, "0.##"), x.GrossCents - x.TaxCents, x.TaxCents, x.GrossCents))
            .ToArray();

        var tse = new List<DigitalReceiptField>();
        var notes = new List<string>();
        IReadOnlyList<string> missing = Array.Empty<string>();
        if (!job.FiscalTestMode)
        {
            // R122: the list the printer refuses to print without.
            missing = FiscalReceiptFields.Missing(job);

            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    tse.Add(new DigitalReceiptField(label, value.Trim()));
            }

            // R140/R144: AEAO zu § 146a Nr. 2.4.4 Nr. 6 - the serial number the TSE logged.
            Add(SerialLabel, job.EasSerial);
            if (!job.TseOutage)
            {
                Add(TseSerialLabel, job.TseSerial);
                Add(TransactionLabel, job.TseTransactionNumber);
                Add(SignatureCounterLabel, job.SignatureCounter > 0 ? job.SignatureCounter.ToString(CultureInfo.InvariantCulture) : "");
            }

            // R136/R140: TSE times unchanged, in UTC; only the till's own start
            // during an outage is local time - exactly as on the paper receipt.
            if (job.TseStartLogTime is { } tseStart)
                Add(ProcessStartLabel, TseReceiptTime.Format(tseStart));
            else if (job.ProcessStart is { } processStart)
                Add(ProcessStartLabel, processStart.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));
            if (job.ProcessEnd is { } processEnd)
                Add(ProcessEndLabel, TseReceiptTime.Format(processEnd));
            // R137: DSFinV-K 2.7.2 - the start of the first order transaction.
            if (job.OrderStart is { } orderStart)
                Add(OrderStartLabel, TseReceiptTime.Format(orderStart));
            if (!job.TseOutage)
                Add(VerificationValueLabel, job.VerificationValue);

            if (missing.Count == 0)
                notes.Add(CompleteNote);
            if (job.TseOutage)
                notes.Add(OutageNote);
        }

        foreach (var footer in (job.Footer ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(MaxFooterNotes))
            notes.Add(footer.Length > MaxNoteLength ? footer[..MaxNoteLength] : footer);

        return new DigitalReceiptDocument(
            job.FiscalTestMode,
            job.CompanyName.Trim(),
            job.CompanyAddress.Trim(),
            job.TaxNumber.Trim(),
            job.VatId.Trim(),
            job.ReceiptNumber > 0 ? job.ReceiptNumber.ToString("000000", CultureInfo.InvariantCulture) : "TEST",
            job.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            job.PickupNumber,
            lines,
            job.Lines.Sum(x => x.LineTotalCents),
            job.DiscountCents,
            job.TotalCents,
            vat,
            payments,
            tse,
            notes,
            missing);
    }

    /// <summary>The payment lines: cash and card with their amounts (R101 split for Mixed).</summary>
    public static IReadOnlyList<DigitalReceiptPayment> PaymentsFor(
        PaymentMethod method,
        long cashCents,
        long cardCents,
        string cashLabel = "Bar",
        string cardLabel = "Karte")
    {
        var payments = new List<DigitalReceiptPayment>();
        if (cashCents != 0) payments.Add(new DigitalReceiptPayment(cashLabel, cashCents));
        if (cardCents != 0) payments.Add(new DigitalReceiptPayment(cardLabel, cardCents));
        if (payments.Count == 0) payments.Add(new DigitalReceiptPayment(method == PaymentMethod.Card ? cardLabel : cashLabel, 0));
        return payments;
    }

    /// <summary>
    /// The upload reference. The same receipt always gives the same one, so a
    /// retry after a lost answer cannot publish a second receipt.
    /// </summary>
    public static string ReferenceFor(ReceiptPrintJob job) =>
        $"bon-{job.ReceiptNumber.ToString(CultureInfo.InvariantCulture)}-{job.CreatedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What is sent to TOR Cloud: the receipt as shown, nothing else.</summary>
    public object ToCloudPayload() => new
    {
        format = Format,
        test_receipt = TestReceipt,
        business = new { name = BusinessName, address = BusinessAddress, tax_number = TaxNumber, vat_id = VatId },
        receipt_number = ReceiptNumber,
        issued_at = IssuedAt,
        pickup_number = PickupNumber > 0 ? PickupNumber : (long?)null,
        lines = Lines.Select(x => new { name = x.Name, quantity = x.Quantity, unit_price_cents = x.UnitPriceCents, line_total_cents = x.LineTotalCents, vat_rate = x.VatRate, note = x.Note }).ToArray(),
        subtotal_cents = SubtotalCents,
        discount_cents = DiscountCents,
        total_cents = TotalCents,
        vat = Vat.Select(x => new { rate = x.Rate, net_cents = x.NetCents, tax_cents = x.TaxCents, gross_cents = x.GrossCents }).ToArray(),
        payments = Payments.Select(x => new { label = x.Label, amount_cents = x.AmountCents }).ToArray(),
        tse = Tse.Select(x => new { label = x.Label, value = x.Value }).ToArray(),
        notes = Notes.ToArray()
    };
}

/// <summary>R145: the link TOR Cloud returned for the QR code.</summary>
public sealed record DigitalReceiptPublication(string ReceiptId, string Url, DateTimeOffset ExpiresAt);

/// <summary>R145: publishes the customer's digital receipt to TOR Cloud.</summary>
public interface IDigitalReceiptPublisher
{
    /// <summary>Offered only when enabled in the settings and TOR Cloud is set up and active on this till.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Publishes the receipt and returns the link for the QR code. Throws when
    /// mandatory fields are missing (the printer's rule) or TOR Cloud does not
    /// confirm in time; the till then issues the paper receipt.
    /// </summary>
    Task<DigitalReceiptPublication> PublishAsync(DigitalReceiptDocument document, string reference, CancellationToken ct = default);
}

public static class DigitalReceiptLink
{
    private static readonly System.Text.RegularExpressions.Regex ReceiptPath =
        new("^/r/[A-Za-z0-9_-]{43}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A link the till may show as QR code: HTTPS (HTTP only on this PC, for
    /// tests), a receipt path with its token, no credentials, query or fragment.
    /// </summary>
    public static bool IsAcceptable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        ReceiptPath.IsMatch(uri.AbsolutePath);
}
