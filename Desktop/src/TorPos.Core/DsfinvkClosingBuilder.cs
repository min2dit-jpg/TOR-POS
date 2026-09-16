using System.Globalization;

namespace TorPos.Core;

/// <summary>Stammdaten of the business and the till, as DSFinV-K 3.2 lists them.</summary>
public sealed record DsfinvkMasterData(
    string KasseId,
    string CompanyName,
    string Street,
    string Zip,
    string City,
    string Country,
    string TaxNumber,
    string VatId,
    string KasseBrand,
    string KasseModel,
    string KasseSerial,
    string SoftwareBrand,
    string SoftwareVersion,
    string BaseCurrency = "EUR");

/// <summary>A Kassenabschluss (TOR: one Z-Bericht in z_report_archive).</summary>
public sealed record DsfinvkClosing(long ZNumber, DateTimeOffset CreatedAt);

public sealed record DsfinvkCashMovement(
    long Id,
    DateTimeOffset CreatedAt,
    CashMovementKind Kind,
    long AmountCents,
    string Reason,
    string Actor);

/// <summary>Where the receipt a Storno or Retoure refers to was closed.</summary>
public sealed record DsfinvkOriginalReference(long ZNumber, DateTimeOffset ZCreatedAt, string BonId);

public sealed record DsfinvkProductInfo(string ArticleNumber, string Unit);

public sealed class DsfinvkClosingInput
{
    public required DsfinvkClosing Closing { get; init; }
    public required DsfinvkMasterData Master { get; init; }
    public IReadOnlyList<Sale> Sales { get; init; } = Array.Empty<Sale>();
    public IReadOnlyList<DsfinvkCashMovement> CashMovements { get; init; } = Array.Empty<DsfinvkCashMovement>();

    /// <summary>Only orders that went through TSE signing (signed or recorded as outage).</summary>
    public IReadOnlyList<ParkedReceipt> Orders { get; init; } = Array.Empty<ParkedReceipt>();

    public Func<long, DsfinvkOriginalReference?> OriginalOf { get; init; } = _ => null;

    /// <summary>The documented outage reason (tse_outage_log) for a Vorgang at this time, "" if none.</summary>
    public Func<DateTimeOffset, string> OutageReasonAt { get; init; } = _ => "";

    /// <summary>Sale id → Abrechnungskreis of the order it was cashed from.</summary>
    public IReadOnlyDictionary<long, string> AllocationGroupBySaleId { get; init; } = new Dictionary<long, string>();

    public Func<long, DsfinvkProductInfo?> ProductOf { get; init; } = _ => null;
}

/// <summary>The records of one Kassenabschluss, by DSFinV-K table name.</summary>
public sealed class DsfinvkRows
{
    private readonly Dictionary<string, List<IReadOnlyDictionary<string, object?>>> _rows = new();

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> For(string table) =>
        _rows.TryGetValue(table, out var rows) ? rows : Array.Empty<IReadOnlyDictionary<string, object?>>();

    internal void Add(string table, Dictionary<string, object?> row)
    {
        if (!_rows.TryGetValue(table, out var rows))
            _rows[table] = rows = new List<IReadOnlyDictionary<string, object?>>();
        rows.Add(row);
    }
}

/// <summary>
/// R131: turns one Kassenabschluss of TOR into the DSFinV-K 2.4 records.
///
/// How TOR's data is represented (DSFinV-K references in brackets):
/// - every sale, Storno and Retoure is a <c>Beleg</c> (Anhang B). TOR stores a
///   Storno/Retoure with positive amounts; here the signs are reversed
///   (4.2.2, 4.2.5), a Storno carries BON_STORNO = 1 and both carry a
///   Bon_Referenzen record pointing at the original receipt;
/// - the header amounts per VAT rate are the printed ones ("Rechnungsdoppel",
///   3.1.2) from the same VatSummaryCalculator the receipt uses;
/// - Pfand is its own position with GV_TYP Pfand at the article's VAT rate
///   (Anhang C: a Warenumschließung shares the rate of the goods);
/// - an Angebot price is shown as the reduced price with base_amount and
///   discount in Bonpos_Preisfindung (4.2.4); a manual discount on the whole
///   receipt is a separate negative position GV_TYP Rabatt, split by VAT rate
///   in Bonpos_USt (4.2.4);
/// - Einlage/Entnahme are Belege with GV_TYP Einzahlung/Auszahlung, the
///   generic types for cash flows TOR cannot classify further (Anhang C);
/// - an IMBISS order that was handed to the TSE is an AVBestellung without
///   payment (Anhang B), linked to the receipt it was paid with through
///   Bonkopf_AbrKreis (2.7.1);
/// - only Belege are summed in the closing (4.1).
///
/// BON_ID: a sale uses its receipt number; a cash movement "KB-{id}" and an
/// order "BE-{Park-Nr}", both assigned once when they are recorded and never
/// changed.
/// </summary>
public static class DsfinvkClosingBuilder
{
    public const string TaxonomyVersion = "2.4";
    public const int VatKeyNotTaxable = 5;

