using System.Globalization;

namespace TorPos.Core;

/// <summary>
/// Builds the TSE "processType"/"processData" of a Vorgang exactly as DSFinV-K
/// 2.4 Anhang I prescribes (BZSt, 15.12.2023; unchanged since 2.3).
///
/// R130: until R130 this class produced a format of its own invention
/// ("Beleg^timestamp^Betrag-Summe:...^UStNormal:...^Beleg-Nr:...") and signed a
/// Storno as "AVBelegstorno". Neither matched Anhang I, which is binding for the
/// data handed to the TSE, and Anhang B/I explicitly rule out AVBelegstorno for
/// a system secured by a TSE. Every test that pinned the old format was
/// rewritten against the official examples (see R130ReviewTests).
///
/// Still to be confirmed against a real certified TSE before the fiscal
/// circuit breakers (FiscalRelease, FiscalComplianceService) may change.
/// </summary>
public static class FiscalProcessData
{
    public const string KassenbelegProcessType = "Kassenbeleg-V1";
    public const string BestellungProcessType = "Bestellung-V1";

    /// <summary>
    /// Anhang I: "processType und processData für die StartTransaction-Operation
    /// [sind] immer leer". The content is only handed over at FinishTransaction.
    /// </summary>
    public const string StartProcessType = "";
    public static byte[] StartProcessData => Array.Empty<byte>();

    /// <summary>
    /// Kassenbeleg-V1: <c>&lt;Vorgangstyp&gt;^&lt;Brutto-Steuerumsätze&gt;^&lt;Zahlungen&gt;</c>.
    /// </summary>
    public static byte[] BuildKassenbeleg(Sale sale) =>
        System.Text.Encoding.UTF8.GetBytes(KassenbelegText(sale));

    public static string KassenbelegText(Sale sale)
    {
        ArgumentNullException.ThrowIfNull(sale);

        // TOR stores a Storno/Retoure with positive amounts and tells them apart
        // by transaction_type; the reports subtract them. On the Beleg and in
        // the TSE they are a normal "Beleg" with the signs reversed (DSFinV-K
        // 4.2.2 and 4.2.5). The link to the original receipt is not part of
        // processData - DSFinV-K carries it in Bon_Referenzen.
        var sign = IsReversal(sale) ? -1 : 1;

        // Five tax containers in the fixed order of Anhang I. The gross per
        // rate comes from the shared discount-prorating calculator (R108), so
        // the containers add up to the amount actually paid.
        var containers = new long[5];
        foreach (var group in VatSummaryCalculator.Compute(sale.Lines, sale.DiscountCents))
            containers[TaxContainer(group.Rate)] += group.GrossCents;

        var gross = string.Join("_", containers.Select(c => Amount(sign * c)));

        // Only "Bar" and "Unbar", cash first, zero payments left out. R101's
        // Effective* portions also cover sales from before the split columns.
        // A training sale lists its (training) payments too - Anhang B allows
        // payment types for AVTraining.
        var payments = new List<string>(2);
        if (sale.EffectiveCashPortionCents != 0)
            payments.Add($"{Amount(sign * sale.EffectiveCashPortionCents)}:Bar");
        if (sale.EffectiveCardPortionCents != 0)
            payments.Add($"{Amount(sign * sale.EffectiveCardPortionCents)}:Unbar");

        return $"{Vorgangstyp(sale)}^{gross}^{string.Join("_", payments)}";
    }

    /// <summary>
    /// R136: an aborted Vorgang. Anhang I example "Vorgang wird abgebrochen":
    /// <c>AVBelegabbruch^0.00_0.00_0.00_0.00_0.00^</c> - no turnover, no payment.
    /// </summary>
    public const string BelegabbruchText = "AVBelegabbruch^0.00_0.00_0.00_0.00_0.00^";

    /// <summary>R135: the transaction type of a training sale.</summary>
    public const string TrainingTransactionType = "TRAINING";
    public const string VorgangstypTraining = "AVTraining";

    public static string Vorgangstyp(Sale sale) =>
        sale.TransactionType == TrainingTransactionType ? VorgangstypTraining : VorgangstypBeleg;

