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
}
