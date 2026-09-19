using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// Side-effect-free request builders for the fiskaltrust sandbox path.
/// They only compose API payloads; sending them is always an explicit caller
/// decision. Production checkout does not reference this class.
/// </summary>
public static class FiskaltrustSandboxRequests
{
    /// <summary>
    /// Germany ZeroReceipt: empty charge/pay blocks and implicit flow.
    /// Useful for a controlled Middleware/TSE functional check once a real
    /// TSE is present. The caller supplies a unique receipt reference.
    /// </summary>
    public static FiskaltrustReceiptRequest ZeroReceipt(
        string receiptReference,
        DateTimeOffset moment,
        string user = "TOR-POS")
    {
        if (string.IsNullOrWhiteSpace(receiptReference))
            throw new ArgumentException(
                "Eine eindeutige cbReceiptReference ist erforderlich.",
                nameof(receiptReference));

        return new FiskaltrustReceiptRequest
        {
            CbReceiptReference = receiptReference.Trim(),
            CbReceiptMoment = moment,
            CbUser = user,
            CbChargeItems = Array.Empty<FiskaltrustChargeItem>(),
            CbPayItems = Array.Empty<FiskaltrustPayItem>(),
            FtReceiptCase =
                FiskaltrustDeCases.WithImplicitFlow(
                    FiskaltrustDeCases.ZeroReceipt)
        };
    }

    /// <summary>
    /// ZeroReceipt plus the DE TSE-info request flag. This is the preferred
    /// first physical-hardware diagnostic because it asks for detailed TSE
    /// status without also forcing self-test/time-update.
    /// </summary>
    public static FiskaltrustReceiptRequest ZeroReceiptWithTseInfo(
        string receiptReference,
        DateTimeOffset moment,
        string user = "TOR-POS")
    {
        var request = ZeroReceipt(receiptReference, moment, user);
        return request with
        {
            FtReceiptCase =
                request.FtReceiptCase |
                FiskaltrustDeCases.ZeroReceiptTseInfoFlag
        };
    }

    /// <summary>
    /// Explicit-flow START. Germany requires empty charge/pay arrays and the
    /// same cbReceiptReference to be reused by later update/final calls.
    /// </summary>
    public static FiskaltrustReceiptRequest StartExplicitTransaction(
        string receiptReference,
        DateTimeOffset moment,
        string user = "TOR-POS")
    {
        if (string.IsNullOrWhiteSpace(receiptReference))
            throw new ArgumentException(
                "Eine eindeutige cbReceiptReference ist erforderlich.",
                nameof(receiptReference));

        return new FiskaltrustReceiptRequest
        {
            CbReceiptReference = receiptReference.Trim(),
            CbReceiptMoment = moment,
            CbUser = user,
            CbChargeItems = Array.Empty<FiskaltrustChargeItem>(),
            CbPayItems = Array.Empty<FiskaltrustPayItem>(),
            FtReceiptCase = FiskaltrustDeCases.StartTransaction
        };
    }

    /// <summary>
    /// Final POS receipt for an already-open explicit transaction. Reuses the
    /// exact same business payload as the restricted cash builder but removes
    /// the implicit-flow flag.
    /// </summary>
    public static FiskaltrustReceiptRequest FinishExplicitSimpleCashSale(
        Sale sale,
        string receiptReference)
    {
        var request = SimpleCashSale(sale, receiptReference);
        return request with
        {
            FtReceiptCase = FiskaltrustDeCases.PosReceipt
        };
    }

