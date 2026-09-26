using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TorPos.Core;

/// <summary>
/// The boundary between TOR POS and the separate product TOR E-Rechnung.
/// TOR POS does not issue invoices (no XRechnung/ZUGFeRD/EN 16931 here); it
/// only hands over a request: which fiscally completed sale, which buyer, and
/// the amounts exactly as the till booked them. TOR E-Rechnung builds and
/// validates the invoice. The request never changes the sale, its receipt or
/// its TSE data; amounts are integer cents so nothing is re-rounded on the way.
/// </summary>
public sealed record InvoiceBuyer(
    string Name,
    string Street,
    string PostalCode,
    string City,
    string CountryCode = "DE",
    string VatId = "",
    string Email = "",
    string BuyerReference = "");

public sealed record InvoiceRequestLine(
    string Name,
    string Quantity,
    string Unit,
    long UnitPriceGrossCents,
    long LineTotalGrossCents,
    string VatRate);

public sealed record InvoiceRequestVatGroup(string VatRate, long NetCents, long TaxCents, long GrossCents);

public sealed record InvoiceTransactionReference(
    string KassenId,
    long SaleId,
    long ReceiptNumber,
    DateTimeOffset BookedAt,
    string TseSerialNumber,
    string TseTransactionNumber,
    bool TseOutage);

public sealed record InvoiceRequest(
    string Format,
    string RequestId,
    DateTimeOffset RequestedAt,
    InvoiceTransactionReference Transaction,
    InvoiceBuyer Buyer,
    string Currency,
    IReadOnlyList<InvoiceRequestLine> Lines,
    long DiscountCents,
    IReadOnlyList<InvoiceRequestVatGroup> Vat,
    long TotalGrossCents)
{
    public const string FormatV1 = "TOR-POS-INVOICE-REQUEST-1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static InvoiceRequest FromJson(string json) =>
        JsonSerializer.Deserialize<InvoiceRequest>(json, Json)
        ?? throw new InvalidOperationException("Rechnungsanforderung ist leer.");
}

public static class InvoiceRequestBuilder
{
    /// <summary>
    /// Builds the request for a booked sale. Refused for a sale that is not
    /// fiscally completed, for Storno/Retoure and for a buyer without the
    /// address an invoice needs (§ 14 Abs. 4 UStG).
    /// </summary>
    public static InvoiceRequest From(Sale sale, InvoiceBuyer buyer, string kassenId, DateTimeOffset requestedAt, string? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(buyer);

        if (ReceiptFiscalStates.Of(sale.TseOutage, sale.TseTransactionNumber, sale.TseSignature) == ReceiptFiscalState.NotCompleted)
            throw new InvalidOperationException("Rechnung nur für einen fiskal abgeschlossenen Verkauf.");
        if (!string.Equals(sale.TransactionType, "SALE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Für Storno oder Retoure wird keine Rechnung angefordert.");
        if (sale.Lines.Count == 0 || sale.TotalCents <= 0)
            throw new InvalidOperationException("Rechnung nur für einen Verkauf mit positivem Betrag.");
        if (string.IsNullOrWhiteSpace(kassenId))
            throw new InvalidOperationException("Kassen-ID fehlt.");

        var clean = Clean(buyer);
        var missing = new List<string>();
        if (clean.Name.Length == 0) missing.Add("Name");
        if (clean.Street.Length == 0) missing.Add("Straße");
        if (clean.PostalCode.Length == 0) missing.Add("PLZ");
        if (clean.City.Length == 0) missing.Add("Ort");
        if (clean.CountryCode.Length != 2 || !clean.CountryCode.All(char.IsAsciiLetterUpper)) missing.Add("Land (ISO)");
        if (clean.Email.Length > 0 && (!clean.Email.Contains('@') || clean.Email.Any(char.IsWhiteSpace))) missing.Add("E-Mail");
        if (missing.Count > 0)
            throw new ArgumentException("Rechnungsempfänger unvollständig: " + string.Join(", ", missing), nameof(buyer));

        var vat = VatSummaryCalculator.Compute(sale.Lines, sale.DiscountCents)
            .Select(g => new InvoiceRequestVatGroup(
                g.Rate.ToString("0.##", CultureInfo.InvariantCulture),
                g.GrossCents - g.TaxCents,
                g.TaxCents,
                g.GrossCents))
            .ToList();

        if (vat.Sum(x => x.GrossCents) != sale.TotalCents)
            throw new InvalidOperationException("USt-Aufteilung stimmt nicht mit dem Bon-Betrag überein.");

        return new InvoiceRequest(
            InvoiceRequest.FormatV1,
            string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("N") : requestId.Trim(),
            requestedAt,
            new InvoiceTransactionReference(
                kassenId.Trim(),
                sale.Id,
                sale.ReceiptNumber,
                sale.CreatedAt,
                sale.TseSerialNumber,
                sale.TseTransactionNumber,
                sale.TseOutage),
            clean,
            "EUR",
            sale.Lines.Select(l => new InvoiceRequestLine(
                string.IsNullOrWhiteSpace(l.VariantName) ? l.ProductName : $"{l.ProductName} ({l.VariantName})",
                l.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                l.Unit,
                l.UnitPriceCents,
                l.LineTotalCents,
                l.VatRate.ToString("0.##", CultureInfo.InvariantCulture))).ToList(),
            sale.DiscountCents,
            vat,
            sale.TotalCents);
    }

    private static InvoiceBuyer Clean(InvoiceBuyer buyer)
    {
        static string C(string? value, int max)
        {
            var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            return text.Length > max ? text[..max] : text;
        }

        return new InvoiceBuyer(
            C(buyer.Name, 200),
            C(buyer.Street, 200),
            C(buyer.PostalCode, 20),
            C(buyer.City, 100),
            C(buyer.CountryCode, 10).ToUpperInvariant(),
            C(buyer.VatId, 30).ToUpperInvariant(),
            C(buyer.Email, 254),
            C(buyer.BuyerReference, 100));
    }
}
