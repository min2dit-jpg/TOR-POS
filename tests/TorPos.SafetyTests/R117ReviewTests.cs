using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

// R117: closes finding İ1 (Medium) from this session's full project audit -
// and one worse case found while fixing it.
//
// Reported: the Kassensturz counted expected cash per CALENDAR DAY while the
// Z-report counted since the last Tagesabschluss. For an IMBISS still trading
// at 01:00 that compared the whole evening's physical cash against only the
// sales made since midnight - a large phantom surplus.
//
// Found while fixing it: GetOpenPeriodAsync started the open period at
// max(today's midnight, last closing). So for a shop open past midnight, the
// sales between the last closing and midnight fell into NO Z-report at all -
// the next Z started at 00:00 and the previous one had closed hours earlier.
// Turnover simply disappeared from the daily closing sequence.
//
// Both now run from the last Tagesabschluss with no midnight floor.
public static class R117ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r117-business-day");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r117.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var management = new BusinessManagementService(db, settings, audit);
        var cashMovements = new CashMovementRepository(db, audit);

        var receipt = 117000L;
        void InsertCashSale(DateTimeOffset at, long cents)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = """
                INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,card_portion_cents)
                VALUES($r,$at,'CASH',$t,$t,'TEST_FIXTURE','SALE',$t,0);
                """;
            q.Parameters.AddWithValue("$r", ++receipt);
            q.Parameters.AddWithValue("$at", at.ToString("O"));
            q.Parameters.AddWithValue("$t", cents);
            q.ExecuteNonQuery();
        }

        void InsertClosing(DateTimeOffset at)
        {
            using var c = db.OpenConnection();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO daily_closings(closed_at,operator_name,close_type) VALUES($at,'tester','Z_REPORT');";
            q.Parameters.AddWithValue("$at", at.ToString("O"));
            q.ExecuteNonQuery();
        }

        // A night shift that crosses midnight, the normal IMBISS case:
        // last closing early yesterday morning, then trade on both sides of
        // midnight, and a Kassensturz/Z now.
        var now = DateTimeOffset.Now;
        var lastClosing = now.AddHours(-26);      // yesterday, early morning
        var eveningSale = now.AddHours(-6);        // before midnight if now is past 00:00
        var beforeMidnight = now.Date == now.AddHours(-6).Date
            ? now.AddHours(-6)                     // still the same day; use any earlier point
            : eveningSale;

        InsertClosing(lastClosing);
        InsertCashSale(beforeMidnight, 4000);
        InsertCashSale(now.AddMinutes(-30), 1000);

        // Also book a movement on each side, to prove the same boundary is
        // applied to cash_movements.
        using (var c = db.OpenConnection())
        using (var q = c.CreateCommand())
        {
            q.CommandText = "INSERT INTO cash_movements(created_at,movement_type,amount_cents,reason,actor,fiscal_mode) VALUES($at,'EINLAGE',500,'R117 Wechselgeld','tester','TEST_ONLY');";
            q.Parameters.AddWithValue("$at", beforeMidnight.ToString("O"));
            q.ExecuteNonQuery();
        }

        var expected = await cashMovements.GetExpectedCashCentsAsync(0);
        assert(
            expected == 5500,
            $"R117 the Kassensturz counts everything since the last Tagesabschluss, including trade from before midnight (expected 5500, actual {expected})");

        var zBefore = await management.BuildXReportAsync();
        var zText = string.Join("\n", zBefore.Lines);
        assert(
            zText.Contains("50,00") || zText.Contains("50.00"),
            $"R117 the open X/Z period also spans midnight - the 40,00 EUR evening sale is not dropped (report:\n{zText})");

        // After a closing, both views start again from that closing.
        await management.CreateZArchiveAsync("tester", "TEST");
        var afterZ = await cashMovements.GetExpectedCashCentsAsync(0);
        assert(
            afterZ == 0,
            $"R117 a Tagesabschluss resets the expected drawer content to the opening balance (actual {afterZ})");

        InsertCashSale(DateTimeOffset.Now, 700);
        var afterNewSale = await cashMovements.GetExpectedCashCentsAsync(0);
        assert(
            afterNewSale == 700,
            $"R117 only sales made after that closing count towards the next Kassensturz (actual {afterNewSale})");

        // Sales that were already closed out must never be counted twice.
        var secondZ = await management.BuildXReportAsync();
        var secondText = string.Join("\n", secondZ.Lines);
        assert(
            !secondText.Contains("50,00") && !secondText.Contains("50.00"),
            $"R117 the already-closed evening turnover does not reappear in the next period (report:\n{secondText})");
    }
}
