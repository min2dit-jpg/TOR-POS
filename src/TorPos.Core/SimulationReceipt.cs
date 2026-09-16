namespace TorPos.Core;

/// <summary>Print-only simulation snapshot. Never allocates a fiscal receipt number.</summary>
public static class SimulationReceipt
{
    public static ReceiptPrintJob Create(CheckoutSnapshot snapshot, string companyName,
        string address, long? tenderedCents = null, string logoPath = "", long pickupNumber = 0)
    {
        // R101: for Mixed, "tendered" is what the cashier hands over against
        // the CASH PORTION only, never the whole total - the remainder is
        // the simulated card charge, exactly as in a real Mixed checkout.
        var cashDue = snapshot.EffectiveCashPortionCents;
        var hasCashLeg = snapshot.Method == PaymentMethod.Cash || snapshot.Method == PaymentMethod.Mixed;
        var tendered = hasCashLeg ? tenderedCents ?? cashDue : 0;
        if (hasCashLeg && tendered < cashDue)
            throw new ArgumentException("Gegebener Betrag ist kleiner als der fällige Bar-Anteil.");
        var paymentLabel = snapshot.Method switch
        {
            PaymentMethod.Cash => "BAR - TEST",
            PaymentMethod.Mixed => "BAR/KARTE - TEST",
            _ => "KARTE - SIMULATION"
        };
        return new ReceiptPrintJob(
            ReceiptNumber: 0, CreatedAt: DateTimeOffset.Now,
            CompanyName: companyName, CompanyAddress: address, TaxNumber: "", VatId: "",
            Header: "TESTBON - KEIN FISKALBELEG\nSIMULATION - KEINE ZAHLUNG\nTest-ID: " + snapshot.OperationId,
            Footer: "TESTBON - KEIN FISKALBELEG\nKeine echte Buchung / keine Kartenbelastung",
            PaymentLabel: paymentLabel,
            DiscountCents: snapshot.DiscountCents, TotalCents: snapshot.TotalCents,
            Lines: CheckoutSnapshot.CopyLines(snapshot.Lines), FiscalTestMode: true,
            OperatorName: snapshot.OperatorName, TenderedCents: tendered,
            ChangeCents: hasCashLeg ? tendered - cashDue : 0,
            LogoPath: logoPath, PickupNumber: pickupNumber);
    }
}