    public static string SaleBonId(long receiptNumber) => receiptNumber.ToString(CultureInfo.InvariantCulture);
    public static string CashMovementBonId(long id) => $"KB-{id.ToString(CultureInfo.InvariantCulture)}";
    public static string OrderBonId(long parkNumber) => $"BE-{parkNumber.ToString(CultureInfo.InvariantCulture)}";
    public static string OrderAllocationGroup(ParkedReceipt order) => $"Bestellung {order.DisplayNumber}";

    /// <summary>
    /// DSFinV-K Anlage 2: ID 1 the general and ID 2 the reduced rate valid when
    /// the sale was recorded. TOR offers 19 % and 7 %; a 0 % sale cannot be
    /// placed without knowing whether it is non-taxable (5) or tax-exempt (6),
    /// so it is refused like any other unknown rate.
    /// </summary>
    public static int VatKey(decimal rate) => rate switch
    {
        19m => 1,
        7m => 2,
        _ => throw new UnsupportedVatRateException(rate)
    };

    public static DsfinvkRows Build(DsfinvkClosingInput input) => new Builder(input).Build();

    private sealed class Builder
    {
        private readonly DsfinvkClosingInput _input;
        private readonly DsfinvkRows _rows = new();
        // Plain dictionaries sorted ordinally on output: a SortedDictionary over
        // string tuples would order by the Windows culture and make the same
        // closing come out differently on a Turkish and a German PC.
        private readonly Dictionary<(string Type, string Name, int Key), (long Gross, long Net, long Tax)> _businessCases = new();
        private readonly Dictionary<(string Type, string Name), long> _payments = new();
        private readonly SortedSet<int> _vatKeys = new();
        private readonly Dictionary<string, long> _tseIds = new(StringComparer.Ordinal);
        private long _paymentTotal;
        private long _cashTotal;

        public Builder(DsfinvkClosingInput input) => _input = input;

        public DsfinvkRows Build()
        {
            var vorgaenge = new List<(DateTimeOffset At, int Order, string BonId, Action Write)>();
            foreach (var sale in _input.Sales)
                vorgaenge.Add((sale.CreatedAt, 0, DsfinvkClosingBuilder.SaleBonId(sale.ReceiptNumber), () => WriteSale(sale)));
            foreach (var movement in _input.CashMovements)
                vorgaenge.Add((movement.CreatedAt, 1, CashMovementBonId(movement.Id), () => WriteCashMovement(movement)));
            foreach (var order in _input.Orders)
                vorgaenge.Add((order.CreatedAt, 2, OrderBonId(order.ParkNumber), () => WriteOrder(order)));

            vorgaenge.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Order.CompareTo(b.Order));
            foreach (var vorgang in vorgaenge)
                vorgang.Write();

            WriteMasterData(vorgaenge.Count == 0 ? null : vorgaenge[0].BonId, vorgaenge.Count == 0 ? null : vorgaenge[^1].BonId);
            WriteClosingTotals();
            return _rows;
        }

        // ------------------------------------------------------------ Vorgänge

