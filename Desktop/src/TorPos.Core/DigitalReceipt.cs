using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
/// (DigitalReceiptHtml below) - originally duplicated by hand between the
/// two, which let the digital receipt's copy compute VAT on the
/// undiscounted gross while the printed one already did this correctly.
/// </summary>
public static class VatSummaryCalculator
{
    public static IReadOnlyList<VatGroupSummary> Compute(IEnumerable<CartLine> lines, long discountCents)
    {
        var subtotal = lines.Sum(x => x.LineTotalCents);
        var targetGross = Math.Max(0L, subtotal - discountCents);

        var groups = lines
            .GroupBy(x => x.VatRate)
            .OrderBy(x => x.Key)
            .Select(g => new { Rate = g.Key, OriginalGross = g.Sum(x => x.LineTotalCents) })
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
/// R103: an optional paperless alternative to printing - served from a
/// small local web server running on the till itself, reachable only over
/// the shop's own WiFi/LAN (never the public internet). §6 KassenSichV's
/// Belegausgabepflicht (receipt issuance obligation) can be satisfied by an
/// electronic Beleg, not only a printed one, as long as the customer can
/// actually take a copy away - a QR code the customer scans right at the
/// register, pointing at this page, satisfies that.
/// </summary>
public interface IDigitalReceiptService : IAsyncDisposable
{
    bool IsRunning { get; }
    int Port { get; }

    /// <summary>
    /// R115: the LAN address the server is actually bound to, or null when it
    /// is not running. The QR URL must be built from this - the server no
    /// longer listens on every interface, so re-detecting the address
    /// separately could advertise a link nothing is listening on.
    /// </summary>
    string? BoundAddress { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();

    /// <summary>
    /// Registers a completed sale for digital pickup and returns a fresh,
    /// unguessable token - never the sale's own receipt number, so a
    /// customer's Bon can't be enumerated by guessing/incrementing a URL.
    /// </summary>
    Task<string> RegisterAsync(long saleId, CancellationToken ct = default);
}

public static class DigitalReceiptToken
{
    // 18 random bytes, base64url (no padding) - short enough for a compact
    // QR, long enough (144 bits) that guessing one is not a realistic risk.
    public static string New()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}

/// <summary>
/// Renders the same legally-relevant content as the printed receipt
/// (company data, lines, VAT summary, payment split, TSE fields) as a
/// small, mobile-readable HTML page - not a stripped-down summary, since
/// this page itself may be the customer's only copy of the Beleg.
/// </summary>
public static class DigitalReceiptHtml
{
    public static string Render(
        Sale sale,
        string companyName,
        string companyAddress,
        string taxNumber,
        string vatId,
        bool fiscalTestMode,
        string easSerial = "")
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"de\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>Bon {sale.ReceiptNumber:000000}</title>");
        sb.Append("""
            <style>
            body{font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;max-width:420px;margin:0 auto;padding:16px;color:#1a1a1a;background:#fff}
            h1{font-size:18px;margin:0 0 2px}
            .muted{color:#666;font-size:13px}
            table{width:100%;border-collapse:collapse;margin:14px 0;font-size:14px}
            td{padding:3px 0;vertical-align:top}
            .num{text-align:right;white-space:nowrap}
            hr{border:none;border-top:1px solid #ddd;margin:12px 0}
            .total{font-size:17px;font-weight:bold}
            .fine{font-size:11px;color:#888;margin-top:18px;line-height:1.5}
            .test{background:#fff3cd;color:#7a5b00;padding:8px 10px;border-radius:6px;font-size:13px;margin-bottom:12px}
            </style>
            """);
        sb.Append("</head><body>");

        if (fiscalTestMode)
            sb.Append("<div class=\"test\">TESTBON &middot; KEIN FISKALBELEG</div>");

        sb.Append($"<h1>{Html(companyName)}</h1>");
        if (!string.IsNullOrWhiteSpace(companyAddress))
            sb.Append($"<div class=\"muted\">{Html(companyAddress)}</div>");
        if (!string.IsNullOrWhiteSpace(taxNumber))
            sb.Append($"<div class=\"muted\">Steuernr. {Html(taxNumber)}</div>");
        if (!string.IsNullOrWhiteSpace(vatId))
            sb.Append($"<div class=\"muted\">USt-IdNr. {Html(vatId)}</div>");

        sb.Append("<hr>");
        sb.Append($"<div>Bon <strong>{sale.ReceiptNumber:000000}</strong></div>");
        sb.Append($"<div class=\"muted\">{sale.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}</div>");

        sb.Append("<table>");
        foreach (var line in sale.Lines)
        {
            var name = line.ProductName + (string.IsNullOrWhiteSpace(line.VariantName) ? "" : " · " + line.VariantName);
            sb.Append("<tr><td colspan=\"2\">").Append(Html(name)).Append("</td></tr>");
            sb.Append("<tr><td class=\"muted\">")
              .Append(line.Quantity.ToString("0.###", De)).Append(" × ").Append(Money(line.UnitPriceCents))
              .Append(" (").Append(line.VatRate.ToString("0.#", De)).Append("% MwSt.)</td>")
              .Append("<td class=\"num\">").Append(Money(line.LineTotalCents)).Append("</td></tr>");
        }
        sb.Append("</table>");

        sb.Append("<hr>");
        // The manual discount (if any) is prorated across VAT-rate groups
        // BEFORE computing VAT on each - never on the pre-discount gross,
        // which would overstate VAT by the discount's own share of it.
        // See VatSummaryCalculator's own doc comment for why this must be
        // shared code, not a second hand-written copy of the formula.
        var vatGroups = VatSummaryCalculator.Compute(sale.Lines, sale.DiscountCents)
            .OrderByDescending(x => x.Rate);
        sb.Append("<table>");
        foreach (var g in vatGroups)
        {
            var net = g.GrossCents - g.TaxCents;
            sb.Append("<tr><td>MwSt. ").Append(g.Rate.ToString("0.#", De))
              .Append("% (netto ").Append(Money(net)).Append(")</td><td class=\"num\">")
              .Append(Money(g.TaxCents)).Append("</td></tr>");
        }
        if (sale.DiscountCents > 0)
            sb.Append("<tr><td>Rabatt (bereits in der MwSt. oben berücksichtigt)</td><td class=\"num\">-").Append(Money(sale.DiscountCents)).Append("</td></tr>");
        sb.Append("<tr class=\"total\"><td>Gesamt</td><td class=\"num\">").Append(Money(sale.TotalCents)).Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("<div>");
        if (sale.CashPortionCents > 0)
            sb.Append("Bar: ").Append(Money(sale.CashPortionCents)).Append("<br>");
        if (sale.CardPortionCents > 0)
            sb.Append("Karte: ").Append(Money(sale.CardPortionCents)).Append("<br>");
        sb.Append("</div>");

        if (!fiscalTestMode)
        {
            // R122 (F5): the printed receipt has refused to print without the
            // mandatory fields since R63, while this page claimed §6
            // KassenSichV compliance and then quietly left out whatever was
            // blank. Both now ask the same function; this one still renders -
            // the customer already scanned the code - but says what is missing
            // instead of asserting compliance it cannot show.
            var missing = FiscalReceiptFields.Missing(
                companyName,
                companyAddress,
                easSerial,
                sale.TseOutage,
                sale.TseSerialNumber,
                sale.TseTransactionNumber,
                !string.IsNullOrWhiteSpace(sale.TseSignatureCounter),
                sale.TseSignature,
                hasProcessStart: true,               // sale.CreatedAt, shown above
                hasProcessEnd: sale.TseLogTime is not null);

            if (missing.Count > 0)
            {
                sb.Append("<div class=\"test\">UNVOLLSTÄNDIGER BELEG<br>Folgende Pflichtangaben fehlen: ")
                  .Append(Html(string.Join(", ", missing)))
                  .Append(".<br>Bitte an der Kasse einen Papierbeleg verlangen.</div>");
            }

            sb.Append("<div class=\"fine\">");
            if (missing.Count == 0)
                sb.Append("Elektronischer Beleg gem. §6 KassenSichV.<br>");
            if (!string.IsNullOrWhiteSpace(easSerial))
                sb.Append("eAS: ").Append(Html(easSerial)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(sale.TseSerialNumber))
                sb.Append("TSE-Seriennr.: ").Append(Html(sale.TseSerialNumber)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(sale.TseTransactionNumber))
                sb.Append("Transaktion: ").Append(Html(sale.TseTransactionNumber)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(sale.TseSignatureCounter))
                sb.Append("Signaturzähler: ").Append(Html(sale.TseSignatureCounter)).Append("<br>");
            if (!string.IsNullOrWhiteSpace(sale.TseSignature))
                sb.Append("Prüfwert: ").Append(Html(sale.TseSignature)).Append("<br>");
            // Vorgangsende is the TSE's log time and is printed only when the
            // TSE actually produced one (R121). R136: Vorgangsbeginn is the TSE
            // start log time, else the till's own start of the Vorgang.
            var processStart = sale.TseStartLogTime ?? sale.StartedAt ?? sale.CreatedAt;
            sb.Append("Vorgangsbeginn: ").Append(processStart.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")).Append("<br>");
            // R137: DSFinV-K 2.7.2 - the start of the first order transaction.
            if (sale.OrderStartedAt is { } orderStart)
                sb.Append("Bestellbeginn: ").Append(orderStart.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")).Append("<br>");
            if (sale.TseLogTime is not null)
                sb.Append("Vorgangsende: ").Append(sale.TseLogTime.Value.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss")).Append("<br>");
            if (sale.TseOutage)
                sb.Append("TSE-Ausfall zum Zeitpunkt des Verkaufs.<br>");
            sb.Append("</div>");
        }

        sb.Append("<div class=\"fine\">Diese Seite ist nur über das WLAN dieses Geschäfts erreichbar und wird nicht dauerhaft öffentlich gehostet.</div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static string Money(long cents) => (cents / 100m).ToString("C2", De);

    private static string Html(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
