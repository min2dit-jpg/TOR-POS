using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// Seeded property/fuzz checks for inputs that reach fiscal data: receipt links
// and QR payloads, DSFinV-K date ranges, cent allocation, the CSV reader and
// the TSE export parser. A fixed seed keeps every run reproducible; a failure
// names the seed and iteration. The goal is proof that unexpected input causes
// no crash, no cent drift and no silent acceptance.
public static class FiscalPropertyTests
{
    private const int Seed = 20260926;

    public static void Run(Action<bool, string> assert)
    {
        ReceiptLinks(assert);
        ExportRanges(assert);
        VatSummaries(assert);
        PartialReturns(assert);
        CsvReader(assert);
        TseExportParser(assert);
        TseSerials(assert);
    }

    private static string RandomText(Random random, int maxLength)
    {
        const string alphabet = "abcAZ09-_/:.?#@%&=+ \t\r\nä€\u0000;\"'<>\\";
        var length = random.Next(maxLength + 1);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = random.Next(10) == 0 ? (char)random.Next(0x20, 0xD7FF) : alphabet[random.Next(alphabet.Length)];
        return new string(chars);
    }

    private static void ReceiptLinks(Action<bool, string> assert)
    {
        var random = new Random(Seed);
        const string tokenAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var valid = 0;
        var failure = "";
        for (var i = 0; i < 4000 && failure.Length == 0; i++)
        {
            var token = new string(Enumerable.Range(0, 43).Select(_ => tokenAlphabet[random.Next(tokenAlphabet.Length)]).ToArray());
            var link = "https://bon.tor-pos.de/r/" + token;
            string candidate = random.Next(4) switch
            {
                0 => link,
                1 => link.Insert(random.Next(link.Length + 1), RandomText(random, 3)),
                2 => link.Remove(random.Next(link.Length), 1),
                _ => RandomText(random, 80)
            };

            try
            {
                var safe = QrReceiptPayload.IsSafe(candidate);
                if (safe)
                {
                    valid++;
                    var uri = new Uri(candidate);
                    if (uri.Scheme != Uri.UriSchemeHttps || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
                        uri.UserInfo.Length > 0 || candidate.Contains('@') || candidate.Length > QrReceiptPayload.MaxLength ||
                        !DigitalReceiptLink.IsAcceptable(candidate))
                        failure = $"accepted unsafe payload at {i}: {candidate}";
                }
                else if (candidate == link)
                {
                    failure = $"rejected a valid link at {i}: {candidate}";
                }
            }
            catch (Exception ex)
            {
                failure = $"crash at {i}: {ex.GetType().Name}";
            }
        }

        assert(failure.Length == 0 && valid > 500,
            $"Property (seed {Seed}): 4000 mutated receipt links never crash, every valid link passes and nothing unsafe reaches a QR code {failure}");
    }

    private static void ExportRanges(Action<bool, string> assert)
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
        var random = new Random(Seed + 1);
        var failure = "";
        for (var i = 0; i < 2000 && failure.Length == 0; i++)
        {
            var from = new DateOnly(2024, 1, 1).AddDays(random.Next(1500));
            var to = from.AddDays(random.Next(120));
            var range = DsfinvkExportRange.ForDates(from, to, berlin);
            var next = DsfinvkExportRange.ForDates(to.AddDays(1), to.AddDays(1 + random.Next(40)), berlin);
            var localStart = TimeZoneInfo.ConvertTime(range.FromInclusive, berlin);
            if (range.FromInclusive > range.ToInclusive ||
                localStart.TimeOfDay != TimeSpan.Zero ||
                DateOnly.FromDateTime(localStart.DateTime) != from ||
                range.ToInclusive.AddTicks(1) != next.FromInclusive ||
                DsfinvkPreflightChecks.Range(range.FromInclusive, range.ToInclusive, range.ToInclusive.AddDays(1), berlin).Count != 0)
                failure = $"{from}..{to}";
        }