        private void WriteSale(Sale sale)
        {
            var sign = FiscalProcessData.IsReversal(sale) ? -1 : 1;
            var bonId = SaleBonId(sale.ReceiptNumber);

            Add("Bonkopf", new()
            {
                ["BON_ID"] = bonId,
                ["BON_NR"] = sale.ReceiptNumber,
                ["BON_TYP"] = FiscalProcessData.VorgangstypBeleg,
                ["BON_NAME"] = sale.TransactionType switch { "STORNO" => "Storno", "RETURN" => "Retoure", _ => "Verkauf" },
                ["BON_STORNO"] = sale.TransactionType == "STORNO" ? "1" : "0",
                ["BON_ENDE"] = DsfinvkCsv.Timestamp(sale.CreatedAt),
                ["BEDIENER_ID"] = DsfinvkCsv.Fit(sale.OperatorName, 50),
                ["BEDIENER_NAME"] = DsfinvkCsv.Fit(sale.OperatorName, 50),
                ["UMS_BRUTTO"] = new DsfinvkMoney(sign * sale.TotalCents),
                ["BON_NOTIZ"] = sale.PickupNumber > 0 ? $"Abholnummer {sale.PickupNumber:000}" : null,
            });

            WriteHeaderVat(bonId, sale.Lines, sale.DiscountCents, sign);
            Payment(bonId, "Bar", "Bar", sign * sale.EffectiveCashPortionCents, beleg: true);
            Payment(bonId, "Unbar", "Karte", sign * sale.EffectiveCardPortionCents, beleg: true);

            if (_input.AllocationGroupBySaleId.TryGetValue(sale.Id, out var group))
                Add("Bonkopf_AbrKreis", new() { ["BON_ID"] = bonId, ["ABRECHNUNGSKREIS"] = DsfinvkCsv.Fit(group, 50) });

            WritePositions(bonId, sale.Lines, sale.DiscountCents, sign, inHaus: null, beleg: true);

            if (FiscalProcessData.IsReversal(sale))
            {
                var original = sale.OriginalSaleId is long originalId ? _input.OriginalOf(originalId) : null;
                if (original is null)
                    throw new InvalidOperationException(
                        $"Beleg {sale.ReceiptNumber} ({sale.TransactionType}): der Ursprungsbeleg liegt in keinem Kassenabschluss.");

                Add("Bon_Referenzen", new()
                {
                    ["BON_ID"] = bonId,
                    ["REF_TYP"] = "Transaktion",
                    ["REF_DATUM"] = DsfinvkCsv.Timestamp(original.ZCreatedAt),
                    ["REF_Z_KASSE_ID"] = _input.Master.KasseId,
                    ["REF_Z_NR"] = original.ZNumber,
                    ["REF_BON_ID"] = original.BonId,
                });
            }

            WriteTse(
                bonId,
                sale.TseSerialNumber,
                sale.TseTransactionNumber,
                sale.TseSignatureCounter,
                sale.TseSignature,
                sale.TseLogTime,
                sale.TseOutage,
                FiscalProcessData.KassenbelegProcessType,
                () => FiscalProcessData.KassenbelegText(sale),
                sale.CreatedAt);
        }

        private void WriteCashMovement(DsfinvkCashMovement movement)
        {
            var bonId = CashMovementBonId(movement.Id);
            var inflow = movement.Kind == CashMovementKind.Einlage;
            var cents = inflow ? movement.AmountCents : -movement.AmountCents;
            var name = inflow ? "Einlage" : "Entnahme";

            Add("Bonkopf", new()
            {
                ["BON_ID"] = bonId,
                ["BON_NR"] = movement.Id,
                ["BON_TYP"] = FiscalProcessData.VorgangstypBeleg,
                ["BON_NAME"] = name,
                ["BON_STORNO"] = "0",
                ["BON_ENDE"] = DsfinvkCsv.Timestamp(movement.CreatedAt),
                ["BEDIENER_ID"] = DsfinvkCsv.Fit(movement.Actor, 50),
                ["BEDIENER_NAME"] = DsfinvkCsv.Fit(movement.Actor, 50),
                ["UMS_BRUTTO"] = new DsfinvkMoney(cents),
            });

            Add("Bonkopf_USt", new()
            {
                ["BON_ID"] = bonId,
                ["UST_SCHLUESSEL"] = (long)VatKeyNotTaxable,
                ["BON_BRUTTO"] = new DsfinvkMoney(cents),
                ["BON_NETTO"] = new DsfinvkMoney(cents),
                ["BON_UST"] = new DsfinvkMoney(0),
            });
            _vatKeys.Add(VatKeyNotTaxable);

            Payment(bonId, "Bar", "Bar", cents, beleg: true);

            var text = string.IsNullOrWhiteSpace(movement.Reason) ? name : movement.Reason;
            Position(bonId, 1, text, inflow ? "Einzahlung" : "Auszahlung", name, null, null, 1m, cents, inHaus: null);
            PositionVat(bonId, 1, VatKeyNotTaxable, 0m, cents, inflow ? "Einzahlung" : "Auszahlung", name, beleg: true);

            // TOR does not secure cash movements with the TSE yet (open
            // decision, see ROADMAP). The record says so instead of the
            // movement simply having no transaction.
            Add("TSE_Transaktionen", new()
            {
                ["BON_ID"] = bonId,
                ["TSE_TA_FEHLER"] = "Kassenbewegung ohne TSE-Absicherung erfasst",
            });
        }

