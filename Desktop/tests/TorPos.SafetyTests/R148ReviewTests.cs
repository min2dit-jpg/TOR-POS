using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R148: deposit added with the PFAND / LEERGUT key is exported as business case
// "Pfand".
//
// DSFinV-K Anhang C: "Im Geschäftsvorfalltyp ,Pfand' werden alle Pfandeinnahmen aus
// Handelsgeschäften dargestellt." Deposit set on an article was already its own
// Pfand position (R131); the bottle and crate deposit of the key was exported as
// "Umsatz" and summed with the turnover in the closing.
public static class R148ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        assert(PfandProducts.IsDeposit(PfandProducts.Bottle8) && PfandProducts.IsDeposit(PfandProducts.CrateFull) &&
               !PfandProducts.IsDeposit(1) && !PfandProducts.IsDeposit(OrderBestellungDelta.DiscountProductId),
            "R148 the positions of the PFAND / LEERGUT key are recognised by their technical ids, nothing else is");

        var dir = Path.Combine(root, "r148-pfand");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r148.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R148 Späti",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus) VALUES(148001,$at,'CASH',425,425,'TEST_FIXTURE','SALE',425,0); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            var saleId = Convert.ToInt64(await q.ExecuteScalarAsync());
            q.Parameters.Clear();
            q.CommandText = """
                INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES
                ($s,1,'Cola 0,5l',1,250,19,250),
                ($s,$bottle,'PFAND · 25 CENT',1,25,19,25),
                ($s,$crate,'LEERGUT KISTE · LEER',1,150,19,150);
                """;
            q.Parameters.AddWithValue("$s", saleId);
            q.Parameters.AddWithValue("$bottle", PfandProducts.Bottle25);
            q.Parameters.AddWithValue("$crate", PfandProducts.CrateEmpty);
            await q.ExecuteNonQueryAsync();
        }

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("chef", "TEST");
        var folder = await new DsfinvkExportService(db, settings).ExportAsync(DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddMinutes(1), Path.Combine(dir, "out"));
        var positions = Csv(Path.Combine(folder, "lines.csv")).Where(p => p["BON_ID"] == "148001").ToList();
        var cases = Csv(Path.Combine(folder, "businesscases.csv"));
        assert(positions.Select(p => p["GV_TYP"]).SequenceEqual(new[] { "Umsatz", "Pfand", "Pfand" }),
            "R148 bottle and crate deposit of the PFAND / LEERGUT key are positions of business case Pfand, the article stays Umsatz");
        assert(cases.Single(x => x["GV_TYP"] == "Pfand")["Z_UMS_BRUTTO"] == "1,75" &&
               cases.Single(x => x["GV_TYP"] == "Umsatz")["Z_UMS_BRUTTO"] == "2,50",
            "R148 the closing shows the deposit apart from the turnover (DSFinV-K Z_GV_Typ)");
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
