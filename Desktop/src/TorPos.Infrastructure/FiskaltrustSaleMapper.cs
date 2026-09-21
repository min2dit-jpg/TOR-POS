using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R176: maps TOR's immutable completed-sale snapshot to the fiskaltrust
/// Middleware DE ReceiptRequest model. This is deliberately separate from
/// SaleFiscalSigningService until physical E2E acceptance is available.
///
/// Important: TOR's generic "Karte" payment is not guessed as debit or credit.
/// A concrete fiskaltrust card tender case must be supplied whenever the
/// receipt contains a card portion.
/// </summary>
public static class FiskaltrustSaleMapper
{
    public static FiskaltrustReceiptRequest CreatePosReceipt(
        Sale sale,
        FiskaltrustLocalQueueConfiguration configuration,
        string posSystemId,
        string terminalId,
        string receiptReference,
        FiskaltrustCardTender? cardTender,
        string area = "")
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.Equals(
                sale.TransactionType,
                "SALE",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "R176 fiskaltrust mapper ist für STORNO/RETURN noch nicht freigegeben; keine Fallart wird geraten.");
        }

        if (sale.Lines.Count == 0)
            throw new InvalidOperationException(
                "fiskaltrust: leerer Kassenbeleg ist nicht zulässig.");

        if (sale.EffectiveCardPortionCents != 0 && cardTender is null)
        {
            throw new InvalidOperationException(
                "fiskaltrust: Kartenart fehlt. Debit/Credit/Online darf nicht aus 'Karte' geraten werden.");
        }

        var moment = sale.CreatedAt.ToUniversalTime();
        var charges = BuildChargeItems(sale, moment);
        var payments = BuildPayItems(
            sale,
            cardTender,
            moment);

        var chargeTotal = ToCents(charges.Sum(x => x.Amount));
        var paymentTotal = ToCents(payments.Sum(x => x.Amount));

        if (chargeTotal != sale.TotalCents)
            throw new InvalidOperationException(
                $"fiskaltrust ChargeItems inkonsistent: {chargeTotal} ct != Bon {sale.TotalCents} ct.");

        if (paymentTotal != sale.TotalCents)
            throw new InvalidOperationException(
                $"fiskaltrust PayItems inkonsistent: {paymentTotal} ct != Bon {sale.TotalCents} ct.");

        return new FiskaltrustReceiptRequest(
            configuration.CashBoxId,
            configuration.QueueId,
            posSystemId.Trim(),
            terminalId.Trim(),
            receiptReference.Trim(),
            moment,
            charges,
            payments,
            FiskaltrustDeCases.PosReceipt,
            "",
            Euros(sale.TotalCents),
            sale.OperatorName?.Trim() ?? "",
            area?.Trim() ?? "");
    }

    private static IReadOnlyList<FiskaltrustChargeItem> BuildChargeItems(
        Sale sale,
        DateTimeOffset moment)
    {
        var result = new List<FiskaltrustChargeItem>();
        var position = 1;
        var takeAway = sale.ImHaus == false
            ? FiskaltrustDeCases.TakeAwayFlag
            : 0L;

        foreach (var line in sale.Lines)
        {
            if (line.Quantity <= 0m ||
                PfandProducts.IsDepositReturn(line))
            {
                throw new InvalidOperationException(
                    "R176 fiskaltrust mapper unterstützt in der Produktionsverdrahtung noch keine negativen/Retouren-Positionen.");
            }

            var description = FiscalProcessData.LineText(line);
            var productNumber = line.ProductId > 0
                ? line.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";

            if (line.HasVatAllocations)
            {
                // A mixed-VAT menu remains one customer-facing TOR line but
                // needs distinct fiscal VAT buckets. LineAllocations is the
                // same exact-cent split used by TOR receipt/DSFinV-K logic.
                foreach (var allocation in MenuVatPolicy.LineAllocations(line))
                {
                    if (allocation.GrossCents == 0)
                        continue;

                    result.Add(new FiskaltrustChargeItem(
                        position++,
                        1m,
                        description,
                        Euros(allocation.GrossCents),
                        allocation.VatRate,
                        FiskaltrustDeCases.ChargeForVat(allocation.VatRate) | takeAway,
                        "",
                        productNumber,
                        line.Barcode ?? "",
                        "Stück",
                        1m,
                        Euros(allocation.GrossCents),
                        moment));
                }

                continue;
            }

            var depositTotal = line.PfandCents == 0
                ? 0L
                : (long)Math.Round(
                    line.Quantity * line.PfandCents,
                    MidpointRounding.AwayFromZero);
            var merchandiseTotal = line.LineTotalCents - depositTotal;

            if (merchandiseTotal != 0)
            {
                AddOrdinary(
                    result,
                    ref position,
                    line,
                    description,
                    productNumber,
                    merchandiseTotal,
                    line.VatRate,
                    FiskaltrustDeCases.ChargeForVat(line.VatRate) | takeAway,
                    moment);
            }

            if (depositTotal != 0)
            {
                AddOrdinary(
                    result,
                    ref position,
                    line,
                    "Pfand · " + description,
                    productNumber.Length == 0 ? "" : productNumber + "-PFAND",
                    depositTotal,
                    line.VatRate,
                    FiskaltrustDeCases.ReturnableForVat(line.VatRate) | takeAway,
                    moment);
            }
        }

        // Manual whole-receipt discount is not hidden in payment totals.
        // Reuse TOR's exact VAT proration and add one negative Rabatt line per
        // VAT bucket so ChargeItems still reconcile exactly to sale.TotalCents.
        if (sale.DiscountCents > 0)
        {
            var originalByVat = sale.Lines
                .SelectMany(MenuVatPolicy.LineAllocations)
                .GroupBy(x => x.VatRate)
                .ToDictionary(
                    g => g.Key,
                    g => g.Sum(x => x.GrossCents));

            foreach (var group in VatSummaryCalculator.Compute(
                         sale.Lines,
                         sale.DiscountCents))
            {
                var before = originalByVat.GetValueOrDefault(group.Rate);
                var discount = group.GrossCents - before;
                if (discount == 0)
                    continue;

                result.Add(new FiskaltrustChargeItem(
                    position++,
                    1m,
                    "Rabatt",
                    Euros(discount),
                    group.Rate,
                    FiskaltrustDeCases.DiscountForVat(group.Rate) | takeAway,
                    "",
                    "",
                    "",
                    "Stück",
                    1m,
                    Euros(discount),
                    moment));
            }
        }

        return result;
    }

    private static void AddOrdinary(
        List<FiskaltrustChargeItem> target,
        ref int position,
        CartLine line,
        string description,
        string productNumber,
        long amountCents,
        decimal vatRate,
        long chargeCase,
        DateTimeOffset moment)
    {
        // For kg articles TOR stores quantity in kg. fiskaltrust/DSFinV-K has
        // both MENGE and FAKTOR; represent one weighed receipt entry with the
        // kg amount as UnitQuantity and the derived €/kg price.
        var quantity = line.IsWeighted ? 1m : line.Quantity;
        var unitQuantity = line.IsWeighted ? line.Quantity : 1m;
        var unitPrice = unitQuantity == 0m
            ? Euros(amountCents)
            : Euros(amountCents) / (quantity * unitQuantity);

        target.Add(new FiskaltrustChargeItem(
            position++,
            quantity,
            description,
            Euros(amountCents),
            vatRate,
            chargeCase,
            "",
            productNumber,
            line.Barcode ?? "",
            line.IsWeighted ? "kg" : (string.IsNullOrWhiteSpace(line.Unit) ? "Stück" : line.Unit),
            unitQuantity,
            decimal.Round(unitPrice, 5, MidpointRounding.AwayFromZero),
            moment));
    }

    private static IReadOnlyList<FiskaltrustPayItem> BuildPayItems(
        Sale sale,
        FiskaltrustCardTender? cardTender,
        DateTimeOffset moment)
    {
        var result = new List<FiskaltrustPayItem>();
        var position = 1;

        if (sale.EffectiveCashPortionCents != 0)
        {
            result.Add(new FiskaltrustPayItem(
                position++,
                1m,
                "Bar",
                Euros(sale.EffectiveCashPortionCents),
                FiskaltrustDeCases.PayCashEur,
                "Bar",
                "EUR",
                moment));
        }

        if (sale.EffectiveCardPortionCents != 0)
        {
            var tender = cardTender ??
                throw new InvalidOperationException(
                    "fiskaltrust Kartenart fehlt.");

            result.Add(new FiskaltrustPayItem(
                position,
                1m,
                tender switch
                {
                    FiskaltrustCardTender.DebitCard => "Debitkarte",
                    FiskaltrustCardTender.CreditCard => "Kreditkarte",
                    FiskaltrustCardTender.Online => "Online-Zahlung",
                    _ => "Kundenkarte"
                },
                Euros(sale.EffectiveCardPortionCents),
                PayCase(tender),
                "Unbar",
                tender.ToString(),
                moment));
        }

        return result;
    }

    private static long PayCase(FiskaltrustCardTender tender) =>
        tender switch
        {
            FiskaltrustCardTender.DebitCard => FiskaltrustDeCases.PayDebitCard,
            FiskaltrustCardTender.CreditCard => FiskaltrustDeCases.PayCreditCard,
            FiskaltrustCardTender.Online => FiskaltrustDeCases.PayOnline,
            FiskaltrustCardTender.CustomerCard => FiskaltrustDeCases.PayCustomerCard,
            _ => throw new ArgumentOutOfRangeException(nameof(tender))
        };

    private static decimal Euros(long cents) => cents / 100m;

    private static long ToCents(decimal euros) =>
        checked((long)Math.Round(
            euros * 100m,
            MidpointRounding.AwayFromZero));
}