        private void WriteOrder(ParkedReceipt order)
        {
            var bonId = OrderBonId(order.ParkNumber);

            Add("Bonkopf", new()
            {
                ["BON_ID"] = bonId,
                ["BON_NR"] = order.ParkNumber,
                ["BON_TYP"] = "AVBestellung",
                ["BON_NAME"] = "Bestellung",
                ["BON_STORNO"] = "0",
                ["BON_ENDE"] = DsfinvkCsv.Timestamp(order.CreatedAt),
                ["BEDIENER_ID"] = DsfinvkCsv.Fit(order.CreatedBy, 50),
                ["BEDIENER_NAME"] = DsfinvkCsv.Fit(order.CreatedBy, 50),
                ["UMS_BRUTTO"] = new DsfinvkMoney(order.TotalCents),
                ["BON_NOTIZ"] = order.PickupNumber > 0 ? $"Abholnummer {order.PickupNumber:000}" : null,
            });

            WriteHeaderVat(bonId, order.Lines, order.DiscountCents, 1);

            // Anhang B: every AV type except AVTraining only knows "Keine".
            Add("Bonkopf_Zahlarten", new()
            {
                ["BON_ID"] = bonId,
                ["ZAHLART_TYP"] = "Keine",
                ["ZAHLART_NAME"] = "Keine",
                ["BASISWAEH_BETRAG"] = new DsfinvkMoney(0),
            });

            Add("Bonkopf_AbrKreis", new() { ["BON_ID"] = bonId, ["ABRECHNUNGSKREIS"] = DsfinvkCsv.Fit(OrderAllocationGroup(order), 50) });

            WritePositions(bonId, order.Lines, order.DiscountCents, 1, inHaus: order.ImHaus ? "1" : "0", beleg: false);

            WriteTse(
                bonId,
                order.TseSerialNumber,
                order.TseTransactionNumber,
                order.TseSignatureCounter,
                order.TseSignature,
                order.TseLogTime,
                order.TseOutage,
                FiscalProcessData.BestellungProcessType,
                () => FiscalProcessData.BestellungText(order),
                order.CreatedAt);
        }

        // ----------------------------------------------------------- positions

        private void WriteHeaderVat(string bonId, IReadOnlyList<CartLine> lines, long discountCents, int sign)
        {
            foreach (var group in VatSummaryCalculator.Compute(lines, discountCents))
            {
                var key = VatKey(group.Rate);
                _vatKeys.Add(key);
                Add("Bonkopf_USt", new()
                {
                    ["BON_ID"] = bonId,
                    ["UST_SCHLUESSEL"] = (long)key,
                    ["BON_BRUTTO"] = new DsfinvkMoney(sign * group.GrossCents),
                    ["BON_NETTO"] = new DsfinvkMoney(sign * (group.GrossCents - group.TaxCents)),
                    ["BON_UST"] = new DsfinvkMoney(sign * group.TaxCents),
                });
            }
        }

