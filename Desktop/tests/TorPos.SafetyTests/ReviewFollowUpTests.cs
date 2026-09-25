using System.Reflection;
using TorPos.Core;
using TorPos.Infrastructure;

// Follow-up findings from the 24.09.2026 review (O-4, O-19): small fail-closed
// corrections without their own feature area.
public static class ReviewFollowUpTests
{
    public static Task Run(Action<bool, string> assert)
    {
        // O-19: an unknown stored terminal profile used to fall back to
        // AUTO_ZVT, which is production-ready - a typo could charge cards.
        var unknown = PaymentTerminalProfiles.Find("TYPO_TERMINAL");
        assert(
            unknown.Id == "UNKNOWN" &&
            !unknown.ProductionReady &&
            !PaymentTerminalProfiles.UsesZvt("TYPO_TERMINAL"),
            "O-19 an unknown terminal profile id is fail-closed, never routed through ZVT");
        assert(
            PaymentTerminalProfiles.Find(null).Id == "AUTO_ZVT" &&
            PaymentTerminalProfiles.Find("  ").Id == "AUTO_ZVT" &&
            !PaymentTerminalProfiles.All.Contains(PaymentTerminalProfiles.Unknown),
            "O-19 nothing stored still means the recommended AUTO_ZVT profile; UNKNOWN is never offered in the picker");
        assert(
            PaymentTerminalProfiles.Find("ZVT").Id == "AUTO_ZVT" &&
            PaymentTerminalProfiles.Find("ingenico").Id == "INGENICO_ZVT" &&
            PaymentTerminalProfiles.Find("VERIFONE").Id == "VERIFONE_ZVT" &&
            PaymentTerminalProfiles.Find("PAX").Id == "PAX_PROVIDER_ZVT" &&
            PaymentTerminalProfiles.Find("OTHER").Id == "OTHER_ZVT",
            "O-19 short legacy ZVT vendor names keep resolving to their ZVT profile");

        // O-4: the owned HttpClient no longer waits 100 s, and ftState bits
        // that mean "not signed by a working TSE" fail the result.
        using (var client = new FiskaltrustQueueClient(
                   new FiskaltrustLocalQueueConfiguration("", "cashbox", "queue", "http://127.0.0.1:1500/")))
        {
            var http = (HttpClient)typeof(FiskaltrustQueueClient)
                .GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(client)!;
            assert(
                http.Timeout == FiskaltrustQueueClient.DefaultSignTimeout &&
                http.Timeout <= TimeSpan.FromSeconds(10),
                "O-4 fiskaltrust Sign times out after at most 10 s instead of HttpClient's 100 s");
        }

        FiskaltrustReceiptResponse Response(long state) => new(
            "cashbox-test", "queue-test", "queue-item", 1, "TOR-1", "sale-o4", "TOR-1", "ftC#T10",
            DateTimeOffset.UtcNow,
            [
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureTransactionNumber, "", "10"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureCounter, "", "19"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureStartTime, "", "2026-09-21T10:47:42.000Z"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureLogTime, "", "2026-09-21T10:48:17.000Z"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureValue, "", "SIG"),
                new FiskaltrustSignature(1, FiskaltrustDeCases.SignatureTseSerial, "", "TSE-SERIAL")
            ],
            state,
            "");
        var lateSigning = FiskaltrustQueueClient.ParseFiscalResult(Response(0x4445000000000008L));
        var sscdDown = FiskaltrustQueueClient.ParseFiscalResult(Response(0x4445000000000002L));
        var messagePending = FiskaltrustQueueClient.ParseFiscalResult(Response(0x4445000000000010L));
        assert(
            !lateSigning.Success && lateSigning.Message.Contains("ftState", StringComparison.Ordinal) &&
            !sscdDown.Success &&
            messagePending.Success,
            "O-4 ftState late-signing or SSCD failure fails the fiscal result; informational bits do not");
        return Task.CompletedTask;
    }
}
