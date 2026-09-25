using System.Globalization;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed partial class RestaurantRepository
{
    private static string Money(long cents) => (cents / 100m).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")) + " €";

    // One read transaction: session and unpaid lines belong to one consistent
    // snapshot. No checkout/TSE/printer dependency, reservation or UPDATE here.
    public async Task<ReportPrintJob> BuildInterimBillAsync(string sessionId)
    {
        using var c=_db.OpenReadConnection(); using var tx=c.BeginTransaction(deferred:true);
        var lines=new List<string> { "Zwischenrechnung – kein Zahlungsbeleg", "" };
        using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="SELECT t.display_name,s.assigned_waiter,s.state FROM restaurant_sessions s JOIN restaurant_tables t ON t.id=s.table_id WHERE s.id=$id;";
            q.Parameters.AddWithValue("$id",sessionId);
            using var r=await q.ExecuteReaderAsync();
            if(!await r.ReadAsync() || r.GetString(2) is not ("OPEN" or "CHECK_REQUESTED"))
                throw new InvalidOperationException("Kein offener Tischvorgang.");
            lines.Add("Tisch: "+r.GetString(0)); lines.Add("Bedienung: "+r.GetString(1));
            if(r.GetString(2)=="CHECK_REQUESTED") lines.Add("Zahlung in Bearbeitung – Zahlungsstatus bitte prüfen.");
        }
        using(var q=c.CreateCommand())
        {
            q.Transaction=tx; q.CommandText="SELECT value FROM app_settings WHERE key='company.name';";
            lines.Insert(0,Convert.ToString(await q.ExecuteScalarAsync())??"");
        }
        var created=DateTimeOffset.Now;lines.Add(created.ToString("dd.MM.yyyy HH:mm",CultureInfo.GetCultureInfo("de-DE")));lines.Add("");
        long total=0;
        using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="SELECT product_name,variant_name,quantity_milli,unit_price_cents,fiscal_state FROM restaurant_session_items WHERE session_id=$id AND state='ACTIVE' ORDER BY id;";
            q.Parameters.AddWithValue("$id",sessionId);using var r=await q.ExecuteReaderAsync();
            while(await r.ReadAsync())
            {
                if(r.GetString(4)=="PENDING") throw new InvalidOperationException("Bestellung wird noch gesichert. Bitte anschließend erneut versuchen.");
                var quantity=r.GetInt64(2)/1000m;
                var amount=(long)decimal.Round(quantity*r.GetInt64(3),0,MidpointRounding.AwayFromZero);
                total=checked(total+amount);
                lines.Add($"{quantity.ToString("0.###",CultureInfo.GetCultureInfo("de-DE"))} × {r.GetString(0)} · {Money(amount)}");
                if(r.GetString(1).Length>0) lines.Add("  "+r.GetString(1));
            }
        }
        lines.Add("");lines.Add("Offener Betrag: "+Money(total));
        lines.Add("Bereits bezahlte Positionen sind nicht enthalten.");
        lines.Add("Zwischenrechnung – kein Zahlungsbeleg");
        lines.Add("Keine Zahlung erfolgt. Tisch bleibt geöffnet.");
        return new("Zwischenrechnung – kein Zahlungsbeleg",lines,created,ReportPaperFormat.Receipt80);
    }
}