        private void WritePositions(string bonId, IReadOnlyList<CartLine> lines, long discountCents, int sign, string? inHaus, bool beleg)
        {
            var row = 0;
            foreach (var line in lines)
            {
                var key = VatKey(line.VatRate);
                var text = FiscalProcessData.LineText(line);
                var product = _input.ProductOf(line.ProductId);
                var pfandTotal = line.PfandCents == 0
                    ? 0L
                    : (long)Math.Round(line.Quantity * line.PfandCents, MidpointRounding.AwayFromZero);
                var articleTotal = line.LineTotalCents - pfandTotal;

                row++;
                Position(bonId, row, text, "Umsatz", null, product, line.Barcode, sign * line.Quantity, line.UnitPriceCents - line.PfandCents, inHaus);
                PositionVat(bonId, row, key, line.VatRate, sign * articleTotal, "Umsatz", null, beleg);

                if (line.HasPromotion)
                {
                    var baseTotal = (long)Math.Round(
                        line.Quantity * (line.EffectiveListUnitPriceCents - line.PfandCents),
                        MidpointRounding.AwayFromZero);
                    Pricing(bonId, row, "base_amount", key, line.VatRate, sign * baseTotal);
                    Pricing(bonId, row, "discount", key, line.VatRate, sign * (articleTotal - baseTotal));
                }

                if (pfandTotal != 0)
                {
                    row++;
                    Position(bonId, row, "Pfand " + text, "Pfand", null, null, null, sign * line.Quantity, line.PfandCents, inHaus);
                    PositionVat(bonId, row, key, line.VatRate, sign * pfandTotal, "Pfand", null, beleg);
                }
            }

            if (discountCents <= 0)
                return;

            var discounted = VatSummaryCalculator.Compute(lines, discountCents).ToDictionary(g => g.Rate, g => g.GrossCents);
            var undiscounted = lines.GroupBy(l => l.VatRate).ToDictionary(g => g.Key, g => g.Sum(l => l.LineTotalCents));
            var appliedDiscount = undiscounted.Values.Sum() - discounted.Values.Sum();
            if (appliedDiscount == 0)
                return;

            row++;
            Position(bonId, row, "Rabatt", "Rabatt", null, null, null, sign * 1m, -appliedDiscount, inHaus);
            foreach (var (rate, gross) in undiscounted.OrderByDescending(x => x.Key))
            {
                var share = discounted[rate] - gross;
                if (share != 0)
                    PositionVat(bonId, row, VatKey(rate), rate, sign * share, "Rabatt", null, beleg);
            }
        }

        private void Position(
            string bonId, int row, string text, string gvType, string? gvName,
            DsfinvkProductInfo? product, string? gtin, decimal quantity, long unitCents, string? inHaus)
        {
            Add("Bonpos", new()
            {
                ["BON_ID"] = bonId,
                ["POS_ZEILE"] = row.ToString(CultureInfo.InvariantCulture),
                ["ARTIKELTEXT"] = DsfinvkCsv.Fit(text, 255),
                ["GV_TYP"] = gvType,
                ["GV_NAME"] = gvName,
                ["INHAUS"] = inHaus,
                ["P_STORNO"] = "0",
                ["AGENTUR_ID"] = 0L,
                ["ART_NR"] = string.IsNullOrWhiteSpace(product?.ArticleNumber) ? null : DsfinvkCsv.Fit(product.ArticleNumber, 50),
                ["GTIN"] = string.IsNullOrWhiteSpace(gtin) ? null : DsfinvkCsv.Fit(gtin, 50),
                ["MENGE"] = Math.Round(quantity, 3, MidpointRounding.AwayFromZero),
                ["FAKTOR"] = 1m,
                // Anhang E: an empty unit means "Stück".
                ["EINHEIT"] = string.IsNullOrWhiteSpace(product?.Unit) || product.Unit == "Stück" ? null : DsfinvkCsv.Fit(product.Unit, 50),
                ["STK_BR"] = new DsfinvkMoney(unitCents),
            });
        }

        private void PositionVat(string bonId, int row, int key, decimal rate, long grossCents, string gvType, string? gvName, bool beleg)
        {
            var (net, tax) = Split(grossCents, rate);
            _vatKeys.Add(key);
            Add("Bonpos_USt", new()
            {
                ["BON_ID"] = bonId,
                ["POS_ZEILE"] = row.ToString(CultureInfo.InvariantCulture),
                ["UST_SCHLUESSEL"] = (long)key,
                ["POS_BRUTTO"] = new DsfinvkMoney(grossCents),
                ["POS_NETTO"] = new DsfinvkMoney(net),
                ["POS_UST"] = new DsfinvkMoney(tax),
            });

            if (!beleg)
                return;

            var caseKey = (gvType, gvName ?? "", key);
            _businessCases.TryGetValue(caseKey, out var sum);
            _businessCases[caseKey] = (sum.Gross + grossCents, sum.Net + net, sum.Tax + tax);
        }