    /// <summary>
    /// R134: Einlage / Entnahme as Kassenbeleg-V1. They carry no VAT, so the
    /// amount goes into the 0 % container, and they move cash. These are the
    /// Anhang I examples "Privateinlage 100 bar" (Beleg^0.00_0.00_0.00_0.00_100.00^100.00:Bar)
    /// and "Privatentnahme 100" / "Einzahlung ... auf das Geschäftsbankkonto"
    /// (Beleg^0.00_0.00_0.00_0.00_-100.00^-100.00:Bar).
    /// </summary>
    public static byte[] BuildCashMovement(CashMovement movement) =>
        System.Text.Encoding.UTF8.GetBytes(CashMovementText(movement));

    public static string CashMovementText(CashMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        if (movement.Kind is not (CashMovementKind.Einlage or CashMovementKind.Entnahme))
            throw new InvalidOperationException("Nur Einlagen und Entnahmen werden als Kassenbeleg abgesichert.");

        var amount = Amount(movement.SignedCents);
        return $"{VorgangstypBeleg}^0.00_0.00_0.00_0.00_{amount}^{amount}:Bar";
    }

    /// <summary>
    /// Bestellung-V1: one line per position, <c>&lt;Menge&gt;;"&lt;Bezeichnung&gt;";&lt;Preis&gt;</c>,
    /// lines separated by CR (U+000D). The price is the gross unit price.
    /// </summary>
    public static byte[] BuildBestellung(ParkedReceipt order) =>
        System.Text.Encoding.UTF8.GetBytes(BestellungText(order));

    public static string BestellungText(ParkedReceipt order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return string.Join("\r", order.Lines.Select(line =>
            $"{Quantity(line.Quantity)};\"{LineText(line).Replace("\"", "\"\"")}\";{Amount(line.UnitPriceCents)}"));
    }

    /// <summary>The Vorgangstyp every TOR sale, Storno and Retoure is recorded under.</summary>
    public const string VorgangstypBeleg = "Beleg";

    /// <summary>
    /// The article text of a line, the same way the printed and the digital
    /// receipt show it. Shared so the TSE data and the DSFinV-K export name a
    /// position identically.
    /// </summary>
    public static string LineText(CartLine line) =>
        string.IsNullOrWhiteSpace(line.VariantName)
            ? line.ProductName
            : $"{line.ProductName} · {line.VariantName}";

    public static bool IsReversal(Sale sale) =>
        sale.TransactionType is "STORNO" or "RETURN";

    /// <summary>
    /// Index of the Anhang I tax container: 0 allgemeiner Steuersatz, 1
    /// ermäßigter Steuersatz, 2 and 3 the § 24 UStG averages (not used by
    /// TOR), 4 the 0 % container. TOR only offers 19 % and 7 % (plus 0 %);
    /// any other rate cannot be placed without knowing its legal basis, so it
    /// is refused rather than guessed.
    /// </summary>
    public static int TaxContainer(decimal rate) => rate switch
    {
        19m => 0,
        7m => 1,
        0m => 4,
        _ => throw new UnsupportedVatRateException(rate)
    };

    /// <summary>
    /// Amounts: "." as decimal separator, exactly two decimals, leading "-" for
    /// negative values, no "+", no thousands separator.
    /// </summary>
    public static string Amount(long cents) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quantity with as few decimals as possible ("1", "0.5", "0.451"),
    /// at most three, cut off rather than rounded.
    /// </summary>
    public static string Quantity(decimal quantity) =>
        (Math.Truncate(quantity * 1000m) / 1000m).ToString("0.###", CultureInfo.InvariantCulture);
}

public sealed class UnsupportedVatRateException : InvalidOperationException
{
    public UnsupportedVatRateException(decimal rate)
        : base($"MwSt-Satz {rate.ToString(CultureInfo.InvariantCulture)} % ist keinem Steuersatz nach DSFinV-K Anhang I zugeordnet (erlaubt: 19 %, 7 %, 0 %).")
    {
        Rate = rate;
    }

    public decimal Rate { get; }
}
