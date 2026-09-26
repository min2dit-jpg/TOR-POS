using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantInterimBillTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var old=Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION","RESTAURANT");
        try
        {
            var db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(Path.GetTempPath(),"interim-"+Guid.NewGuid().ToString("N")+".db"));
            var repo=new RestaurantRepository(db);
            var area=await repo.SaveAreaAsync("Gastraum");var table=await repo.SaveTableAsync(area,"G1","Tisch 1",4);
            var session=await repo.OpenTableAsync(table,"Kellner");
            var product=new Product { Id=901,Name="Burger",BasePriceCents=1200,VatRate=7m };
            var item=await repo.AddItemAsync(session.Id,session.Version,product,2m,"Kellner");
            var refused=false;
            try {await repo.BuildInterimBillAsync(session.Id);}catch(InvalidOperationException){refused=true;}
            assert(refused,"Interim bill rejects a not-yet-secured pending order snapshot");
            // Model a secured order with a previously paid slice. Full fiscal
            // signing/split settlement is covered by RestaurantFoundationTests.
            using(var c=db.OpenConnection())using(var q=c.CreateCommand())
            {q.CommandText="UPDATE restaurant_session_items SET fiscal_state='SECURED';";await q.ExecuteNonQueryAsync();}
            var before=await Fingerprint(db);
            var report=await repo.BuildInterimBillAsync(session.Id);
            var repeat=await repo.BuildInterimBillAsync(session.Id);
            assert(report.Title.Contains("kein Zahlungsbeleg") && report.Lines.Any(x=>x.Contains("24,00")) && report.Lines.Any(x=>x.Contains("2 × Burger")),
                "Interim bill shows existing order amounts and an explicit non-payment label");
            assert(before==await Fingerprint(db) && (await repo.GetSessionAsync(session.Id))!.State==RestaurantTableSessionState.Open,
                "Repeated interim bill creates no sale, payment, receipt number, TSE transaction or session mutation");
            using(var c=db.OpenConnection())using(var q=c.CreateCommand())
            {q.CommandText="UPDATE restaurant_session_items SET quantity_milli=1000;";await q.ExecuteNonQueryAsync();}
            report=await repo.BuildInterimBillAsync(session.Id);
            assert(report.Lines.Any(x=>x.Contains("1 × Burger")) && report.Lines.Any(x=>x.Contains("12,00")),
                "Interim bill uses remaining quantity after a partial split payment");
            using(var c=db.OpenConnection())using(var q=c.CreateCommand())
            {q.CommandText="UPDATE restaurant_session_items SET state='PAID';";await q.ExecuteNonQueryAsync();}
            report=await repo.BuildInterimBillAsync(session.Id);
            assert(!report.Lines.Any(x=>x.Contains("× Burger")),"Interim bill excludes already paid lines");
        }
        finally {Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION",old);}
    }

    private static async Task<string> Fingerprint(SqliteDatabase db)
    {
        using var c=db.OpenReadConnection();var tables=new List<string>();
        using(var q=c.CreateCommand())
        {q.CommandText="SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";using var r=await q.ExecuteReaderAsync();while(await r.ReadAsync())tables.Add(r.GetString(0));}
        var output=new System.Text.StringBuilder();
        foreach(var table in tables)
        {
            using var q=c.CreateCommand();q.CommandText="SELECT * FROM \""+table.Replace("\"","\"\"")+"\";";
            using var r=await q.ExecuteReaderAsync();
            while(await r.ReadAsync()) for(var i=0;i<r.FieldCount;i++)output.Append(table).Append('|').Append(Convert.ToString(r.GetValue(i),System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        return output.ToString();
    }
}