        private void Pricing(string bonId, int row, string type, int key, decimal rate, long grossCents)
        {
            var (net, tax) = Split(grossCents, rate);
            Add("Bonpos_Preisfindung", new()
            {
                ["BON_ID"] = bonId,
                ["POS_ZEILE"] = row.ToString(CultureInfo.InvariantCulture),
                ["TYP"] = type,
                ["UST_SCHLUESSEL"] = (long)key,
                ["PF_BRUTTO"] = new DsfinvkMoney(grossCents),
                ["PF_NETTO"] = new DsfinvkMoney(net),
                ["PF_UST"] = new DsfinvkMoney(tax),
            });
        }

        /// <summary>Same rounding as VatSummaryCalculator, symmetric for negative amounts.</summary>
        private static (long Net, long Tax) Split(long grossCents, decimal rate)
        {
            var sign = grossCents < 0 ? -1 : 1;
            var gross = Math.Abs(grossCents);
            var net = (long)Math.Round(gross / (1m + rate / 100m), MidpointRounding.AwayFromZero);
            return (sign * net, sign * (gross - net));
        }

        private void Payment(string bonId, string type, string name, long cents, bool beleg)
        {
            if (cents == 0)
                return;

            Add("Bonkopf_Zahlarten", new()
            {
                ["BON_ID"] = bonId,
                ["ZAHLART_TYP"] = type,
                ["ZAHLART_NAME"] = name,
                ["BASISWAEH_BETRAG"] = new DsfinvkMoney(cents),
            });

            if (!beleg)
                return;

            _payments.TryGetValue((type, name), out var sum);
            _payments[(type, name)] = sum + cents;
            _paymentTotal += cents;
            if (type == "Bar")
                _cashTotal += cents;
        }

        private void WriteTse(
            string bonId, string serial, string transactionNumber, string signatureCounter, string signature,
            DateTimeOffset? logTime, bool outage, string processType, Func<string> processData, DateTimeOffset at)
        {
            var row = new Dictionary<string, object?> { ["BON_ID"] = bonId };

            if (!outage && transactionNumber.Length > 0)
            {
                row["TSE_ID"] = TseId(serial);
                row["TSE_TANR"] = ParseCounter(transactionNumber, bonId, "Transaktionsnummer");
                row["TSE_TA_ENDE"] = logTime is { } finished ? DsfinvkCsv.TseTime(finished) : null;
                row["TSE_TA_VORGANGSART"] = processType;
                row["TSE_TA_SIGZ"] = ParseCounter(signatureCounter, bonId, "Signaturzähler");
                row["TSE_TA_SIG"] = signature;

                // Optional field. Left out rather than altered when it would not
                // survive as one CSV value (the CR between order lines) or does
                // not fit.
                var data = processData();
                if (data.Length <= 1000 && data.IndexOfAny(new[] { '\r', '\n' }) < 0)
                    row["TSE_VORGANGSDATEN"] = data;
            }
            else if (outage)
            {
                var reason = _input.OutageReasonAt(at);
                row["TSE_TA_FEHLER"] = DsfinvkCsv.Fit(reason.Length == 0 ? "TSE-Ausfall" : "TSE-Ausfall: " + reason, 200);
            }
            else
            {
                row["TSE_TA_FEHLER"] = "Kein TSE-Ergebnis gespeichert";
            }

            Add("TSE_Transaktionen", row);
        }

        private long TseId(string serial)
        {
            if (!_tseIds.TryGetValue(serial, out var id))
                _tseIds[serial] = id = _tseIds.Count + 1;
            return id;
        }

        private static long ParseCounter(string value, string bonId, string what) =>
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new InvalidOperationException($"Vorgang {bonId}: TSE-{what} '{value}' ist keine Zahl.");

        // ----------------------------------------------------- master & totals

