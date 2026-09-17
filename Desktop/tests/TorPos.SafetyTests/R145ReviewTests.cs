using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TorPos.Core;
using TorPos.Infrastructure;

// R145: the digital receipt through TOR Cloud.
//
// AEAO zu § 146a Nr. 2.5.2 - 2.5.7: electronic receipt after the transaction is
// finished, with the customer's consent, in a standardised data format (a QR
// code leading to it is allowed), right after the sale. TOR Cloud side - token,
// separate domains, noindex/no-store, expiry, PDF - is tested in Cloud/tests.
// Here: the document the till sends, the contract both sides share, and the
// rules the till applies before anything leaves it.
public static class R145ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var createdAt = new DateTimeOffset(new DateTime(2026, 9, 17, 12, 0, 5, 123, DateTimeKind.Local));
        var tseStart = new DateTimeOffset(2026, 9, 17, 9, 59, 35, 123, TimeSpan.Zero);
        var tseEnd = new DateTimeOffset(2026, 9, 17, 10, 0, 5, 123, TimeSpan.Zero);
        var lines = new[]
        {
            new CartLine { ProductName = "Çiğ Köfte Dürüm", Quantity = 2, UnitPriceCents = 650, VatRate = 7m },
            new CartLine
            {
                ProductName = "Ayran", VariantName = "0,25l", Quantity = 1, UnitPriceCents = 250, VatRate = 19m,
                PromotionId = 7, PromotionName = "Getränke", PromotionPercent = 10, PromotionDiscountUnitCents = 28
            }
        };
        var job = new ReceiptPrintJob(
            145, createdAt, "Imbiss am Markt", "Hauptstraße 1, 10115 Berlin", "27/123/45678", "", "",
            "Vielen Dank für Ihren Einkauf!", "Bar 10,00 € / Karte 5,00 €", 50, 1500, lines,
            FiscalTestMode: false,
            EasSerial: "TORPOS-538AE94ABA78A2058181215",
            TseSerial: "a1b2c3d4e5f60718",
            TseTransactionNumber: "1450",
            SignatureCounter: 28114,
            ProcessStart: tseStart,
            ProcessEnd: tseEnd,
            VerificationValue: "MEUCIQDk3Fh0dAXGfYJp6m1gE1sL0n2n9sJ6y6oM0m3Yc3a8uAIgQ9x2Qv8b3m2V0r0fXk1c9o4mZs0v5Q1y2p7cV3n8wHk=",
            OperatorName: "kassierer1",
            TenderedCents: 98765,
            ChangeCents: 97765,
            PickupNumber: 23,
            TseStartLogTime: tseStart);
        var payments = DigitalReceiptDocument.PaymentsFor(PaymentMethod.Mixed, 1000, 500);
        var document = DigitalReceiptDocument.From(job, payments);

        // ---------- the document ----------
        assert(
            document.SubtotalCents - document.DiscountCents == document.TotalCents &&
            document.Vat.Sum(v => v.GrossCents) == document.TotalCents &&
            document.Vat.All(v => v.NetCents + v.TaxCents == v.GrossCents) &&
            document.Payments.Sum(p => p.AmountCents) == document.TotalCents &&
            document.MissingFields.Count == 0,
            "R145 the digital receipt adds up the way TOR Cloud checks it: positions, discount, VAT groups and payments");

        // ---------- the contract with TOR Cloud ----------
        string? fixture = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && fixture is null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "Cloud", "tests", "fixtures", "digitalbon-kasse.json");
            if (File.Exists(candidate))
                fixture = candidate;
        }
        var sentPayload = JsonSerializer.SerializeToNode(document.ToCloudPayload());
        var expectedPayload = fixture is null ? null : JsonNode.Parse(await File.ReadAllTextAsync(fixture))?["receipt"];
        assert(
            expectedPayload is not null && JsonNode.DeepEquals(sentPayload, expectedPayload),
            $"R145 the till sends exactly the document of Cloud/tests/fixtures/digitalbon-kasse.json, which the Cloud tests accept unchanged (found: {fixture ?? "no fixture"}, sent: {sentPayload?.ToJsonString()})");

        var reference = DigitalReceiptDocument.ReferenceFor(job);
        assert(
            reference == DigitalReceiptDocument.ReferenceFor(job with { OperatorName = "other", TenderedCents = 0 }) &&
            reference != DigitalReceiptDocument.ReferenceFor(job with { ReceiptNumber = 146 }) &&
            Regex.IsMatch(reference, "^[A-Za-z0-9._:-]{8,120}$"),
            "R145 the same receipt always has the same upload reference, so a retry cannot publish a second receipt");

        assert(
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, 700, 0).Single() == new DigitalReceiptPayment("Bar", 700) &&
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Card, 0, 700).Single() == new DigitalReceiptPayment("Karte", 700) &&
            DigitalReceiptDocument.PaymentsFor(PaymentMethod.Card, 0, 0).Single() == new DigitalReceiptPayment("Karte", 0),
            "R145 the payment lines are cash and card with their amounts");

        // ---------- publishing ----------
        var dir145 = Path.Combine(root, "r145-digitalbon");
        Directory.CreateDirectory(dir145);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir145, "r145.db"));
        var settings = new SettingsRepository(db);

        const string token = "f07QSoPVMkkp77T5cS1uM2J2FMSTIoeTGM3gwlUFBVA";
        var link = $"https://bon.tor-pos.de/r/{token}";
        string Answer(string url) =>
            $$"""{"ok":true,"receipt_id":"4XolblWHTfOto0sS","url":"{{url}}","created_at":"2026-09-17T10:00:06.000Z","expires_at":"2026-12-16T10:00:06.000Z","reissued":false}""";
        static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        var calls = new List<(string Route, string Body)>();
        var cloudActive = false;
        var publisher = new CloudDigitalReceiptService(
            settings,
            (route, body, ct) =>
            {
                calls.Add((route, JsonSerializer.Serialize(body)));
                return Task.FromResult(Json(Answer(link)));
            },
            () => Task.FromResult(cloudActive),
            TimeSpan.FromSeconds(5));

        var offeredWhenOff = await publisher.IsAvailableAsync();
        await settings.SaveManyAsync(new Dictionary<string, string> { [CloudDigitalReceiptService.EnabledSetting] = "true" });
        var offeredWithoutCloud = await publisher.IsAvailableAsync();
        cloudActive = true;
        assert(
            !offeredWhenOff && !offeredWithoutCloud && await publisher.IsAvailableAsync() &&
            !await new CloudDigitalReceiptService(settings, null).IsAvailableAsync(),
            "R145 the choice is offered only when switched on and TOR Cloud is set up and active on the till");

        var publication = await publisher.PublishAsync(document, reference);
        var sent = calls.Single();
        using (var body = JsonDocument.Parse(sent.Body))
        {
            assert(
                sent.Route == "api/v1/devices/receipts" &&
                body.RootElement.GetProperty("receipt_ref").GetString() == reference &&
                body.RootElement.GetProperty("receipt").GetProperty("format").GetString() == DigitalReceiptDocument.Format &&
                publication.Url == link && publication.ReceiptId == "4XolblWHTfOto0sS" &&
                publication.ExpiresAt == new DateTimeOffset(2026, 12, 16, 10, 0, 6, TimeSpan.Zero),
                "R145 the till publishes through the device API and shows the link TOR Cloud returns");
        }
        assert(
            !sent.Body.Contains("kassierer1") && !sent.Body.Contains("98765"),
            "R145 only what the receipt shows leaves the till - no operator, no cash tendered (DSGVO Art. 5 Abs. 1 lit. c)");

        calls.Clear();
        string refusal = "";
        try
        {
            await publisher.PublishAsync(DigitalReceiptDocument.From(job with { TseTransactionNumber = "" }, payments), "bon-145-incomplete");
        }
        catch (InvalidOperationException ex)
        {
            refusal = ex.Message;
        }
        assert(
            refusal.Contains("Transaktionsnummer") && calls.Count == 0,
            "R145 a receipt missing mandatory fields is not published - the printer's rule (R122) - and nothing is sent");

        var testDocument = DigitalReceiptDocument.From(job with { FiscalTestMode = true, ReceiptNumber = 0, TseTransactionNumber = "" }, payments);
        await publisher.PublishAsync(testDocument, "bon-0-1");
        assert(
            testDocument.TestReceipt && testDocument.ReceiptNumber == "TEST" && testDocument.Tse.Count == 0 &&
            testDocument.MissingFields.Count == 0 && calls.Count == 1,
            "R145 a test sale can be tried as digital receipt, marked TESTBON and without TSE data");

        var refusedLinks = 0;
        foreach (var bad in new[]
                 {
                     $"http://bon.tor-pos.de/r/{token}",
                     $"https://bon.tor-pos.de/login/{token}",
                     $"https://user:pw@bon.tor-pos.de/r/{token}",
                     $"https://bon.tor-pos.de/r/{token}?next=x",
                     "https://bon.tor-pos.de/r/short",
                     ""
                 })
        {
            var badCloud = new CloudDigitalReceiptService(settings, (_, _, _) => Task.FromResult(Json(Answer(bad))), () => Task.FromResult(true), TimeSpan.FromSeconds(5));
            try
            {
                await badCloud.PublishAsync(document, reference);
            }
            catch (InvalidDataException)
            {
                refusedLinks++;
            }
        }
        assert(
            refusedLinks == 6 && DigitalReceiptLink.IsAcceptable($"http://localhost:8787/r/{token}"),
            "R145 only a receipt link over HTTPS becomes a QR code in front of the customer (HTTP only on this PC)");

        var slow = new CloudDigitalReceiptService(
            settings,
            async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return default;
            },
            () => Task.FromResult(true),
            TimeSpan.FromMilliseconds(200));
        var timedOut = false;
        try
        {
            await slow.PublishAsync(document, reference);
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        assert(timedOut, "R145 when TOR Cloud does not answer in time the till stops waiting - the paper receipt follows (AEAO zu § 146a Nr. 2.5.7)");
    }
}
