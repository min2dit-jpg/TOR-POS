using System.Net;
using System.Text;
using TorPos.Infrastructure;

public static class FiskaltrustIntegrationTests
{
    public static async Task Run(Action<bool, string> assert)
    {
        var localOptions = new FiskaltrustMiddlewareOptions(
            new Uri("rest://localhost:1500/queue-test"),
            Guid.Empty,
            Guid.Empty,
            "");

        assert(
            localOptions.HttpBaseUri.ToString().StartsWith(
                "http://localhost:1500/queue-test",
                StringComparison.OrdinalIgnoreCase),
            "fiskaltrust local rest:// endpoint is normalized to http:// without changing port/path");

        var localHandler = new FakeHandler();
        using (var http = new HttpClient(localHandler))
        {
            var client = new FiskaltrustMiddlewareClient(http, localOptions);
            var echo = await client.EchoAsync("TOR POS TEST");

            assert(
                echo == "TOR POS TEST" &&
                localHandler.LastPath == "/queue-test/json/v1/Echo" &&
                !localHandler.SawCashboxHeader &&
                !localHandler.SawAccessTokenHeader,
                "fiskaltrust local Echo works without CashBox/POS IDs and sends no portal credentials");
        }

        var saasCashBoxId = Guid.NewGuid();
        var saasHandler = new FakeHandler();
        using (var http = new HttpClient(saasHandler))
        {
            var client = new FiskaltrustMiddlewareClient(
                http,
                new FiskaltrustMiddlewareOptions(
                    new Uri("https://example.invalid/cashbox/"),
                    saasCashBoxId,
                    Guid.Empty,
                    "",
                    "secret-test-token",
                    UseSaasHeaders: true));

            _ = await client.EchoAsync("SAAS");

            assert(
                saasHandler.SawCashboxHeader &&
                saasHandler.CashboxHeader == saasCashBoxId.ToString() &&
                saasHandler.SawAccessTokenHeader &&
                saasHandler.AccessTokenHeader == "secret-test-token",
                "fiskaltrust SaaS mode adds CashBox and access-token headers only when explicitly enabled");
        }

        var signHandler = new FakeHandler();
        using (var http = new HttpClient(signHandler))
        {
            var client = new FiskaltrustMiddlewareClient(http, localOptions);
            var rejected = false;
            try
            {
                await client.SignAsync(new FiskaltrustReceiptRequest
                {
                    CbReceiptReference = "test-1"
                });
            }
            catch (InvalidOperationException ex)
            {
                rejected = ex.Message.Contains("CashBox-ID", StringComparison.Ordinal);
            }

            assert(
                rejected && signHandler.Calls == 0,
                "fiskaltrust Sign refuses missing fiscal identity before any network request");
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string LastPath { get; private set; } = "";
        public bool SawCashboxHeader { get; private set; }
        public string CashboxHeader { get; private set; } = "";
        public bool SawAccessTokenHeader { get; private set; }
        public string AccessTokenHeader { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastPath = request.RequestUri?.AbsolutePath ?? "";

            SawCashboxHeader =
                request.Headers.TryGetValues("cashboxid", out var cashboxValues);
            CashboxHeader =
                cashboxValues?.SingleOrDefault() ?? "";

            SawAccessTokenHeader =
                request.Headers.TryGetValues("accesstoken", out var tokenValues);
            AccessTokenHeader =
                tokenValues?.SingleOrDefault() ?? "";

            var requestedMessage = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();

            var echo = requestedMessage.Contains("SAAS", StringComparison.Ordinal)
                ? "SAAS"
                : "TOR POS TEST";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"message\":\"" + echo + "\"}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
