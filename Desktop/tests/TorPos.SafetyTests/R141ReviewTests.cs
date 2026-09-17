using TorPos.Infrastructure;

// R141: the payment types of the X/Z-Bericht are net of Storno/Retoure, and the
// report shows the cash movements of the period.
//
// Until R141 "ZAHLARTEN Bar/Karte" counted sales only: on a day with a cash
// Storno the Z-Bericht showed more cash than was taken, and Bar + Karte did not
// match "Umsatz nach Storno/Retouren" printed a few lines above. Einlagen,
// Entnahmen and Kassendifferenzen did not appear at all. The DSFinV-K
// Kassenabschluss (Z_Zahlart) is net and contains every cash Beleg.
public static class R141ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r141-z-zahlarten");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r141.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);

        var cashSale = await InsertAsync(db, 141001, "SALE", "CASH", 1000, 1000, 0, null);
        var cardSale = await InsertAsync(db, 141002, "SALE", "CARD", 500, 0, 500, null);
        await InsertAsync(db, 141003, "SALE", "MIXED", 800, 300, 500, null);
        await InsertAsync(db, 141004, "STORNO", "CASH", 1000, 1000, 0, cashSale);
        await InsertAsync(db, 141005, "RETURN", "CARD", 200, 0, 200, cardSale);
        await MovementAsync(db, "EINLAGE", 5000, "PRODUCTION", "Geldtransit");
        await MovementAsync(db, "ENTNAHME", 1000, "PRODUCTION", "Privatentnahme");
        await MovementAsync(db, "ENTNAHME", 50, "PRODUCTION", "DifferenzSollIst");
        await MovementAsync(db, "EINLAGE", 9999, "TEST_ONLY", "Einzahlung");

        var x = string.Join("\n", (await management.BuildXReportAsync()).Lines);
        // sales 23,00 - Storno 10,00 - Retoure 2,00 = 11,00; Bar 10+3-10 = 3,00; Karte 5+5-2 = 8,00
        assert(x.Contains("= Umsatz nach Storno/Retouren: 11,00 EUR") &&
               x.Contains("Bar: 3,00 EUR") && x.Contains("Karte: 8,00 EUR"),
            $"R141 Bar and Karte are net of Storno/Retoure in the way they were paid and add up to the turnover after reversals (report:\n{x})");

        assert(x.Contains("Geldtransit (Wechselgeld / aus Bank oder Tresor): 50,00 EUR") &&
               x.Contains("Privatentnahme: -10,00 EUR") &&
               x.Contains("Kassendifferenz (Fehlbetrag beim Kassensturz): -0,50 EUR") &&
               x.Contains("= Bar-Saldo des Zeitraums (Bar-Umsatz + Kassenbewegungen): 42,50 EUR") &&
               !x.Contains("99,99"),
            "R141 the report lists Einlagen, Entnahmen and Kassendifferenzen of the period and the resulting cash, without test entries");

        await Task.Delay(15);
        var z = await management.CreateZArchiveAsync("chef", "TEST");
        assert(z.CashCents == 300 && z.CardCents == 800 && z.GrossCents == 1100,
            "R141 the archived Z-Bericht stores the net payment types (Bar + Karte = gross after reversals)");
    }

    private static async Task<long> InsertAsync(SqliteDatabase db, long receipt, string type, string method, long total, long cash, long card, long? original)
    {
        await Task.Delay(15);
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,original_sale_id,cash_portion_cents,card_portion_cents) VALUES($r,$at,$m,$t,$t,'TEST_FIXTURE',$type,$orig,$cash,$card); SELECT last_insert_rowid();";
        q.Parameters.AddWithValue("$r", receipt);
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$m", method);
        q.Parameters.AddWithValue("$t", total);
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$orig", original is long o ? o : DBNull.Value);
        q.Parameters.AddWithValue("$cash", cash);
        q.Parameters.AddWithValue("$card", card);
        return Convert.ToInt64(await q.ExecuteScalarAsync());
    }

    private static async Task MovementAsync(SqliteDatabase db, string type, long cents, string mode, string businessCase)
    {
        await using var c = db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode,business_case) VALUES($at,$type,$amount,'R141','kasse1',$mode,$case);";
        q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
        q.Parameters.AddWithValue("$type", type);
        q.Parameters.AddWithValue("$amount", cents);
        q.Parameters.AddWithValue("$mode", mode);
        q.Parameters.AddWithValue("$case", businessCase);
        await q.ExecuteNonQueryAsync();
    }
}
