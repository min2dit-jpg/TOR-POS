using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R149: the PFAND / LEERGUT key takes back empties - money leaves the till.
//
// DSFinV-K Anhang C: "PfandRueckzahlung" documents "alle Rückgaben von
// Pfandgegenständen sowie die Verrechnung des Pfandbetrages oder die Auszahlung an
// den Kunden"; a bottle's deposit shares the rate of the drink (Warenumschließung),
// a crate is a Transporthilfsmittel at the general rate. 4.2.5: a negative position
// changes only the sign of MENGE. Until R149 the key sold deposit at a fixed 19 %,
// returned empties could not be recorded at all, and TOR Cloud rejected every
// sale event of a real till (discount field name, MIXED payment).
public static class R149ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        // ---------- the rate ----------
        assert(PfandProducts.RateFor(PfandProducts.Bottle25, reducedRateGoods: false) == 19m &&
               PfandProducts.RateFor(PfandProducts.Bottle25, reducedRateGoods: true) == 7m &&
               PfandProducts.RateFor(PfandProducts.CrateFull, reducedRateGoods: true) == 19m &&
               PfandProducts.IsCrate(PfandProducts.CrateEmpty) && !PfandProducts.IsCrate(PfandProducts.Bottle8),
            "R149 bottle deposit takes the rate of the drink (7 % for milk), a crate is always 19 % (Transporthilfsmittel)");

        // ---------- the cart ----------
        var engine = new SaleEngine();
        engine.Add(new Product { Id = 1, Name = "Cola 0,5l", BasePriceCents = 250, VatRate = 19m });
        var added = engine.AddDepositReturn(PfandProducts.Bottle25, "PFAND-RÜCKGABE · 25 CENT", 25, 19m, 10);
        engine.AddDepositReturn(PfandProducts.Bottle25, "PFAND-RÜCKGABE · 25 CENT", 25, 19m, 2);
        engine.SetDiscount(100);
        var deposit = engine.Cart.Single(PfandProducts.IsDepositReturn);
        var withDiscount = new SaleEngine();
        withDiscount.Add(new Product { Id = 1, Name = "Cola 0,5l", BasePriceCents = 250, VatRate = 19m });
        withDiscount.SetDiscount(50);
        assert(added && engine.Cart.Count == 2 && deposit is { Quantity: 12, UnitPriceCents: -25, LineTotalCents: -300, ImHausApplicable: false } &&
               engine.TotalCents == -50 && engine.HasDepositReturns && engine.DiscountCents == 0 &&
               !withDiscount.AddDepositReturn(PfandProducts.Bottle8, "PFAND-RÜCKGABE · 8 CENT", 8, 19m) &&
               CheckoutSnapshot.CopyLines(engine.Cart, imHaus: true).Single(PfandProducts.IsDepositReturn).VatRate == 19m,
            "R149 returned empties are a negative position (merged per deposit and rate), the total becomes a payout, no manual discount on the same receipt, Im Haus does not change the rate");

        assert(ReceiptTotals.Total(300, 500) == 0 && ReceiptTotals.Total(300, 100) == 200 && ReceiptTotals.Total(-50, 0) == -50,
            "R149 a discount never makes a purchase negative; returned deposit can make the receipt a payout");

        var lines = CheckoutSnapshot.CopyLines(engine.Cart);
        var cash = new CheckoutSnapshot("op-149", lines, 0, PaymentMethod.Cash, "kasse1", null);
        var mixed = new CheckoutSnapshot("op-149m", lines, 0, PaymentMethod.Mixed, "kasse1", null, CashPortionCents: 20);
        assert(cash.TotalCents == -50 && cash.EffectiveCashPortionCents == -50 && cash.EffectiveCardPortionCents == 0 &&
               mixed.EffectiveCashPortionCents == -50 && mixed.EffectiveCardPortionCents == 0,
            "R149 a payout is cash in full - the card portion of a negative receipt is always zero");

        // ---------- VAT and TSE data ----------
        var milk = new[]
        {
            new CartLine { ProductId = 1, ProductName = "Cola 0,5l", Quantity = 1, UnitPriceCents = 250, VatRate = 19m },
            new CartLine { ProductId = PfandProducts.Bottle25, ProductName = "PFAND-RÜCKGABE · 25 CENT", Quantity = 2, UnitPriceCents = -25, VatRate = 7m },
        };
        var groups = VatSummaryCalculator.Compute(milk, 0);
        var payoutOnly = VatSummaryCalculator.Compute(new[] { milk[1] }, 0);
        assert(groups.Single(g => g.Rate == 19m).GrossCents == 250 && groups.Single(g => g.Rate == 7m) is { GrossCents: -50, TaxCents: -3 } &&
               payoutOnly.Single().GrossCents == -50,
            "R149 returned deposit reduces the VAT of its own rate - a receipt made only of returned empties keeps its negative amount");

        var sale = new Sale { ReceiptNumber = 149001, PaymentMethod = PaymentMethod.Cash, TotalCents = -50, CashPortionCents = -50, Lines = lines };
        assert(FiscalProcessData.KassenbelegText(sale) == "Beleg^-0.50_0.00_0.00_0.00_0.00^-0.50:Bar" &&
               FiscalProcessData.BestellungText(new[] { deposit }) == "-12;\"PFAND-RÜCKGABE · 25 CENT\";0.25",
            "R149 the TSE data carry the payout (Kassenbeleg-V1) and, in an order, returned deposit with negative quantity (DSFinV-K 4.2.5)");

        // ---------- the till refuses what does not fit ----------
        var dir = Path.Combine(root, "r149-leergut");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r149.db"));
        var sales = new SaleRepository(db);
        async Task<string> Refusal(Func<Task> action)
        {
            try { await action(); return ""; }
            catch (InvalidOperationException ex) { return ex.Message; }
        }
        var cardRefusal = await Refusal(() => sales.CommitAsync(new CheckoutSnapshot("op-149-card", lines, 0, PaymentMethod.Card, "kasse1", null)));
        var discountRefusal = await Refusal(() => sales.CommitAsync(new CheckoutSnapshot("op-149-disc", lines, 10, PaymentMethod.Cash, "kasse1", null)));
        assert(cardRefusal.Contains("nur bar") && discountRefusal.Contains("Rabatt und Pfand-Rückgabe"),
            "R149 a payout is refused on card, and returned deposit together with a manual discount is refused, before anything is booked");

        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R149 Späti",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        long saleId, depositItemId;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents,im_haus) VALUES(149001,$at,'CASH',-50,-50,'TEST_FIXTURE','SALE',-50,0,0); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.Parameters.Clear();
            q.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Cola 0,5l',1,250,19,250),($s,$p,'PFAND-RÜCKGABE · 25 CENT',12,-25,19,-300); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$s", saleId);
            q.Parameters.AddWithValue("$p", PfandProducts.Bottle25);
            depositItemId = Convert.ToInt64(await q.ExecuteScalarAsync());
        }

        var returnRefusal = await Refusal(() => sales.RecordReturnAsync(saleId, new[] { new ReturnLineRequest(depositItemId, 1) }, "kasse1", "Test"));
        var controlled = new ControlledPosActionService(db);
        var negativeLogged = await Refusal(() => controlled.AppendAsync(new PosActionLogRequest
        {
            ActionId = "r149-action", Phase = "APPLIED", Actor = "kasse1", RegisterId = "1", OperationId = "op-149",
            ActionType = "LINE_STORNO", Reason = "Test", EntityType = "CURRENT_CART", EntityId = "",
            BeforeTotalCents = -50, AfterTotalCents = -300, AmountCents = 250, Details = ""
        }));
        assert(returnRefusal.Contains("Pfand-Rückgabe kann nicht retourniert") && negativeLogged == "",
            "R149 returned deposit cannot be handed back as a Retoure; the action log records a negative cart total as it was");

        // ---------- DSFinV-K ----------
        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var positions = Csv(Path.Combine(folder, "lines.csv")).Where(p => p["BON_ID"] == "149001").ToList();
        var cases = Csv(Path.Combine(folder, "businesscases.csv"));
        var payments = Csv(Path.Combine(folder, "payment.csv"));
        var depositRow = positions.Single(p => p["GV_TYP"] == "PfandRueckzahlung");
        assert(depositRow["MENGE"] == "-12,000" && depositRow["STK_BR"] == "0,25" &&
               positions.Single(p => p["GV_TYP"] == "Umsatz")["MENGE"] == "1,000" &&
               cases.Single(x => x["GV_TYP"] == "PfandRueckzahlung")["Z_UMS_BRUTTO"] == "-3,00" &&
               cases.Single(x => x["GV_TYP"] == "Umsatz")["Z_UMS_BRUTTO"] == "2,50" &&
               payments.Single(x => x["ZAHLART_TYP"] == "Bar")["Z_ZAHLART_BETRAG"] == "-0,50",
            "R149 the export shows returned deposit as PfandRueckzahlung with negative MENGE and positive unit price, and the closing the payout in cash");

        // ---------- TOR Cloud ----------
        var outbox = new TorCloudOutbox(db);
        await outbox.SaveConfigurationAsync(new TorCloudConfiguration("https://api.torpos.de/", "KASSE-1", "protected", true));
        await using (var c = db.OpenConnection())
        {
            await using var tx = (SqliteTransaction)await c.BeginTransactionAsync();
            TorCloudOutbox.EnqueueSale(c, tx, cash, 149001, 0, DateTimeOffset.Now);
            await tx.CommitAsync();
        }
        string sentPayload;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT payload FROM cloud_outbox WHERE event_type='sale.completed';";
            sentPayload = (string)(await q.ExecuteScalarAsync())!;
        }
        string? fixture = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && fixture is null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "Cloud", "tests", "fixtures", "sale-completed-kasse.json");
            if (File.Exists(candidate))
                fixture = candidate;
        }
        var expected = fixture is null ? null : JsonNode.Parse(await File.ReadAllTextAsync(fixture))?["payload"];
        assert(expected is not null && JsonNode.DeepEquals(JsonNode.Parse(sentPayload), expected),
            $"R149 the sale event the till queues is exactly Cloud/tests/fixtures/sale-completed-kasse.json, which TOR Cloud accepts - with discount_cents and a payout (found: {fixture ?? "no fixture"}, sent: {sentPayload})");

        // ---------- the digital receipt of a payout ----------
        var job = new ReceiptPrintJob(149001, DateTimeOffset.Now, "R149 Späti", "Hauptstraße 1, 10115 Berlin", "", "", "", "", "Bar", 0, -50, lines);
        var digital = DigitalReceiptDocument.From(job, DigitalReceiptDocument.PaymentsFor(PaymentMethod.Cash, -50, 0));
        assert(digital.TotalCents == -50 && digital.SubtotalCents == -50 && digital.Vat.Sum(v => v.GrossCents) == -50 &&
               digital.Payments.Single() == new DigitalReceiptPayment("Bar", -50),
            "R149 the digital receipt of a payout adds up the way TOR Cloud checks it");
    }

    /// <summary>Reads a DSFinV-K file: quoted fields may contain ";" and doubled quotes.</summary>
    private static List<Dictionary<string, string>> Csv(string path)
    {
        var rows = File.ReadAllText(path, Encoding.UTF8).Split("\r\n").Where(r => r.Length > 0).ToList();
        var header = Fields(rows[0]);
        return rows.Skip(1)
            .Select(Fields)
            .Select(cells => header.Select((name, i) => (name, value: i < cells.Count ? cells[i] : "")).ToDictionary(x => x.name, x => x.value))
            .ToList();
    }

    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ';') { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(ch);
        }

        fields.Add(field.ToString());
        return fields;
    }
}
