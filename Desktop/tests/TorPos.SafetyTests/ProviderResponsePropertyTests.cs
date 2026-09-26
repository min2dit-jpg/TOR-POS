using System.Text;
using System.Text.Json;
using TorPos.Core;
using TorPos.Infrastructure;

// Seeded property checks for what payment terminals and TSE providers send
// back. A garbled or hostile answer must never turn into "paid" or "signed":
// a failed card payment is never Approved, a stored outcome that cannot be
// read never becomes Approved, and a fiskaltrust answer is a signature only
// when every fiscal field is really there.
public static class ProviderResponsePropertyTests
{
    private const int Seed = 20260928;

    public static void Run(Action<bool, string> assert)
    {
        PaymentOutcomes(assert);
        FiskaltrustAnswers(assert);
    }

    private static string Noise(Random random, int max)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-|:.äöüİı\t\r\n\u0000";
        return new string(Enumerable.Range(0, random.Next(max + 1)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }

    private static void PaymentOutcomes(Action<bool, string> assert)
    {
        var random = new Random(Seed);
        string[] words = ["APPROVED", "SUCCESS", "OK", "PAID", "DECLINED", "CANCELLED", "ERROR", "TIMEOUT", "ZAHLUNG ABGELEHNT", "VORGANG ABGEBROCHEN", "GENEHMIGT", "AUTHORISED"];
        var failure = "";
        for (var i = 0; i < 6000 && failure.Length == 0; i++)
        {
            string Pick() => random.Next(3) switch
            {
                0 => words[random.Next(words.Length)],
                1 => Noise(random, 30) + words[random.Next(words.Length)] + Noise(random, 10),
                _ => Noise(random, 40)
            };
            var state = random.Next(6) == 0 ? null : Pick();
            var error = random.Next(4) == 0 ? null : Pick();
            var terminal = random.Next(4) == 0 ? null : Pick();
            try
            {
                var outcome = PaymentTerminalOutcomeClassifier.ClassifyCompletedFailure(state, error, terminal);
                if (outcome is not (PaymentTerminalOutcome.Cancelled or PaymentTerminalOutcome.Declined or PaymentTerminalOutcome.Unknown))
                    failure = $"iteration {i}: failed payment classified as {outcome}";

                var stored = Pick();
                var parsed = PaymentOutcomeCodec.ParseOutcome(stored);
                if (parsed == PaymentTerminalOutcome.Approved &&
                    !string.Equals(stored.Trim(), "APPROVED", StringComparison.OrdinalIgnoreCase))
                    failure = $"iteration {i}: '{stored}' read back as Approved";
                var resolution = PaymentOutcomeCodec.ParseResolution(stored);
                if (resolution is CheckoutResolution.AutoApproved or CheckoutResolution.ManualPaid &&
                    PaymentOutcomeCodec.ToStorage(resolution) != stored.Trim().ToUpperInvariant())
                    failure = $"iteration {i}: '{stored}' read back as paid";
            }
            catch (Exception ex)
            {
                failure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        var roundTrip =
            Enum.GetValues<PaymentTerminalOutcome>().All(x => PaymentOutcomeCodec.ParseOutcome(PaymentOutcomeCodec.ToStorage(x)) == x) &&
            Enum.GetValues<CheckoutResolution>().All(x => PaymentOutcomeCodec.ParseResolution(PaymentOutcomeCodec.ToStorage(x)) == x);
        assert(failure.Length == 0 && roundTrip,
            $"Property (seed {Seed}): 6000 terminal answers - a failed card payment is never Approved, unreadable stored outcomes never read back as paid, and every outcome round-trips {failure}");
    }

    private static FiskaltrustSignature Sig(long type, string data) => new(0, type, "", data);

    private static void FiskaltrustAnswers(Action<bool, string> assert)
    {
        var random = new Random(Seed + 1);
        var failure = "";
        var signed = 0;
        long[] types =
        [
            FiskaltrustDeCases.SignatureTransactionNumber, FiskaltrustDeCases.SignatureCounter,
            FiskaltrustDeCases.SignatureStartTime, FiskaltrustDeCases.SignatureLogTime,
            FiskaltrustDeCases.SignatureValue, FiskaltrustDeCases.SignatureTseSerial, FiskaltrustDeCases.SignatureQr
        ];

        string Valid(long type) =>
            type == FiskaltrustDeCases.SignatureTransactionNumber || type == FiskaltrustDeCases.SignatureCounter
                ? random.Next(1, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : type == FiskaltrustDeCases.SignatureStartTime || type == FiskaltrustDeCases.SignatureLogTime
                    ? "2026-09-26T10:00:00Z"
                    : "abc" + random.Next(1000);

        for (var i = 0; i < 4000 && failure.Length == 0; i++)
        {
            var signatures = new List<FiskaltrustSignature>();
            foreach (var type in types)
            {
                switch (random.Next(5))
                {
                    case 0: break;
                    case 1: signatures.Add(Sig(type, Noise(random, 20))); break;
                    default: signatures.Add(Sig(type, Valid(type))); break;
                }
            }

            var response = new FiskaltrustReceiptResponse(
                "", "", "", 0, "", "R-" + i, "", random.Next(3) == 0 ? "ft#" + Noise(random, 8) : "",
                DateTimeOffset.UtcNow, random.Next(8) == 0 ? null! : signatures, 0x4445000000000000L, "");
            try
            {
                var result = FiskaltrustQueueClient.ParseFiscalResult(response);
                if (result.Success)
                {
                    signed++;
                    if (result.TseSerialNumber.Trim().Length == 0 || result.Signature.Trim().Length == 0 ||
                        result.StartLogTime is null || result.LogTime is null)
                        failure = $"iteration {i}: success without complete TSE data";
                }
                else if (result.Message.Length == 0)
                {
                    failure = $"iteration {i}: failure without message";
                }
            }
            catch (Exception ex)
            {
                failure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        var junkParsed = true;
        for (var i = 0; i < 1000 && junkParsed; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(random.Next(2) == 0 ? "{\"ftSignatures\":[" + Noise(random, 60) : Noise(random, 80));
            try
            {
                var parsed = JsonSerializer.Deserialize<FiskaltrustReceiptResponse>(bytes);
                if (parsed is not null)
                    _ = FiskaltrustQueueClient.ParseFiscalResult(parsed);
            }
            catch (JsonException) { }
            catch (ArgumentNullException) { }
            catch (Exception) { junkParsed = false; }
        }

        assert(failure.Length == 0 && signed > 100 && junkParsed,
            $"Property (seed {Seed + 1}): 4000 fiskaltrust answers are a signature only with transaction, counter, serial, signature and both times; 1000 malformed bodies are rejected without a crash {failure}");
    }
}

// E-Rechnung boundary: TOR POS hands a request to TOR E-Rechnung, it never
// issues the invoice itself and never changes the sale.
public static class InvoiceRequestContractTests
{
    public static void Run(Action<bool, string> assert)
    {
        Sale Signed() => new()
        {
            Id = 42,
            ReceiptNumber = 1042,
            CreatedAt = new DateTimeOffset(2026, 9, 26, 12, 30, 0, TimeSpan.FromHours(2)),
            TotalCents = 1000 + 2 * 250 - 100,
            DiscountCents = 100,
            TseTransactionNumber = "77",
            TseSignature = "c2ln",
            TseSerialNumber = "SER-1",
            Lines = new[]
            {
                new CartLine { ProductName = "Döner", Quantity = 1, UnitPriceCents = 1000, VatRate = 7m },
                new CartLine { ProductName = "Ayran", VariantName = "0,25 l", Quantity = 2, UnitPriceCents = 250, VatRate = 19m }
            }
        };
        var buyer = new InvoiceBuyer("Muster GmbH", "Hauptstr. 1", "10115", "Berlin", "de", "de123456789", "buchhaltung@muster.de");
        var sale = Signed();
        var request = InvoiceRequestBuilder.From(sale, buyer, "EAS-1", sale.CreatedAt.AddMinutes(5), "req-1");
        var back = InvoiceRequest.FromJson(request.ToJson());
        assert(
            request.Format == InvoiceRequest.FormatV1 &&
            request.TotalGrossCents == sale.TotalCents &&
            request.Vat.Sum(x => x.GrossCents) == sale.TotalCents &&
            request.Vat.All(x => x.NetCents + x.TaxCents == x.GrossCents) &&
            request.Transaction is { KassenId: "EAS-1", SaleId: 42, ReceiptNumber: 1042, TseTransactionNumber: "77", TseSerialNumber: "SER-1" } &&
            request.Buyer is { CountryCode: "DE", VatId: "DE123456789" } &&
            request.Lines[1].Name == "Ayran (0,25 l)" && request.Lines[1].Quantity == "2" &&
            back.TotalGrossCents == request.TotalGrossCents && back.Transaction == request.Transaction &&
            back.Lines.SequenceEqual(request.Lines) && back.Vat.SequenceEqual(request.Vat) &&
            request.ToJson().Contains("\"receipt_number\": 1042", StringComparison.Ordinal),
            "E-Rechnung boundary: an invoice request carries the booked sale reference, buyer and cent-exact VAT groups and survives JSON round-trip unchanged");

        bool Refused(Func<InvoiceRequest> build)
        {
            try { build(); return false; }
            catch (InvalidOperationException) { return true; }
            catch (ArgumentException) { return true; }
        }

        var unsigned = Signed(); unsigned.TseSignature = "";
        var storno = Signed(); storno.TransactionType = "STORNO";
        var outage = Signed(); outage.TseTransactionNumber = ""; outage.TseSignature = ""; outage.TseOutage = true;
        assert(
            Refused(() => InvoiceRequestBuilder.From(unsigned, buyer, "EAS-1", DateTimeOffset.Now)) &&
            Refused(() => InvoiceRequestBuilder.From(storno, buyer, "EAS-1", DateTimeOffset.Now)) &&
            Refused(() => InvoiceRequestBuilder.From(Signed(), buyer with { Street = " " }, "EAS-1", DateTimeOffset.Now)) &&
            Refused(() => InvoiceRequestBuilder.From(Signed(), buyer with { CountryCode = "Deutschland" }, "EAS-1", DateTimeOffset.Now)) &&
            Refused(() => InvoiceRequestBuilder.From(Signed(), buyer with { Email = "kein mail" }, "EAS-1", DateTimeOffset.Now)) &&
            Refused(() => InvoiceRequestBuilder.From(Signed(), buyer, " ", DateTimeOffset.Now)) &&
            !Refused(() => InvoiceRequestBuilder.From(outage, buyer, "EAS-1", DateTimeOffset.Now)),
            "E-Rechnung boundary: no request for an unsigned sale, a Storno, an incomplete buyer address or without Kassen-ID; a documented TSE outage is referenced as such");

        var source = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/InvoiceRequestContract.cs"));
        assert(
            !source.Contains("XRechnung", StringComparison.Ordinal) || source.Contains("no XRechnung/ZUGFeRD/EN 16931 here", StringComparison.Ordinal),
            "E-Rechnung boundary: TOR POS only defines the request contract - XRechnung/ZUGFeRD generation stays in TOR E-Rechnung");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