    /// <summary>
    /// First real-sale sandbox payload after ZeroReceipt: a deliberately narrow
    /// cash-only POS receipt. It refuses scenarios whose mapping still needs
    /// separate validation (card/mixed, manual discount, reversals, cancelled
    /// positions or negative/zero lines). Production checkout never calls it.
    /// </summary>
    public static FiskaltrustReceiptRequest SimpleCashSale(
        Sale sale,
        string receiptReference)
    {
        ArgumentNullException.ThrowIfNull(sale);

        if (string.IsNullOrWhiteSpace(receiptReference))
            throw new ArgumentException(
                "Eine eindeutige cbReceiptReference ist erforderlich.",
                nameof(receiptReference));

        if (!string.Equals(sale.TransactionType, "SALE", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale unterstützt noch keinen STORNO/RETURN.");

        if (sale.PaymentMethod != PaymentMethod.Cash ||
            sale.EffectiveCardPortionCents != 0)
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale unterstützt nur reine Barzahlung.");

        if (sale.DiscountCents != 0)
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale unterstützt noch keinen manuellen Bon-Rabatt.");

        if (sale.CancelledLines.Count != 0)
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale unterstützt noch keine stornierten Positionen.");

        if (sale.Lines.Count == 0)
            throw new InvalidOperationException(
                "Ein fiskaltrust POS-Beleg benötigt mindestens eine Position.");

        if (sale.Lines.Any(x => x.Quantity <= 0 || x.LineTotalCents <= 0))
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale akzeptiert vor der Realhardware-Abnahme nur positive Verkaufspositionen.");

        if (sale.Lines.Any(x =>
                x.PfandCents != 0 ||
                PfandProducts.IsDeposit(x.ProductId)))
        {
            throw new InvalidOperationException(
                "Sandbox-SimpleCashSale unterstützt Pfand erst nach eigener fiskaltrust-Pfandzuordnung.");
        }

        var lineTotal = sale.Lines.Sum(x => x.LineTotalCents);
        if (lineTotal != sale.TotalCents)
            throw new InvalidOperationException(
                $"Positionssumme ({lineTotal}) und Belegsumme ({sale.TotalCents}) stimmen nicht überein.");

        var chargeItems = sale.Lines
            .Select((line, index) =>
            {
                var itemCase = FiskaltrustDeCases.ChargeItemCaseForVat(line.VatRate);

                // TOR only needs the explicit take-away marker where the
                // reduced food rate is actually the relevant distinction.
                if (sale.ImHaus == false &&
                    line.ImHausApplicable &&
                    line.VatRate == 7m)
                {
                    itemCase |= FiskaltrustDeCases.TakeAwayChargeItemFlag;
                }

                return new FiskaltrustChargeItem
                {
                    Position = index + 1,
                    Quantity = line.Quantity,
                    Description = FiscalProcessData.LineText(line),
                    Amount = line.LineTotalCents / 100m,
                    VatRate = line.VatRate,
                    FtChargeItemCase = itemCase,
                    ProductNumber = line.ProductId > 0
                        ? line.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : null,
                    ProductBarcode = string.IsNullOrWhiteSpace(line.Barcode)
                        ? null
                        : line.Barcode,
                    Unit = "Stück",
                    UnitPrice = line.UnitPriceCents / 100m,
                    // For implicit flow fiskaltrust derives the overall action
                    // start from the earliest request/item timestamp. Preserve
                    // TOR's first-position start when available.
                    Moment = index == 0
                        ? sale.StartedAt ?? sale.CreatedAt
                        : sale.CreatedAt
                };
            })
            .ToArray();

        return new FiskaltrustReceiptRequest
        {
            CbReceiptReference = receiptReference.Trim(),
            CbReceiptMoment = sale.CreatedAt,
            CbUser = sale.OperatorName,
            CbChargeItems = chargeItems,
            CbPayItems =
            [
                new FiskaltrustPayItem
                {
                    Position = 1,
                    Quantity = 1m,
                    Description = "Bar",
                    Amount = sale.TotalCents / 100m,
                    FtPayItemCase = FiskaltrustDeCases.CashPayment,
                    Moment = sale.CreatedAt
                }
            ],
            CbReceiptAmount = sale.TotalCents / 100m,
            FtReceiptCase =
                FiskaltrustDeCases.WithImplicitFlow(
                    FiskaltrustDeCases.PosReceipt)
        };
    }
}
