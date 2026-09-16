using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TorPos.Core;
using TorPos.Infrastructure;

// R131: DSFinV-K 2.4 export.
//
// The files are checked against the official index.xml of the BZSt (column
// order, text length, numeric form) and against each other the way an
// auditor's software reads them: header and positions of every Beleg add up,
// the closing totals equal the Belege, a Storno points at its original in the
// earlier closing, and the closing matches the printed Z-Bericht.
public static class R131ReviewTests
{
    // SHA-256 of index.xml and gdpdu-01-09-2004.dtd in dsfinv_k_v_2_4.zip (BZSt).
    private const string OfficialIndexSha256 = "d0b1fed31a50dc6370d7a54528034a1e0ac2f982f82e3c0a9250a15c64c85160";
    private const string OfficialDtdSha256 = "af3d4c5a19e991f2d8c53995bc708680bbd7ff9326fde539c55b7e2c63f848a2";

    public static async Task Run(string root, Action<bool, string> assert)
    {
        var tables = DsfinvkExportService.OfficialTables;
        assert(tables.Count == 20 &&
               tables.Single(t => t.Name == "Bonkopf").Columns.Count == 23 &&
               tables.All(t => t.Columns.Take(3).Select(c => c.Name).SequenceEqual(new[] { "Z_KASSE_ID", "Z_ERSTELLUNG", "Z_NR" })) &&
               tables.Single(t => t.Name == "Bonpos").Column("MENGE").Accuracy == 3 &&
               tables.Single(t => t.Name == "Bonkopf").Column("BON_ID").MaxLength == 40,
            "R131 the layout comes from the official index.xml: 20 tables, the three Z_ keys first, lengths and accuracies as published");

        var dir = Path.Combine(root, "r131-dsfinvk");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r131.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var sales = new SaleRepository(db);
        var orders = new ParkedReceiptRepository(db);
        var management = new BusinessManagementService(db, settings, audit);
        var cash = new CashMovementRepository(db, audit);
        var outages = new TseOutageRepository(db, audit);
        var provider = new FakeTseProvider();
        var tse = new TseFailSafeService(provider, outages, audit);
        var saleSigning = new SaleFiscalSigningService(tse, settings, sales);
        var orderSigning = new OrderFiscalSigningService(tse, settings, orders);
        var export = new DsfinvkExportService(db, settings);

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "Imbiss \"Sonne\"; Berlin",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["company.vat_id"] = "DE123456789",
            ["tse.status"] = "AKTIV",
            ["tse.client_id"] = "KASSE-1",
        });

        var receipt = 131000L;
        Task<Sale> SaleAsync(string method, long cashCents, long discountCents, params CartLine[] lines) =>
            RecordAsync("SALE", null, method, cashCents, discountCents, lines);

        // Storno and Retoure are written the way SaleRepository.RecordStornoAsync /
        // RecordReturnAsync store them (positive amounts, transaction_type,
        // original_sale_id). Those methods themselves refuse to run while the
        // fiscal circuit breaker is off, which it stays in every test (see R80).
        async Task<Sale> RecordAsync(string type, long? originalId, string method, long cashCents, long discountCents, params CartLine[] lines)
        {
            await Task.Delay(15);
            var subtotal = lines.Sum(l => l.LineTotalCents);
            var total = subtotal - discountCents;
            long id;
            await using (var c = db.OpenConnection())
            {
                await using var q = c.CreateCommand();
                q.CommandText = """
                    INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,discount_cents,total_cents,fiscal_status,
                                      transaction_type,original_sale_id,cash_portion_cents,card_portion_cents,list_subtotal_cents,promotion_discount_cents)
                    VALUES($r,$at,$m,$sub,$disc,$total,'TEST_FIXTURE',$type,$orig,$cash,$card,$list,$promo);
                    SELECT last_insert_rowid();
                    """;
                q.Parameters.AddWithValue("$r", ++receipt);
                q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
                q.Parameters.AddWithValue("$m", method);
                q.Parameters.AddWithValue("$sub", subtotal);
                q.Parameters.AddWithValue("$disc", discountCents);
                q.Parameters.AddWithValue("$total", total);
                q.Parameters.AddWithValue("$cash", cashCents);
                q.Parameters.AddWithValue("$card", total - cashCents);
                q.Parameters.AddWithValue("$list", lines.Sum(l => l.ListLineTotalCents));
                q.Parameters.AddWithValue("$promo", lines.Sum(l => l.PromotionDiscountCents));
                q.Parameters.AddWithValue("$type", type);
                q.Parameters.AddWithValue("$orig", originalId is long original ? original : DBNull.Value);
                id = Convert.ToInt64(await q.ExecuteScalarAsync());

                foreach (var line in lines)
                {
                    await using var item = c.CreateCommand();
                    item.CommandText = """
                        INSERT INTO sale_items(sale_id,product_id,product_name,variant_name,barcode,quantity,unit_price_cents,vat_rate,pfand_cents,
                                               line_total_cents,list_unit_price_cents,list_line_total_cents,promotion_id,promotion_name,
                                               promotion_percent,promotion_discount_unit_cents,promotion_discount_cents)
                        VALUES($s,1,$n,$v,$b,$q,$u,$vat,$p,$t,$lu,$lt,$pid,$pn,$pp,$pdu,$pd);
                        """;
                    item.Parameters.AddWithValue("$s", id);
                    item.Parameters.AddWithValue("$n", line.ProductName);
                    item.Parameters.AddWithValue("$v", line.VariantName);
                    item.Parameters.AddWithValue("$b", line.Barcode);
                    item.Parameters.AddWithValue("$q", (double)line.Quantity);
                    item.Parameters.AddWithValue("$u", line.UnitPriceCents);
                    item.Parameters.AddWithValue("$vat", (double)line.VatRate);
                    item.Parameters.AddWithValue("$p", line.PfandCents);
                    item.Parameters.AddWithValue("$t", line.LineTotalCents);
                    item.Parameters.AddWithValue("$lu", line.EffectiveListUnitPriceCents);
                    item.Parameters.AddWithValue("$lt", line.ListLineTotalCents);
                    item.Parameters.AddWithValue("$pid", line.PromotionId);
                    item.Parameters.AddWithValue("$pn", line.PromotionName);
                    item.Parameters.AddWithValue("$pp", line.PromotionPercent);
                    item.Parameters.AddWithValue("$pdu", line.PromotionDiscountUnitCents);
                    item.Parameters.AddWithValue("$pd", line.PromotionDiscountCents);
                    await item.ExecuteNonQueryAsync();
                }

                await using var op = c.CreateCommand();
                op.CommandText = "INSERT INTO sale_operators(sale_id,operator_name) VALUES($s,'kasse1');";
                op.Parameters.AddWithValue("$s", id);
                await op.ExecuteNonQueryAsync();
            }

            var sale = (await sales.GetByIdAsync(id))!;
            await saleSigning.SignAsync(sale, "kasse1");
            return sale;
        }

        // ---------- closing 1 ----------
        var sale1 = await SaleAsync("CASH", 1975, 0,
            new CartLine { ProductName = "Döner\nscharf", VariantName = "groß", Quantity = 2, UnitPriceCents = 850, VatRate = 7m, Barcode = "4001234567890" },
            new CartLine { ProductName = "Cola", Quantity = 1, UnitPriceCents = 275, PfandCents = 25, VatRate = 19m });
        provider.FailStart = true;
        var sale2 = await SaleAsync("CARD", 0, 140,
            new CartLine { ProductName = "Pizza", Quantity = 1, UnitPriceCents = 800, ListUnitPriceCents = 1000, VatRate = 19m,
                           PromotionId = 5, PromotionName = "Pizza-Woche", PromotionPercent = 20, PromotionDiscountUnitCents = 200 },
            new CartLine { ProductName = "Wasser", Quantity = 3, UnitPriceCents = 200, VatRate = 7m });
        provider.FailStart = false;
        var sale4 = await SaleAsync("CASH", 800, 0, new CartLine { ProductName = "Pommes", Quantity = 2, UnitPriceCents = 400, VatRate = 7m });
        await Task.Delay(15);
        // R134: a fiscal cash movement from before R134 - no business case,
        // no TSE result. (AddAsync refuses production entries while the fiscal
        // circuit breaker is off, so it is written directly.)
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode) VALUES($at,'ENTNAHME',5000,'Bankeinzahlung','kasse1','PRODUCTION');";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            await q.ExecuteNonQueryAsync();
        }
        await Task.Delay(15);
        var order = await orders.ParkAsync(new[] { new CartLine { ProductName = "Burger", Quantity = 1, UnitPriceCents = 1100, VatRate = 19m } }, 0, "kasse1", assignPickupNumber: true);
        await orderSigning.SignAsync(order, "kasse1");
        var sale6 = await SaleAsync("CARD", 0, 0, new CartLine { ProductName = "Burger", Quantity = 1, UnitPriceCents = 1100, VatRate = 19m });
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE parked_receipts SET status='CASHED',cashed_sale_id=$s WHERE id=$id;";
            q.Parameters.AddWithValue("$s", sale6.Id);
            q.Parameters.AddWithValue("$id", order.Id);
            await q.ExecuteNonQueryAsync();
        }
        await Task.Delay(15);
        var z1 = await management.CreateZArchiveAsync("kasse1", "TEST");

        // ---------- closing 2 ----------
        await Task.Delay(15);
        var storno = await RecordAsync("STORNO", sale1.Id, "CASH", 1975, 0, sale1.Lines.ToArray());
        var retoure = await RecordAsync("RETURN", sale4.Id, "CASH", 400, 0,
            new CartLine { ProductName = "Pommes", Quantity = 1, UnitPriceCents = 400, VatRate = 7m });
        var sale3 = await SaleAsync("MIXED", 500, 0, new CartLine { ProductName = "Menü", Quantity = 1, UnitPriceCents = 1200, VatRate = 19m });
        await Task.Delay(15);
        var z2 = await management.CreateZArchiveAsync("kasse1", "TEST");

        // after the last closing - not exported yet
        await SaleAsync("CASH", 300, 0, new CartLine { ProductName = "Ayran", Quantity = 1, UnitPriceCents = 300, VatRate = 7m });

        // ---------- validation ----------
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await export.ValidateAsync(from, to);
        var codes = report.Issues.Select(x => x.Code).ToHashSet();
        assert(report.Ready && report.Issues.All(x => !x.Blocking),
            $"R131 a closed period with complete company data is exportable (blocking: {string.Join(" | ", report.Issues.Where(x => x.Blocking).Select(x => x.Message))})");
        assert(new[] { "BON_START", "TRAINING", "KASSENBEWEGUNG", "TSE_STAMMDATEN", "BESTELLUNG", "INHAUS" }.All(codes.Contains) &&
               report.Issues.Single(x => x.Code == "OPEN_PERIOD").Message.StartsWith("1 "),
            "R131 what TOR does not record yet is reported, not hidden - and the one sale after the last closing is named as not included");

        var target = Path.Combine(dir, "out-de");
        var folder = await export.ExportAsync(from, to, target);
        var written = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        assert(written.Length == 23 &&
               tables.All(t => written.Contains(t.FileName)) &&
               written.Contains("index.xml") && written.Contains("gdpdu-01-09-2004.dtd") && written.Contains(DsfinvkExportService.ProtocolFileName) &&
               Directory.GetDirectories(target).Length == 1,
            $"R131 the export folder holds the 20 CSV files, index.xml, the DTD and the protocol, and no unfinished working folder (files: {string.Join(", ", written)})");

        assert(Sha256File(Path.Combine(folder, "index.xml")) == OfficialIndexSha256 &&
               Sha256File(Path.Combine(folder, "gdpdu-01-09-2004.dtd")) == OfficialDtdSha256,
            "R131 index.xml and the GDPdU DTD are the files published by the BZSt, byte for byte");

        // ---------- every file against index.xml ----------
        var data = new Dictionary<string, List<Dictionary<string, string>>>();
        var formatErrors = new List<string>();
        foreach (var table in tables)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, table.FileName));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                formatErrors.Add($"{table.FileName}: BOM");
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.EndsWith("\r\n"))
                formatErrors.Add($"{table.FileName}: last record not ended by CR LF");
            var records = text.Split("\r\n");
            if (records.Any(r => r.Contains('\r') || r.Contains('\n')))
                formatErrors.Add($"{table.FileName}: stray line break");
            if (records[0] != string.Join(";", table.Columns.Select(c => c.Name)))
                formatErrors.Add($"{table.FileName}: header does not match index.xml");

            var rows = new List<Dictionary<string, string>>();
            foreach (var record in records.Skip(1).Where(r => r.Length > 0))
            {
                var fields = ParseRecord(record);
                if (fields.Count != table.Columns.Count)
                {
                    formatErrors.Add($"{table.FileName}: {fields.Count} fields instead of {table.Columns.Count}");
                    continue;
                }

                var row = new Dictionary<string, string>();
                for (var i = 0; i < fields.Count; i++)
                {
                    var column = table.Columns[i];
                    var value = fields[i];
                    row[column.Name] = value;
                    if (column.Kind == DsfinvkColumnKind.AlphaNumeric && column.MaxLength > 0 && value.Length > column.MaxLength)
                        formatErrors.Add($"{table.FileName}.{column.Name}: {value.Length} > {column.MaxLength}");
                    if (column.Kind == DsfinvkColumnKind.Numeric && value.Length > 0)
                    {
                        var decimals = column.Accuracy == 0 ? "" : column.Accuracy == 3 ? ",\\d{3}" : ",\\d{2}";
                        if (!Regex.IsMatch(value, $"^-?\\d+{decimals}$"))
                            formatErrors.Add($"{table.FileName}.{column.Name}: '{value}' is not Numeric({column.Accuracy})");
                    }
                }

                if (row["Z_KASSE_ID"].Length == 0 || row["Z_ERSTELLUNG"].Length == 0 || row["Z_NR"].Length == 0)
                    formatErrors.Add($"{table.FileName}: record without Z_ keys");
                rows.Add(row);
            }

            data[table.Name] = rows;
        }

        assert(formatErrors.Count == 0,
            "R131 every file matches index.xml: UTF-8 without BOM, CR LF, header, field count, text length, numeric form, Z_ keys everywhere" +
            (formatErrors.Count > 0 ? " - " + string.Join(" | ", formatErrors.Take(8)) : ""));

        decimal M(string value) => value.Length == 0 ? 0m : decimal.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
        List<Dictionary<string, string>> T(string name) => data[name];
        string Bon(Sale s) => s.ReceiptNumber.ToString(CultureInfo.InvariantCulture);
        string Z(long number) => number.ToString(CultureInfo.InvariantCulture);

        var heads = T("Bonkopf");
        var headIds = heads.Select(h => h["BON_ID"]).ToList();
        assert(headIds.Count == 9 &&
               new[] { Bon(sale1), Bon(sale2), Bon(sale4), Bon(sale6), Bon(storno), Bon(retoure), Bon(sale3) }.All(headIds.Contains) &&
               headIds.Count(id => id.StartsWith("KB-")) == 1 && headIds.Count(id => id.StartsWith("BE-")) == 1 &&
               !headIds.Contains((receipt).ToString(CultureInfo.InvariantCulture)),
            $"R131 the two closings hold all nine Vorgänge (6 sales incl. Storno/Retoure, 1 Entnahme, 1 order) and not the sale after the last closing (actual: {string.Join(",", headIds)})");

        var stornoHead = heads.Single(h => h["BON_ID"] == Bon(storno));
        var retoureHead = heads.Single(h => h["BON_ID"] == Bon(retoure));
        assert(stornoHead["BON_TYP"] == "Beleg" && stornoHead["BON_STORNO"] == "1" && stornoHead["UMS_BRUTTO"] == "-19,75" &&
               retoureHead["BON_TYP"] == "Beleg" && retoureHead["BON_STORNO"] == "0" && retoureHead["UMS_BRUTTO"] == "-4,00",
            "R131 a Storno is a Beleg with BON_STORNO 1 and reversed amount, a Retoure a Beleg with negative amount (DSFinV-K 4.2.2, 4.2.5)");

        var references = T("Bon_Referenzen");
        var z1Stamp = DsfinvkCsv.Timestamp(z1.CreatedAt);
        assert(references.Count == 2 &&
               references.Any(r => r["BON_ID"] == Bon(storno) && r["REF_TYP"] == "Transaktion" && r["REF_Z_NR"] == Z(z1.ZNumber) && r["REF_BON_ID"] == Bon(sale1) && r["REF_DATUM"] == z1Stamp && r["Z_NR"] == Z(z2.ZNumber)) &&
               references.Any(r => r["BON_ID"] == Bon(retoure) && r["REF_BON_ID"] == Bon(sale4) && r["REF_Z_NR"] == Z(z1.ZNumber)),
            "R131 Storno and Retoure in closing 2 reference their original receipts in closing 1 (Bon_Referenzen, Transaktion)");

        var mismatches = new List<string>();
        foreach (var head in heads)
        {
            var id = head["BON_ID"];
            var z = head["Z_NR"];
            var total = M(head["UMS_BRUTTO"]);
            var vat = T("Bonkopf_USt").Where(r => r["BON_ID"] == id && r["Z_NR"] == z).Sum(r => M(r["BON_BRUTTO"]));
            var positions = T("Bonpos_USt").Where(r => r["BON_ID"] == id && r["Z_NR"] == z).Sum(r => M(r["POS_BRUTTO"]));
            var vatSplit = T("Bonkopf_USt").Where(r => r["BON_ID"] == id).All(r => M(r["BON_NETTO"]) + M(r["BON_UST"]) == M(r["BON_BRUTTO"]));
            if (vat != total || positions != total || !vatSplit)
                mismatches.Add($"{id}: head {total}, vat {vat}, positions {positions}");
            if (head["BON_TYP"] == "Beleg")
            {
                var paid = T("Bonkopf_Zahlarten").Where(r => r["BON_ID"] == id && r["Z_NR"] == z).Sum(r => M(r["BASISWAEH_BETRAG"]));
                if (paid != total)
                    mismatches.Add($"{id}: head {total}, paid {paid}");
            }
        }
        assert(mismatches.Count == 0,
            "R131 for every Vorgang the header, its VAT split, its positions and its payments add up to the same amount" +
            (mismatches.Count > 0 ? " - " + string.Join(" | ", mismatches) : ""));

        var positions1 = T("Bonpos").Where(p => p["BON_ID"] == Bon(sale1)).ToList();
        var pos1Vat = T("Bonpos_USt").Where(p => p["BON_ID"] == Bon(sale1)).ToList();
        assert(positions1.Count == 3 &&
               positions1.Any(p => p["ARTIKELTEXT"] == "Döner scharf · groß" && p["MENGE"] == "2,000" && p["STK_BR"] == "8,50" && p["GTIN"] == "4001234567890") &&
               positions1.Any(p => p["GV_TYP"] == "Pfand" && p["ARTIKELTEXT"] == "Pfand Cola") &&
               pos1Vat.Any(p => p["UST_SCHLUESSEL"] == "1" && p["POS_BRUTTO"] == "2,50") &&
               pos1Vat.Any(p => p["UST_SCHLUESSEL"] == "1" && p["POS_BRUTTO"] == "0,25"),
            "R131 positions keep their text (line break removed), quantity, unit price and GTIN; Pfand is its own position at the article's rate (Anhang C)");

        var pricing = T("Bonpos_Preisfindung").Where(p => p["BON_ID"] == Bon(sale2)).ToList();
        assert(pricing.Count == 2 &&
               pricing.Any(p => p["TYP"] == "base_amount" && p["PF_BRUTTO"] == "10,00") &&
               pricing.Any(p => p["TYP"] == "discount" && p["PF_BRUTTO"] == "-2,00"),
            "R131 an Angebot price is shown with its base amount and the discount in Bonpos_Preisfindung (4.2.4)");

        var discountRow = T("Bonpos").Single(p => p["BON_ID"] == Bon(sale2) && p["GV_TYP"] == "Rabatt");
        var discountVat = T("Bonpos_USt").Where(p => p["BON_ID"] == Bon(sale2) && p["POS_ZEILE"] == discountRow["POS_ZEILE"]).ToList();
        assert(discountVat.Count == 2 &&
               discountVat.Single(p => p["UST_SCHLUESSEL"] == "1")["POS_BRUTTO"] == "-0,80" &&
               discountVat.Single(p => p["UST_SCHLUESSEL"] == "2")["POS_BRUTTO"] == "-0,60",
            "R131 a manual discount on the whole receipt is a Rabatt position split onto both VAT rates exactly as the receipt prorates it");

        var tseRows = T("TSE_Transaktionen");
        var tse1 = tseRows.Single(t => t["BON_ID"] == Bon(sale1));
        var tse2 = tseRows.Single(t => t["BON_ID"] == Bon(sale2));
        var tseStorno = tseRows.Single(t => t["BON_ID"] == Bon(storno));
        assert(tse1["TSE_ID"] == "1" && tse1["TSE_TANR"] == "42" && tse1["TSE_TA_SIGZ"] == "2" && tse1["TSE_TA_VORGANGSART"] == "Kassenbeleg-V1" &&
               Regex.IsMatch(tse1["TSE_TA_ENDE"], @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$") &&
               tse1["TSE_VORGANGSDATEN"] == "Beleg^2.75_17.00_0.00_0.00_0.00^19.75:Bar" &&
               tseStorno["TSE_VORGANGSDATEN"] == "Beleg^-2.75_-17.00_0.00_0.00_0.00^-19.75:Bar",
            "R131 a signed Beleg carries its TSE transaction, the log time in the Anhang E format and exactly the Anhang I processData that was signed");
        assert(tse2["TSE_TANR"] == "" && tse2["TSE_TA_FEHLER"] == "TSE-Ausfall: Fake TSE Start failed" &&
               tseRows.Single(t => t["BON_ID"].StartsWith("KB-"))["TSE_TA_FEHLER"] == "Kein TSE-Ergebnis gespeichert",
            "R131 a Beleg from a TSE outage is listed with the documented reason in TSE_TA_FEHLER, neither left out nor signed afterwards (R129); a cash movement without TSE result says so");

        var orderHead = heads.Single(h => h["BON_ID"].StartsWith("BE-"));
        var groups = T("Bonkopf_AbrKreis");
        assert(orderHead["BON_TYP"] == "AVBestellung" &&
               T("Bonkopf_Zahlarten").Single(p => p["BON_ID"] == orderHead["BON_ID"])["ZAHLART_TYP"] == "Keine" &&
               tseRows.Single(t => t["BON_ID"] == orderHead["BON_ID"])["TSE_TA_VORGANGSART"] == "Bestellung-V1" &&
               groups.Single(g => g["BON_ID"] == orderHead["BON_ID"])["ABRECHNUNGSKREIS"] == groups.Single(g => g["BON_ID"] == Bon(sale6))["ABRECHNUNGSKREIS"],
            "R131 a signed order is an AVBestellung without payment, linked to the receipt it was paid with through the same Abrechnungskreis (2.7.1)");

        assert(T("Stamm_TSE").Any(s => s["Z_NR"] == Z(z1.ZNumber) && s["TSE_ID"] == "1" && s["TSE_SERIAL"] == "FAKE-SERIAL" && s["TSE_PD_ENCODING"] == "UTF-8") &&
               T("Stamm_USt").Where(s => s["Z_NR"] == Z(z1.ZNumber)).Select(s => s["UST_SCHLUESSEL"] + "=" + s["UST_SATZ"]).OrderBy(x => x, StringComparer.Ordinal)
                   .SequenceEqual(new[] { "1=19,00", "2=7,00", "5=0,00" }) &&
               T("Stamm_Abschluss").Single(s => s["Z_NR"] == Z(z1.ZNumber))["NAME"] == "Imbiss \"Sonne\"; Berlin" &&
               T("Stamm_Kassen").All(s => s["KASSE_SERIENNR"].StartsWith("TORPOS-") && s["KASSE_BASISWAEH_CODE"] == "EUR"),
            "R131 master data per closing: the TSE by serial, the VAT keys of Anlage 2 that were used, company name with quote and semicolon intact, the eAS serial as till serial");

        var closingErrors = new List<string>();
        foreach (var z in new[] { z1, z2 })
        {
            var zn = Z(z.ZNumber);
            var master = T("Stamm_Abschluss").Single(s => s["Z_NR"] == zn);
            var belegIds = heads.Where(h => h["Z_NR"] == zn && h["BON_TYP"] == "Beleg").Select(h => h["BON_ID"]).ToHashSet();
            var fromPositions = T("Bonpos_USt").Where(p => p["Z_NR"] == zn && belegIds.Contains(p["BON_ID"])).Sum(p => M(p["POS_BRUTTO"]));
            var gv = T("Z_GV_Typ").Where(g => g["Z_NR"] == zn).Sum(g => M(g["Z_UMS_BRUTTO"]));
            var payments = T("Z_Zahlart").Where(p => p["Z_NR"] == zn).Sum(p => M(p["Z_ZAHLART_BETRAG"]));
            var currency = T("Z_Waehrungen").Single(w => w["Z_NR"] == zn);
            if (gv != fromPositions || gv != M(master["Z_SE_ZAHLUNGEN"]) || payments != M(master["Z_SE_ZAHLUNGEN"]) ||
                currency["ZAHLART_WAEH"] != "EUR" || M(currency["ZAHLART_BETRAG_WAEH"]) != M(master["Z_SE_BARZAHLUNGEN"]))
                closingErrors.Add($"Z {zn}: gv {gv}, positions {fromPositions}, payments {payments}, master {master["Z_SE_ZAHLUNGEN"]}/{master["Z_SE_BARZAHLUNGEN"]}, cash {currency["ZAHLART_BETRAG_WAEH"]}");
        }
        assert(closingErrors.Count == 0,
            "R131 each closing's business-case totals, payment totals and cash per currency equal the Belege it contains (4.1)" +
            (closingErrors.Count > 0 ? " - " + string.Join(" | ", closingErrors) : ""));

        var z1Master = T("Stamm_Abschluss").Single(s => s["Z_NR"] == Z(z1.ZNumber));
        var z2Master = T("Stamm_Abschluss").Single(s => s["Z_NR"] == Z(z2.ZNumber));
        assert(z1Master["Z_SE_ZAHLUNGEN"] == "1,35" && z1Master["Z_SE_BARZAHLUNGEN"] == "-22,25" &&
               z2Master["Z_SE_ZAHLUNGEN"] == "-11,75" && z2Master["Z_SE_BARZAHLUNGEN"] == "-18,75",
            $"R131 closing totals by hand: Z1 19,75 + 12,60 + 8,00 + 11,00 - 50,00 = 1,35 (cash -22,25); Z2 -19,75 - 4,00 + 12,00 = -11,75 (cash -18,75) (actual {z1Master["Z_SE_ZAHLUNGEN"]}/{z1Master["Z_SE_BARZAHLUNGEN"]}, {z2Master["Z_SE_ZAHLUNGEN"]}/{z2Master["Z_SE_BARZAHLUNGEN"]})");

        var printed = new Dictionary<long, long>();
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT z_number,gross_cents FROM z_report_archive;";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync())
                printed[r.GetInt64(0)] = r.GetInt64(1);
        }
        bool MatchesPrinted(long zNumber) =>
            T("Z_GV_Typ").Where(g => g["Z_NR"] == Z(zNumber) && g["GV_TYP"] is not ("Einzahlung" or "Auszahlung")).Sum(g => M(g["Z_UMS_BRUTTO"])) * 100m == printed[zNumber];
        decimal Exported(long zNumber) =>
            T("Z_GV_Typ").Where(g => g["Z_NR"] == Z(zNumber) && g["GV_TYP"] is not ("Einzahlung" or "Auszahlung")).Sum(g => M(g["Z_UMS_BRUTTO"]));
        // gross_cents is the printed "Umsatz nach Storno/Retouren". Closing 2
        // has more Storno and Retoure (23,75) than sales (12,00): before R131
        // the Z-Bericht clamped that to 0,00.
        assert(MatchesPrinted(z1.ZNumber) && MatchesPrinted(z2.ZNumber) && printed[z2.ZNumber] == -1175,
            $"R131 the exported turnover of each closing equals the printed Z-Bericht, also when Storno and Retoure exceed the sales of the period (Z1 printed {printed[z1.ZNumber]} exported {Exported(z1.ZNumber)}, Z2 printed {printed[z2.ZNumber]} exported {Exported(z2.ZNumber)})");

        // ---------- same bytes on a Turkish Windows ----------
        var previous = CultureInfo.CurrentCulture;
        string turkishFolder;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            await Task.Delay(1100); // folder name carries the second
            turkishFolder = await export.ExportAsync(from, to, Path.Combine(dir, "out-tr"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        assert(tables.All(t => Sha256File(Path.Combine(folder, t.FileName)) == Sha256File(Path.Combine(turkishFolder, t.FileName))),
            "R131 the export is byte-identical whatever the Windows language");

        // ---------- what blocks ----------
        // R132: master data can no longer change while Vorgänge wait for a
        // closing, so a closing without a tax id is built in its own till.
        var bareDb = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r131-no-tax-id.db"));
        var bareSettings = new SettingsRepository(bareDb);
        await bareSettings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "Ohne Steuernummer",
            ["company.street"] = "Weg 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
        });
        await using (var c = bareDb.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents) VALUES(1,$at,'CASH',100,100,'TEST_FIXTURE','SALE',100); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            await using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Tee',1,100,19,100);";
            item.Parameters.AddWithValue("$s", id);
            await item.ExecuteNonQueryAsync();
        }
        await Task.Delay(15);
        await new BusinessManagementService(bareDb, bareSettings, new AuditLogRepository(bareDb)).CreateZArchiveAsync("kasse1", "TEST");
        var bareExport = new DsfinvkExportService(bareDb, bareSettings);
        var noTaxId = await bareExport.ValidateAsync(from, DateTimeOffset.Now.AddMinutes(1));
        var refused = false;
        try { await bareExport.ExportAsync(from, DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out-refused")); }
        catch (InvalidOperationException) { refused = true; }
        assert(!noTaxId.Ready && noTaxId.Issues.Any(x => x.Blocking && x.Code == "MASTER_DATA") && refused && !Directory.Exists(Path.Combine(dir, "out-refused")),
            "R131 without Steuernummer or USt-IdNr. (§ 14 Abs. 4 Nr. 2 UStG) the export is refused and nothing is written");

        var noClosing = await export.ValidateAsync(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2000, 12, 31, 0, 0, 0, TimeSpan.Zero));
        assert(!noClosing.Ready && noClosing.Issues.Any(x => x.Blocking && x.Code == "NO_CLOSING"),
            "R131 a period without a Kassenabschluss has nothing exportable and says so");

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents) VALUES(139999,$at,'CASH',116,116,'TEST_FIXTURE','SALE',116); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            await using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Alt',1,116,16,116);";
            item.Parameters.AddWithValue("$s", id);
            await item.ExecuteNonQueryAsync();
        }
        await Task.Delay(15);
        await management.CreateZArchiveAsync("kasse1", "TEST");
        var oldRate = await export.ValidateAsync(from, DateTimeOffset.Now.AddMinutes(1));
        assert(!oldRate.Ready && oldRate.Issues.Any(x => x.Blocking && x.Code == "VAT" && x.Message.Contains("139999") && x.Message.Contains("16")),
            "R131 a VAT rate without a DSFinV-K key blocks the export and names the receipt, instead of a guessed key");
    }

    private static List<string> ParseRecord(string line)
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

    private static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class FakeTseProvider : ITseProvider
    {
        public bool FailStart;

        public string ProviderId => "FAKE";
        public string DisplayName => "Fake TSE";
        public string PreferredProduct => "Fake";
        public bool SdkAvailable => true;
        public bool ActivationAvailable => true;
        public bool TransactionAvailable => true;
        public bool ExportAvailable => true;

        public TseRuntimeStatus GetRuntimeStatus() => new(true, true, "1.0", "", "OK");

        public Task<IReadOnlyList<string>> FindSdkLibrariesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public TseRuntimeStatus ConfigureSdkLibrary(string libraryPath) => GetRuntimeStatus();

        public Task<TseProbeResult> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new TseProbeResult(TseConnectionState.Ready, "OK"));

        public Task<TseActivationResult> ActivateAsync(TseActivationRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseActivationResult(true, "OK"));

        public Task<TseTransactionResult> StartTransactionAsync(TseTransactionStartRequest request, CancellationToken ct = default) =>
            Task.FromResult(FailStart
                ? new TseTransactionResult(false, "Fake TSE Start failed")
                : new TseTransactionResult(true, "OK", 42, 1, DateTimeOffset.UtcNow, "FAKE-SERIAL", "c3RhcnQ="));

        public Task<TseTransactionResult> UpdateTransactionAsync(TseTransactionUpdateRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TseTransactionResult> FinishTransactionAsync(TseTransactionFinishRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TseTransactionResult(true, "OK", request.TransactionNumber, 2, DateTimeOffset.UtcNow, "FAKE-SERIAL", "ZmluaXNo"));

        public Task<TseExportResult> ExportTarAsync(string targetPath, CancellationToken ct = default) =>
            Task.FromResult(new TseExportResult(false, "Fake export"));
    }
}