        var reversed = false;
        try { DsfinvkExportRange.ForDates(new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 1), berlin); }
        catch (ArgumentException) { reversed = true; }
        assert(failure.Length == 0 && reversed,
            $"Property (seed {Seed + 1}): 2000 DSFinV-K day ranges start at local midnight, abut the next range without gap or overlap across DST, and need no preflight warning {failure}");
    }

    private static void VatSummaries(Action<bool, string> assert)
    {
        var random = new Random(Seed + 2);
        var failure = "";
        for (var i = 0; i < 3000 && failure.Length == 0; i++)
        {
            var lines = Enumerable.Range(0, 1 + random.Next(8)).Select(_ => new CartLine
            {
                ProductName = "P",
                UnitPriceCents = random.Next(1, 20000),
                Quantity = random.Next(1, 6),
                VatRate = random.Next(3) switch { 0 => 7m, 1 => 19m, _ => 0m }
            }).ToList();
            var subtotal = lines.Sum(x => x.LineTotalCents);
            var discount = random.Next(3) == 0 ? 0 : random.NextInt64(0, subtotal + 1);
            var groups = VatSummaryCalculator.Compute(lines, discount);
            var target = ReceiptTotals.Total(subtotal, discount);
            if (groups.Sum(x => x.GrossCents) != target ||
                groups.Any(x => x.GrossCents < 0 || x.TaxCents < 0 || x.TaxCents > x.GrossCents) ||
                groups.Any(x => x.Rate == 0m && x.TaxCents != 0))
                failure = $"iteration {i}: subtotal {subtotal}, discount {discount}";
        }

        assert(failure.Length == 0,
            $"Property (seed {Seed + 2}): 3000 random receipts with discounts split into VAT groups that sum to the exact cent - no cent leaks {failure}");
    }

    private static void PartialReturns(Action<bool, string> assert)
    {
        var random = new Random(Seed + 3);
        var failure = "";
        for (var i = 0; i < 2000 && failure.Length == 0; i++)
        {
            var subtotal = random.NextInt64(1, 500000);
            var total = subtotal - random.NextInt64(0, subtotal + 1);
            var cash = random.NextInt64(0, total + 1);
            long raw = 0, returnedTotal = 0, returnedCash = 0, returnedCard = 0;
            while (raw < subtotal)
            {
                var part = Math.Min(subtotal - raw, random.NextInt64(1, Math.Max(2, subtotal / 3)));
                var allocation = CumulativeReturnProration.Allocate(raw, part, returnedTotal, returnedCash, subtotal, total, cash);
                if (allocation.TotalCents != allocation.CashCents + allocation.CardCents ||
                    allocation.RawCents != allocation.TotalCents + allocation.DiscountCents)
                {
                    failure = $"iteration {i}: fragment does not add up";
                    break;
                }

                raw += part;
                returnedTotal += allocation.TotalCents;
                returnedCash += allocation.CashCents;
                returnedCard += allocation.CardCents;
            }

            if (failure.Length == 0 && (returnedTotal != total || returnedCash != cash || returnedCard != total - cash))
                failure = $"iteration {i}: returned {returnedTotal}/{returnedCash}, sold {total}/{cash}";

            var overReturn = false;
            try { CumulativeReturnProration.Allocate(subtotal, 1, total, cash, subtotal, total, cash); }
            catch (InvalidOperationException) { overReturn = true; }
            if (failure.Length == 0 && !overReturn)
                failure = $"iteration {i}: a return beyond the sale was accepted";
        }

        assert(failure.Length == 0,
            $"Property (seed {Seed + 3}): 2000 random sequences of partial returns give back exactly the paid total and cash/card split, and never more {failure}");
    }

    private static void CsvReader(Action<bool, string> assert)
    {
        var random = new Random(Seed + 4);
        var failure = "";
        for (var i = 0; i < 3000 && failure.Length == 0; i++)
        {
            var bytes = new byte[random.Next(200)];
            random.NextBytes(bytes);
            if (random.Next(3) == 0)
                bytes = Encoding.UTF8.GetBytes("GRUPPE;WARENGRUPPE;ARTIKEL\n" + RandomText(random, 120));
            try
            {
                _ = BusinessManagementService.DecodeCsvText(bytes);
                _ = BusinessManagementService.TryParseCsvVat(RandomText(random, 6), out _);
            }
            catch (Exception ex)
            {
                failure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        var vatOk = BusinessManagementService.TryParseCsvVat("19", out var v19) && v19 == 19m &&
                    BusinessManagementService.TryParseCsvVat("7", out var v7) && v7 == 7m &&
                    !BusinessManagementService.TryParseCsvVat("16", out _) &&
                    !BusinessManagementService.TryParseCsvVat("", out _);
        assert(failure.Length == 0 && vatOk,
            $"Property (seed {Seed + 4}): 3000 random article-CSV inputs never crash the decoder or USt parser, and only real German rates are accepted {failure}");
    }

    private static void TseExportParser(Action<bool, string> assert)
    {
        var random = new Random(Seed + 5);
        var failure = "";
        for (var i = 0; i < 1500 && failure.Length == 0; i++)
        {
            var bytes = new byte[random.Next(2048)];
            random.NextBytes(bytes);
            if (random.Next(2) == 0 && bytes.Length > 262)
                Encoding.ASCII.GetBytes("ustar").CopyTo(bytes, 257);
            try
            {
                using var stream = new MemoryStream(bytes);
                var parsed = TseExportMasterDataReader.Read(stream);
                if (parsed.Any(x => string.IsNullOrEmpty(x.SerialNumber)))
                    failure = $"iteration {i}: master data without serial";
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or EndOfStreamException or ArgumentException)
            {
                // A rejected file is fine; a crash of another kind is not.
            }
            catch (Exception ex)
            {
                failure = $"iteration {i}: {ex.GetType().Name}";
            }
        }

        assert(failure.Length == 0,
            $"Property (seed {Seed + 5}): 1500 random TSE TAR exports are rejected cleanly or yield only master data with a serial {failure}");
    }

    private static void TseSerials(Action<bool, string> assert)
    {
        var random = new Random(Seed + 6);
        var failure = "";
        for (var i = 0; i < 2000 && failure.Length == 0; i++)
        {
            var serial = Convert.ToHexString(Enumerable.Range(0, 32).Select(_ => (byte)random.Next(256)).ToArray());
            var variant = random.Next(2) == 0 ? serial.ToLowerInvariant() : " " + serial + "\t";
            var a = new TseIdentity(TseProviderCatalog.SwissbitHardware, serial, "", "", "");
            var b = a with { SerialNumber = variant };
            var other = a with { SerialNumber = serial[..^1] + (serial[^1] == '0' ? '1' : '0') };
            if (TseChangeDetector.IsChange(a, b) || TseChangeDetector.IsChange(b, a) ||
                !TseChangeDetector.IsChange(a, other) || !TseChangeDetector.IsChange(other, a))
                failure = $"iteration {i}: {serial}";
        }

        assert(failure.Length == 0,
            $"Property (seed {Seed + 6}): 2000 TSE serials - case and whitespace never fake a TSE-Wechsel, one changed digit always is one {failure}");
    }
}