        private void WriteMasterData(string? firstBonId, string? lastBonId)
        {
            var m = _input.Master;

            Add("Stamm_Abschluss", new()
            {
                ["TAXONOMIE_VERSION"] = TaxonomyVersion,
                ["Z_START_ID"] = firstBonId,
                ["Z_ENDE_ID"] = lastBonId,
                ["NAME"] = m.CompanyName,
                ["STRASSE"] = m.Street,
                ["PLZ"] = m.Zip,
                ["ORT"] = m.City,
                ["LAND"] = m.Country,
                ["STNR"] = Blank(m.TaxNumber),
                ["USTID"] = Blank(m.VatId),
                ["Z_SE_ZAHLUNGEN"] = new DsfinvkMoney(_paymentTotal),
                ["Z_SE_BARZAHLUNGEN"] = new DsfinvkMoney(_cashTotal),
            });

            Add("Stamm_Orte", new()
            {
                ["LOC_NAME"] = m.CompanyName,
                ["LOC_STRASSE"] = m.Street,
                ["LOC_PLZ"] = m.Zip,
                ["LOC_ORT"] = m.City,
                ["LOC_LAND"] = m.Country,
                ["LOC_USTID"] = Blank(m.VatId),
            });

            Add("Stamm_Kassen", new()
            {
                ["KASSE_BRAND"] = m.KasseBrand,
                ["KASSE_MODELL"] = m.KasseModel,
                ["KASSE_SERIENNR"] = m.KasseSerial,
                ["KASSE_SW_BRAND"] = m.SoftwareBrand,
                ["KASSE_SW_VERSION"] = m.SoftwareVersion,
                ["KASSE_BASISWAEH_CODE"] = m.BaseCurrency,
                ["KEINE_UST_ZUORDNUNG"] = "0",
            });

            foreach (var (serial, id) in _tseIds.OrderBy(x => x.Value))
            {
                Add("Stamm_TSE", new()
                {
                    ["TSE_ID"] = id,
                    ["TSE_SERIAL"] = serial,
                    ["TSE_PD_ENCODING"] = "UTF-8",
                });
            }

            foreach (var key in _vatKeys)
            {
                var (rate, description) = key switch
                {
                    1 => (19m, "Allgemeiner Steuersatz (§ 12 Abs. 1 UStG)"),
                    2 => (7m, "Ermäßigter Steuersatz (§ 12 Abs. 2 UStG)"),
                    VatKeyNotTaxable => (0m, "Nicht steuerbar"),
                    _ => throw new InvalidOperationException($"USt-Schlüssel {key} ohne Beschreibung.")
                };
                Add("Stamm_USt", new() { ["UST_SCHLUESSEL"] = (long)key, ["UST_SATZ"] = rate, ["UST_BESCHR"] = description });
            }
        }

        private void WriteClosingTotals()
        {
            foreach (var ((type, name, key), sum) in _businessCases
                         .OrderBy(x => x.Key.Type, StringComparer.Ordinal)
                         .ThenBy(x => x.Key.Name, StringComparer.Ordinal)
                         .ThenBy(x => x.Key.Key))
            {
                Add("Z_GV_Typ", new()
                {
                    ["GV_TYP"] = type,
                    ["GV_NAME"] = Blank(name),
                    ["AGENTUR_ID"] = 0L,
                    ["UST_SCHLUESSEL"] = (long)key,
                    ["Z_UMS_BRUTTO"] = new DsfinvkMoney(sum.Gross),
                    ["Z_UMS_NETTO"] = new DsfinvkMoney(sum.Net),
                    ["Z_UST"] = new DsfinvkMoney(sum.Tax),
                });
            }

            foreach (var ((type, name), cents) in _payments
                         .OrderBy(x => x.Key.Type, StringComparer.Ordinal)
                         .ThenBy(x => x.Key.Name, StringComparer.Ordinal))
                Add("Z_Zahlart", new() { ["ZAHLART_TYP"] = type, ["ZAHLART_NAME"] = name, ["Z_ZAHLART_BETRAG"] = new DsfinvkMoney(cents) });

            Add("Z_Waehrungen", new() { ["ZAHLART_WAEH"] = _input.Master.BaseCurrency, ["ZAHLART_BETRAG_WAEH"] = new DsfinvkMoney(_cashTotal) });
        }

        private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>Every record carries the three Z_ keys of its closing (Anhang E).</summary>
        private void Add(string table, Dictionary<string, object?> row)
        {
            row["Z_KASSE_ID"] = _input.Master.KasseId;
            row["Z_ERSTELLUNG"] = DsfinvkCsv.Timestamp(_input.Closing.CreatedAt);
            row["Z_NR"] = _input.Closing.ZNumber;
            _rows.Add(table, row);
        }
    }
}
