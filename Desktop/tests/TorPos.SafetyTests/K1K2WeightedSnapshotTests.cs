using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class K1K2WeightedSnapshotTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var product = new Product
        {
            Id = 193_001,
            CategoryId = 193,
            Name = "K-1 Baklava",
            Unit = "kg",
            BasePriceCents = 1990,
            VatRate = 7m
        };

        var promotion = new PromotionSnapshot(
            193,
            "K-1 GEWICHT 10",
            10,
            "2026-09-01",
            "2026-09-30",
            PromotionScope.All,
            0);

        var engine = new SaleEngine();
        engine.Add(
            product,
            quantity: 0.500m,
            promotion: promotion);

        var allocated = MenuVatPolicy.ApplyAllocations(
                engine.Cart,
                new[] { product },
                imHaus: false)
            .Single();

        assert(
            allocated.Unit == "kg" &&
            allocated.IsWeighted &&
            allocated.ListLineTotalCents == 995 &&
            allocated.PromotionDiscountCents == 100 &&
            allocated.LineTotalCents == 895,
            "K-1 menu VAT snapshot copy preserves kg and the cent-exact 8.95 EUR weighted promotion total");

        var dir = Path.Combine(root, "k1-k2-weighted-snapshot");
        Directory.CreateDirectory(dir);

        var db = await SafetyDatabase.CreateCurrentAsync(
            Path.Combine(dir, "weighted-snapshot.db"));

        var unitTables = new[]
        {
            "sale_items",
            "parked_receipt_items",
            "training_receipt_items",
            "aborted_vorgang_items",
            "order_bestellung_items",
            "sale_cancelled_items",
            "training_cancelled_items"
        };

        var allUnitColumnsPresent = true;
        await using (var c = db.OpenConnection())
        {
            foreach (var table in unitTables)
            {
                await using var q = c.CreateCommand();
                q.CommandText = $"PRAGMA table_info({table});";

                var hasUnit = false;
                await using var r = await q.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    if (string.Equals(
                            r.GetString(1),
                            "unit",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        hasUnit = true;
                        break;
                    }
                }

                allUnitColumnsPresent &= hasUnit;
            }
        }

        assert(
            allUnitColumnsPresent,
            "K-1 R193 additive migration snapshots Unit on every shared CartLine persistence table");

        var initialSnapshot = new CheckoutSnapshot(
            Guid.NewGuid().ToString("N"),
            CheckoutSnapshot.CopyLines(new[] { allocated }),
            0,
            PaymentMethod.Cash,
            "k1-k2-test",
            null);

        var parkedRepository = new ParkedReceiptRepository(db);
        var parked = await parkedRepository.ParkAsync(
            initialSnapshot.Lines,
            0,
            "k1-k2-test");

        var reloadedPark = (await new ParkedReceiptRepository(db)
                .GetOpenAsync())
            .Single(x => x.Id == parked.Id);

        var parkedLine = reloadedPark.Lines.Single();

        assert(
            parkedLine.Unit == "kg" &&
            parkedLine.IsWeighted &&
            parkedLine.PersistedLineTotalCents == 895 &&
            parkedLine.LineTotalCents == 895,
            "K-1/K-2 parked receipt reload uses the stored kg unit and stored 8.95 EUR line total");

        var recalledSnapshot = CheckoutSnapshot.CopyLines(
            reloadedPark.Lines);

        assert(
            recalledSnapshot.Single().Unit == "kg" &&
            recalledSnapshot.Single().PersistedLineTotalCents == 895 &&
            recalledSnapshot.Single().LineTotalCents == 895,
            "K-2 checkout snapshot keeps a persisted fiscal line total when a parked receipt is recalled");

        var reversal = OrderBestellungDelta.WithQuantity(
            parkedLine,
            -0.500m);

        assert(
            reversal.Unit == "kg" &&
            reversal.PersistedLineTotalCents is null &&
            reversal.LineTotalCents == -895,
            "K-1 Bestellung quantity copy keeps kg while a changed quantity deliberately recalculates its new delta total");

        await InsertSaleFixtureAsync(
            db,
            receiptNumber: 193001,
            recalledSnapshot.Single(),
            storedLineTotalCents: 895);

        var loaded = await new SaleRepository(db).GetLastAsync()
            ?? throw new InvalidOperationException(
                "K-1/K-2 test sale was not reloaded.");

        var loadedLine = loaded.Lines.Single();
        var processData = FiscalProcessData.KassenbelegText(loaded);

        assert(
            loaded.TotalCents == 895 &&
            loadedLine.Unit == "kg" &&
            loadedLine.IsWeighted &&
            loadedLine.PersistedLineTotalCents == 895 &&
            loadedLine.LineTotalCents == 895 &&
            processData ==
                "Beleg^0.00_8.95_0.00_0.00_0.00^8.95:Bar",
            "K-1/K-2 19.90 EUR/kg + 10% + 0.500 kg survives snapshot -> storage -> reload -> KassenbelegText as kg and 8.95 EUR");

        // Independent K-2 proof: emulate a historical row whose stored gross
        // differs from what today's formula would calculate. Reload/reporting
        // must keep the immutable stored fiscal cents instead of recomputing.
        await InsertSaleFixtureAsync(
            db,
            receiptNumber: 193002,
            recalledSnapshot.Single(),
            storedLineTotalCents: 894);

        var historical = await new SaleRepository(db).GetLastAsync()
            ?? throw new InvalidOperationException(
                "K-2 historical test sale was not reloaded.");

        var historicalLine = historical.Lines.Single();
        var historicalProcessData =
            FiscalProcessData.KassenbelegText(historical);

        assert(
            historicalLine.LineTotalCentsFor(
                historicalLine.Quantity) == 895 &&
            historicalLine.PersistedLineTotalCents == 894 &&
            historicalLine.LineTotalCents == 894 &&
            historical.TotalCents == 894 &&
            historicalProcessData ==
                "Beleg^0.00_8.94_0.00_0.00_0.00^8.94:Bar",
            "K-2 stored line_total_cents is authoritative for historical reload and KassenbelegText even when recomputation would differ");
    }

    private static async Task InsertSaleFixtureAsync(
        SqliteDatabase db,
        long receiptNumber,
        CartLine line,
        long storedLineTotalCents)
    {
        await using var c = db.OpenConnection();
        await using var tx =
            (SqliteTransaction)await c.BeginTransactionAsync();

        long saleId;
        await using (var sale = c.CreateCommand())
        {
            sale.Transaction = tx;
            sale.CommandText = """
                INSERT INTO sales(
                    receipt_number,pickup_number,created_at,payment_method,
                    subtotal_cents,discount_cents,total_cents,fiscal_status,
                    list_subtotal_cents,promotion_discount_cents,
                    transaction_type,cash_portion_cents,card_portion_cents,
                    im_haus)
                VALUES(
                    $receipt,0,$created,'CASH',
                    $total,0,$total,'TEST_FIXTURE',
                    $list,$promotion,
                    'SALE',$total,0,0);
                SELECT last_insert_rowid();
                """;
            sale.Parameters.AddWithValue(
                "$receipt",
                receiptNumber);
            sale.Parameters.AddWithValue(
                "$created",
                DateTimeOffset.Now.ToString("O"));
            sale.Parameters.AddWithValue(
                "$total",
                storedLineTotalCents);
            sale.Parameters.AddWithValue(
                "$list",
                line.ListLineTotalCents);
            sale.Parameters.AddWithValue(
                "$promotion",
                Math.Max(
                    0L,
                    line.ListLineTotalCents -
                    storedLineTotalCents));

            saleId = Convert.ToInt64(
                await sale.ExecuteScalarAsync());
        }

        await using (var item = c.CreateCommand())
        {
            item.Transaction = tx;
            item.CommandText = """
                INSERT INTO sale_items(
                    sale_id,product_id,product_name,variant_name,barcode,
                    quantity,quantity_milli,unit_price_cents,vat_rate,
                    pfand_cents,line_total_cents,
                    list_unit_price_cents,list_line_total_cents,
                    promotion_id,promotion_name,promotion_percent,
                    promotion_discount_unit_cents,promotion_discount_cents,
                    promotion_start_date,promotion_end_date,
                    vat_allocations_json,menu_components_json,unit)
                VALUES(
                    $sale,$product,$name,$variant,$barcode,
                    $quantity,$quantityMilli,$unitPrice,$vat,
                    $pfand,$lineTotal,
                    $listUnit,$listTotal,
                    $promotionId,$promotionName,$promotionPercent,
                    $promotionUnit,$promotionTotal,
                    $promotionStart,$promotionEnd,
                    '', '', $unit);
                """;
            item.Parameters.AddWithValue("$sale", saleId);
            item.Parameters.AddWithValue("$product", line.ProductId);
            item.Parameters.AddWithValue("$name", line.ProductName);
            item.Parameters.AddWithValue("$variant", line.VariantName);
            item.Parameters.AddWithValue("$barcode", line.Barcode);
            item.Parameters.AddWithValue(
                "$quantity",
                Convert.ToDouble(line.Quantity));
            item.Parameters.AddWithValue(
                "$quantityMilli",
                QuantityStorage.ToMilli(line.Quantity));
            item.Parameters.AddWithValue(
                "$unitPrice",
                line.UnitPriceCents);
            item.Parameters.AddWithValue(
                "$vat",
                Convert.ToDouble(line.VatRate));
            item.Parameters.AddWithValue("$pfand", line.PfandCents);
            item.Parameters.AddWithValue(
                "$lineTotal",
                storedLineTotalCents);
            item.Parameters.AddWithValue(
                "$listUnit",
                line.EffectiveListUnitPriceCents);
            item.Parameters.AddWithValue(
                "$listTotal",
                line.ListLineTotalCents);
            item.Parameters.AddWithValue(
                "$promotionId",
                line.PromotionId);
            item.Parameters.AddWithValue(
                "$promotionName",
                line.PromotionName);
            item.Parameters.AddWithValue(
                "$promotionPercent",
                line.PromotionPercent);
            item.Parameters.AddWithValue(
                "$promotionUnit",
                line.PromotionDiscountUnitCents);
            item.Parameters.AddWithValue(
                "$promotionTotal",
                Math.Max(
                    0L,
                    line.ListLineTotalCents -
                    storedLineTotalCents));
            item.Parameters.AddWithValue(
                "$promotionStart",
                line.PromotionStartDate);
            item.Parameters.AddWithValue(
                "$promotionEnd",
                line.PromotionEndDate);
            item.Parameters.AddWithValue("$unit", line.Unit);

            await item.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}
