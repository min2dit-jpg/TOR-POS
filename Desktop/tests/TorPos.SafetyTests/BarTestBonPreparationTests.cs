using TorPos.Core;

public static class BarTestBonPreparationTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var plan19 = BarTestBonPreparation.Create(19m, "bar-test-19");
        assert(
            plan19.Snapshot.Lines.Length == 1 &&
            plan19.Snapshot.Method == PaymentMethod.Cash &&
            plan19.Snapshot.DiscountCents == 0 &&
            plan19.Snapshot.TotalCents == 100 &&
            plan19.Snapshot.EffectiveCashPortionCents == 100 &&
            plan19.Snapshot.EffectiveCardPortionCents == 0 &&
            plan19.Snapshot.Lines[0] is { VatRate: 19m, PfandCents: 0, PromotionId: 0 },
            "BAR TESTBON preparation is exactly one EUR 1.00 cash item without Pfand, promotion, discount or card leg");

        assert(
            plan19.ExpectedProcessType == "Kassenbeleg-V1" &&
            plan19.ExpectedProcessData == "Beleg^1.00_0.00_0.00_0.00_0.00^1.00:Bar",
            "BAR TESTBON 19 percent pins the DSFinV-K Anhang-I Kassenbeleg-V1 bytes");

        var plan7 = BarTestBonPreparation.Create(7m, "bar-test-7");
        assert(
            plan7.ExpectedProcessData == "Beleg^0.00_1.00_0.00_0.00_0.00^1.00:Bar",
            "BAR TESTBON 7 percent pins the reduced-rate Anhang-I container");

        var invalidVatRejected = false;
        try { BarTestBonPreparation.Create(5m, "bad-vat"); }
        catch (ArgumentOutOfRangeException) { invalidVatRejected = true; }
        assert(invalidVatRejected, "BAR TESTBON refuses every VAT rate except 7 and 19 percent");

        var preview = BarTestBonPreparation.BuildPreviewReceipt(plan19, "TOR Test", "Berlin");
        assert(
            preview.FiscalTestMode &&
            preview.ReceiptNumber == 0 &&
            preview.PaymentLabel == "BAR · TEST" &&
            !preview.OpenCashDrawer &&
            preview.Header.Contains("KEINE TSE-TRANSAKTION") &&
            TseQrCodePayload.Build(preview) == "",
            "BAR TESTBON 80 mm preparation print is visibly non-fiscal and can never open the cash drawer or emit a fiscal QR");

        var start = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var end = start.AddSeconds(2);
        var tse = SaleTseResult.SignedResult(
            "TOR-KASSE-1",
            "4711",
            "88",
            "TSE-SERIAL-1",
            "c2lnbmF0dXJl",
            end,
            start);

        var qr = string.Join(";",
            "V0",
            tse.ClientId,
            plan19.ExpectedProcessType,
            plan19.ExpectedProcessData,
            tse.TransactionNumber,
            tse.SignatureCounter,
            TseReceiptTime.Format(start),
            TseReceiptTime.Format(end),
            "ecdsa-plain-SHA256",
            "utcTime",
            tse.Signature,
            "cHVibGljLWtleQ==");

        var valid = BarTestBonPreparation.ValidateHardwareEvidence(
            plan19,
            tse,
            plan19.ExpectedProcessType,
            plan19.ExpectedProcessData,
            "FINISHED",
            qr);

        assert(
            valid.Passed && valid.Checks.Count == 10 && valid.Checks.All(x => x.Passed),
            "BAR TESTBON validator accepts a complete matching TSE/processData/QR evidence set");

        var wrongProcess = BarTestBonPreparation.ValidateHardwareEvidence(
            plan19,
            tse,
            plan19.ExpectedProcessType,
            plan19.ExpectedProcessData + "-changed",
            "FINISHED",
            qr);
        assert(
            !wrongProcess.Passed &&
            wrongProcess.Checks.Single(x => x.Name == "PROCESS_DATA").Passed == false,
            "BAR TESTBON validator turns red when the bytes sent to the TSE differ from TOR Kassenbeleg-V1");

        var wrongQr = BarTestBonPreparation.ValidateHardwareEvidence(
            plan19,
            tse,
            plan19.ExpectedProcessType,
            plan19.ExpectedProcessData,
            "FINISHED",
            qr.Replace(";4711;", ";9999;", StringComparison.Ordinal));
        assert(
            !wrongQr.Passed &&
            !wrongQr.Checks.Single(x => x.Name == "QR_PAYLOAD").Passed,
            "BAR TESTBON validator turns red when the receipt QR no longer matches the TSE transaction");

        var incomplete = BarTestBonPreparation.ValidateHardwareEvidence(
            plan19,
            SaleTseResult.SignedResult("TOR-KASSE-1", "0", "0", "", "", null, null),
            plan19.ExpectedProcessType,
            plan19.ExpectedProcessData,
            "OPEN",
            "");
        assert(
            !incomplete.Passed &&
            !incomplete.Checks.Single(x => x.Name == "TSE_SERIAL").Passed &&
            !incomplete.Checks.Single(x => x.Name == "TRANSACTION_NUMBER").Passed &&
            !incomplete.Checks.Single(x => x.Name == "SIGNATURE_COUNTER").Passed &&
            !incomplete.Checks.Single(x => x.Name == "TSE_TIMES").Passed &&
            !incomplete.Checks.Single(x => x.Name == "TSE_STATE").Passed,
            "BAR TESTBON validator rejects incomplete hardware evidence instead of treating a partial TSE response as success");

        return Task.CompletedTask;
    }
}
