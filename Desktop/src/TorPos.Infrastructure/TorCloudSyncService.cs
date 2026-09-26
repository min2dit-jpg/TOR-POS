using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed record TorCloudEvent(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("occurred_at")] string OccurredAt,
    [property: JsonPropertyName("payload")] JsonElement Payload);
public sealed record TorCloudConfiguration(string BaseUrl, string DeviceCode, string ProtectedToken, bool Enabled);
public sealed record TorCloudSaleLine(
    long ProductId,
    string ProductName,
    string VariantName,
    decimal Quantity,
    string Unit,
    long UnitPriceCents,
    long LineTotalCents,
    decimal VatRate,
    long ListUnitPriceCents,
    long ListLineTotalCents,
    long PromotionId,
    string PromotionName,
    int PromotionPercent,
    long PromotionDiscountCents,
    string PromotionStartDate,
    string PromotionEndDate,
    MenuComponentSnapshot[] MenuComponents);
/// <summary>Cloud: one Kassenabschluss (Z-Bericht) as TOR Cloud stores it.</summary>
public sealed record TorCloudZVat(decimal Rate, long NetCents, long TaxCents, long GrossCents);
public sealed record TorCloudZClosing(
    long ZNumber,
    DateTimeOffset ClosedAt,
    DateTimeOffset PeriodFrom,
    int ReceiptCount,
    long GrossCents,
    long CashCents,
    long CardCents,
    long ListGrossCents,
    long PromotionDiscountCents,
    long ManualDiscountCents,
    long StornoCents,
    long ReturnCents,
    IReadOnlyList<TorCloudZVat> Vat,
    string FiscalStatus,
    string OperatorName);
public interface ICloudSecretProtector { string Protect(string value); string Unprotect(string value); }
public sealed class WindowsCloudSecretProtector : ICloudSecretProtector
{
    public string Protect(string value) => Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    public string Unprotect(string value) => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}

/// <summary>No HTTP happens inside a database transaction or the cashier UI thread.</summary>
public sealed class TorCloudOutbox
{
    private readonly SqliteDatabase _db;
    public TorCloudOutbox(SqliteDatabase db) => _db=db;
    public const string Schema = """
        CREATE TABLE IF NOT EXISTS cloud_outbox(
          event_id TEXT PRIMARY KEY, event_type TEXT NOT NULL, occurred_at TEXT NOT NULL,
          payload TEXT NOT NULL, target_url TEXT NOT NULL, device_code TEXT NOT NULL,
          attempts INTEGER NOT NULL DEFAULT 0, next_attempt TEXT NOT NULL DEFAULT '', last_error TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS cloud_outbox_due ON cloud_outbox(next_attempt);
        """;
    public static TorCloudEvent Event(string type, object payload, string? id=null, DateTimeOffset? occurred=null) =>
        new(id??Guid.NewGuid().ToString("N"),type,(occurred??DateTimeOffset.UtcNow).ToUniversalTime().ToString("O"),JsonSerializer.SerializeToElement(payload));
    public static TorCloudConfiguration? Configuration(SqliteConnection c,SqliteTransaction? tx=null)
    {
        using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT value FROM app_settings WHERE key='cloud.configuration';";
        var raw=q.ExecuteScalar() as string;
        return string.IsNullOrEmpty(raw)?null:JsonSerializer.Deserialize<TorCloudConfiguration>(raw);
    }
    public static void Insert(SqliteConnection c,SqliteTransaction tx,TorCloudConfiguration config,TorCloudEvent e)
    {
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT INTO cloud_outbox(event_id,event_type,occurred_at,payload,target_url,device_code) VALUES($id,$type,$at,$p,$url,$code);";
        q.Parameters.AddWithValue("$id",e.EventId);q.Parameters.AddWithValue("$type",e.Type);q.Parameters.AddWithValue("$at",e.OccurredAt);
        q.Parameters.AddWithValue("$p",e.Payload.GetRawText());q.Parameters.AddWithValue("$url",config.BaseUrl);q.Parameters.AddWithValue("$code",config.DeviceCode);q.ExecuteNonQuery();
    }
    // Called before sale COMMIT, with the exact immutable checkout snapshot.
    public static void EnqueueSale(SqliteConnection c,SqliteTransaction tx,CheckoutSnapshot s,long receipt,long pickupNumber,DateTimeOffset at)
    {
        var lines=s.Lines.Select(x=>new TorCloudSaleLine(
            x.ProductId,x.ProductName,x.VariantName,x.Quantity,x.Unit,x.UnitPriceCents,x.LineTotalCents,x.VatRate,
            x.EffectiveListUnitPriceCents,x.ListLineTotalCents,x.PromotionId,x.PromotionName,x.PromotionPercent,
            x.PromotionDiscountCents,x.PromotionStartDate,x.PromotionEndDate,x.MenuComponents.ToArray())).ToArray();
        EnqueueRecordedSale(
            c,tx,"sale-"+s.OperationId,"SALE",null,receipt,pickupNumber,at,s.Method,
            s.Lines.Sum(x=>x.LineTotalCents),s.DiscountCents,s.TotalCents,
            s.EffectiveCashPortionCents,s.EffectiveCardPortionCents,s.OperatorName,lines);
    }

    // R179: Storno/Retoure are completed fiscal sales too. They must reach Cloud
    // in the SAME SQLite transaction as the local reversal, otherwise Cloud can
    // show turnover/stock that never reflects the counter-booking. Amounts stay
    // positive in the wire contract; transaction_type tells Cloud to project them
    // with the opposite sign.
    public static void EnqueueRecordedSale(
        SqliteConnection c,
        SqliteTransaction tx,
        string eventId,
        string transactionType,
        long? originalReceiptNumber,
        long receipt,
        long pickupNumber,
        DateTimeOffset at,
        PaymentMethod paymentMethod,
        long subtotalCents,
        long discountCents,
        long totalCents,
        long cashPortionCents,
        long cardPortionCents,
        string operatorName,
        IReadOnlyList<TorCloudSaleLine> lines)
    {
        var config=Configuration(c,tx);if(config is null)return;
        transactionType=(transactionType??"SALE").Trim().ToUpperInvariant();
        if(transactionType is not ("SALE" or "STORNO" or "RETURN"))
            throw new InvalidOperationException("Ungültiger Cloud-Buchungstyp.");

        var consumption=new Dictionary<long,decimal>();
        foreach(var line in lines.Where(x=>x.ProductId>0))
        {
            if(line.MenuComponents.Length>0)
            {
                foreach(var component in line.MenuComponents)
                {
                    var qty=line.Quantity*component.Quantity;
                    consumption[component.ProductId]=consumption.GetValueOrDefault(component.ProductId)+qty;
                }
                continue;
            }

            var found=false;
            using var combo=c.CreateCommand(); combo.Transaction=tx;
            combo.CommandText="SELECT component_product_id,CASE WHEN COALESCE(quantity_milli,0)<>0 THEN quantity_milli ELSE CAST(ROUND(quantity*1000.0) AS INTEGER) END FROM product_combo_items WHERE product_id=$id AND COALESCE(choice_group,'')='' ORDER BY sort_order;";
            combo.Parameters.AddWithValue("$id",line.ProductId);
            using var r=combo.ExecuteReader();
            while(r.Read())
            {
                found=true; var id=r.GetInt64(0); var qty=line.Quantity*QuantityStorage.FromMilli(r.GetInt64(1));
                consumption[id]=consumption.GetValueOrDefault(id)+qty;
            }
            if(!found) consumption[line.ProductId]=consumption.GetValueOrDefault(line.ProductId)+line.Quantity;
        }

        Insert(c,tx,config,Event("sale.completed",new {
            receipt_number=receipt,pickup_number=pickupNumber,transaction_type=transactionType,
            original_receipt_number=originalReceiptNumber,payment_method=paymentMethod.ToString().ToUpperInvariant(),
            cash_portion_cents=cashPortionCents,card_portion_cents=cardPortionCents,
            list_subtotal_cents=lines.Sum(x=>x.ListLineTotalCents),
            promotion_discount_cents=lines.Sum(x=>x.PromotionDiscountCents),
            subtotal_cents=subtotalCents,discount_cents=discountCents,manual_discount_cents=discountCents,total_cents=totalCents,
            operator_name=operatorName,item_count=lines.Count,
            items=lines.Select((x,i)=>new {position_no=i+1,product_key=x.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                name=x.ProductName+(string.IsNullOrEmpty(x.VariantName)?"":" · "+x.VariantName),quantity=x.Quantity,unit=x.Unit,
                list_unit_price_cents=x.ListUnitPriceCents,unit_price_cents=x.UnitPriceCents,line_total_cents=x.LineTotalCents,vat_rate=x.VatRate,
                promotion_id=x.PromotionId,promotion_name=x.PromotionName,promotion_percent=x.PromotionPercent,
                promotion_discount_cents=x.PromotionDiscountCents,promotion_start_date=x.PromotionStartDate,promotion_end_date=x.PromotionEndDate}).ToArray(),
            stock_consumption=consumption.Select(x=>new {product_key=x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),quantity=x.Value}).ToArray()
        },eventId,at));
    }
    /// <summary>
    /// Cloud: the Z-Bericht reaches TOR Cloud in the SAME transaction as the
    /// local closing, like sales (R179), so the portal's Z archive and its
    /// turnover can never disagree with the till. The event id is the Z number:
    /// a retried closing is the same event.
    /// </summary>
    public static void EnqueueZClosed(SqliteConnection c,SqliteTransaction tx,TorCloudZClosing z)
    {
        var config=Configuration(c,tx);if(config is null)return;
        Insert(c,tx,config,Event("z.closed",new {
            z_number=z.ZNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gross_cents=z.GrossCents,sale_count=z.ReceiptCount,
            period_from=z.PeriodFrom.ToUniversalTime().ToString("O"),period_to=z.ClosedAt.ToUniversalTime().ToString("O"),
            cash_cents=z.CashCents,card_cents=z.CardCents,
            list_gross_cents=z.ListGrossCents,promotion_discount_cents=z.PromotionDiscountCents,manual_discount_cents=z.ManualDiscountCents,
            storno_cents=z.StornoCents,return_cents=z.ReturnCents,
            vat=z.Vat.Select(v=>new {rate=v.Rate,net_cents=v.NetCents,tax_cents=v.TaxCents,gross_cents=v.GrossCents}).ToArray(),
            fiscal_status=z.FiscalStatus.Length>40?z.FiscalStatus[..40]:z.FiscalStatus,
            operator_name=z.OperatorName.Length>200?z.OperatorName[..200]:z.OperatorName
        },"z-"+z.ZNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),z.ClosedAt));
    }

    /// <summary>
    /// Cloud: a real (production) Einlage or Entnahme, in the same transaction as
    /// the local booking. Test entries and the Kassensturz count itself are not
    /// cash flows and are not sent.
    /// </summary>
    public static void EnqueueCashMovement(SqliteConnection c,SqliteTransaction tx,CashMovement movement)
    {
        if(movement.FiscalMode!=CashMovement.ProductionMode)return;
        if(movement.Kind is not (CashMovementKind.Einlage or CashMovementKind.Entnahme))return;
        var config=Configuration(c,tx);if(config is null)return;
        Insert(c,tx,config,Event("cash.movement",new {
            movement_id=movement.Id,
            movement_type=movement.Kind==CashMovementKind.Einlage?"DEPOSIT":"WITHDRAWAL",
            amount_cents=movement.AmountCents,
            business_case=movement.BusinessCase?.ToString()??"",
            reason=movement.Reason.Length>500?movement.Reason[..500]:movement.Reason,
            actor=movement.Actor.Length>200?movement.Actor[..200]:movement.Actor
        },"cash-"+movement.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),movement.CreatedAt));
    }

    public Task<TorCloudConfiguration?> GetConfigurationAsync()=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();return Task.FromResult(Configuration(c));
    });
    public Task SaveConfigurationAsync(TorCloudConfiguration config)=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var tx=c.BeginTransaction();
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT COUNT(*) FROM cloud_outbox WHERE target_url<>$url OR device_code<>$code;";
        q.Parameters.AddWithValue("$url",config.BaseUrl);q.Parameters.AddWithValue("$code",config.DeviceCode);
        if(Convert.ToInt64(q.ExecuteScalar())>0)throw new InvalidOperationException("Noch ungesendete Daten für die bisherige Cloud. Erst synchronisieren; Zielwechsel wurde nicht gespeichert.");
        q.Parameters.Clear();q.CommandText="INSERT INTO app_settings(key,value) VALUES('cloud.configuration',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        q.Parameters.AddWithValue("$v",JsonSerializer.Serialize(config));q.ExecuteNonQuery();tx.Commit();return Task.CompletedTask;
    });
    // Review §7: the heartbeat always said "NICHT GEPRÜFT". It now reports what
    // the till knows without touching the TSE: an open outage wins, otherwise
    // the configured TSE status. The printer is still not probed for this.
    private static string HeartbeatTseStatus(SqliteConnection c,SqliteTransaction tx){
        using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="SELECT COUNT(*) FROM tse_outage_log WHERE state='OPEN';";
        if(Convert.ToInt64(q.ExecuteScalar())>0)return "AUSFALL";
        q.CommandText="SELECT value FROM app_settings WHERE key='tse.status';";
        var configured=((q.ExecuteScalar() as string)??"").Trim().ToUpperInvariant();
        return configured.Length==0?"NICHT EINGERICHTET":configured.Length>40?configured[..40]:configured;
    }
    public Task EnqueueHeartbeatAsync()=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var tx=c.BeginTransaction();var config=Configuration(c,tx);
        if(config is null||!config.Enabled)return Task.CompletedTask;
        using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT COUNT(*) FROM cloud_outbox WHERE event_type='heartbeat';";
        if(Convert.ToInt64(q.ExecuteScalar())==0)Insert(c,tx,config,Event("heartbeat",HeartbeatPayload(c,tx)));
        tx.Commit();return Task.CompletedTask;
    });

    /// <summary>
    /// Cloud: what the portal's Gerätestatus needs to see a till's real state -
    /// the product edition it runs, test or production mode, events still
    /// waiting or refused by TOR Cloud, and when the TSE certificate expires.
    /// Nothing here touches the TSE or the printer.
    /// </summary>
    internal static object HeartbeatPayload(SqliteConnection c,SqliteTransaction tx)
    {
        string Setting(string key){using var s=c.CreateCommand();s.Transaction=tx;s.CommandText="SELECT value FROM app_settings WHERE key=$k;";s.Parameters.AddWithValue("$k",key);return ((s.ExecuteScalar() as string)??"").Trim();}
        long Count(string sql){using var s=c.CreateCommand();s.Transaction=tx;s.CommandText=sql;try{return Convert.ToInt64(s.ExecuteScalar());}catch(SqliteException){return 0;}}
        var edition=(AppPaths.ProductEdition??Setting("installation.edition")).ToUpperInvariant();
        if(edition.Length==0)edition=Setting("business.mode").ToUpperInvariant();
        if(edition is not ("KIOSK" or "IMBISS" or "RESTAURANT"))edition="";
        var expiry=Setting("tse.expiry_date");
        if(!DateOnly.TryParseExact(expiry,"yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out _))expiry="";
        return new {
            software_version=TorRelease.UserAgentVersion,
            tse_status=HeartbeatTseStatus(c,tx),
            printer_status="NICHT GEPRÜFT",
            edition,
            fiscal_mode=FiscalRelease.ProductionAllowed?"PRODUKTIV":"TESTBETRIEB",
            outbox_pending=Count("SELECT COUNT(*) FROM cloud_outbox WHERE event_type<>'heartbeat';"),
            outbox_rejected=Count("SELECT COUNT(*) FROM cloud_outbox_rejected;"),
            tse_certificate_until=expiry
        };
    }
    public Task EnqueueStockAsync()=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var tx=c.BeginTransaction();var config=Configuration(c,tx)??throw new InvalidOperationException("Cloud zuerst speichern.");
        using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="""
            SELECT p.id,p.name,p.sku,p.barcode,
                   COALESCE(p.stock_milli,CAST(ROUND(COALESCE(p.stock_quantity,0)*1000.0) AS INTEGER)),
                   p.unit,p.base_price_cents,
                   c.name,COALESCE(g.name,'Standard'),
                   COALESCE(p.min_stock_milli,CAST(ROUND(COALESCE(p.min_stock_quantity,0)*1000.0) AS INTEGER)),
                   COALESCE(p.purchase_price_cents,0)
            FROM products p
            JOIN categories c ON c.id=p.category_id
            LEFT JOIN category_master_data m ON m.category_id=c.id
            LEFT JOIN product_groups g ON g.id=m.group_id
            WHERE p.is_active=1
              AND (UPPER(COALESCE(p.edition_scope,'ALL'))='ALL'
                   OR UPPER(p.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
              AND (UPPER(COALESCE(c.edition_scope,'ALL'))='ALL'
                   OR UPPER(c.edition_scope)=UPPER(COALESCE((SELECT value FROM app_settings WHERE key='business.mode'),'IMBISS')))
            ORDER BY p.id
            LIMIT 5001;
            """;
        var items=new List<object>();
        using(var r=q.ExecuteReader())while(r.Read())items.Add(new {
            product_key=r.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
            name=r.GetString(1),sku=r.GetString(2),barcode=r.GetString(3),quantity=QuantityStorage.FromMilli(r.GetInt64(4)),
            unit=r.GetString(5),price_cents=r.GetInt64(6),category_name=r.GetString(7),group_name=r.GetString(8),
            min_stock_quantity=QuantityStorage.FromMilli(r.GetInt64(9)),purchase_price_cents=r.GetInt64(10)});
        if(items.Count>5000)throw new InvalidOperationException("R48 unterstützt vollständige Artikel-/Bestandsübertragung bis 5000 aktive Artikel. Es wurden keine Artikel übertragen.");
        var e=Event("stock.snapshot",new {items});
        if(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(e))>900000)throw new InvalidOperationException("Bestandsdaten zu groß. Es wurden keine Artikel übertragen.");
        // R46: A full stock snapshot supersedes older unsent snapshots for the same device.
        // Sales/events keep their FIFO order; only redundant stock snapshots are coalesced.
        using(var cleanup=c.CreateCommand()){
            cleanup.Transaction=tx;
            cleanup.CommandText="DELETE FROM cloud_outbox WHERE event_type='stock.snapshot' AND target_url=$url AND device_code=$code;";
            cleanup.Parameters.AddWithValue("$url",config.BaseUrl);cleanup.Parameters.AddWithValue("$code",config.DeviceCode);
            cleanup.ExecuteNonQuery();
        }
        Insert(c,tx,config,e);tx.Commit();return Task.CompletedTask;
    });
    public Task<IReadOnlyList<TorCloudEvent>> PendingAsync(TorCloudConfiguration config)=>IoQueue.RunAsync<IReadOnlyList<TorCloudEvent>>(()=>{
        using var c=_db.OpenConnection();using var q=c.CreateCommand();
        // FIFO: an unacknowledged head is never overtaken by newer snapshots.
        q.CommandText="SELECT event_id,event_type,occurred_at,payload,next_attempt FROM cloud_outbox WHERE target_url=$url AND device_code=$code ORDER BY rowid LIMIT 25;";
        q.Parameters.AddWithValue("$url",config.BaseUrl);q.Parameters.AddWithValue("$code",config.DeviceCode);
        var result=new List<TorCloudEvent>();int bytes=0;
        using var r=q.ExecuteReader();while(r.Read()){
            if(DateTimeOffset.TryParse(r.GetString(4),out var next)&&next>DateTimeOffset.UtcNow)break;
            var e=new TorCloudEvent(r.GetString(0),r.GetString(1),r.GetString(2),JsonSerializer.Deserialize<JsonElement>(r.GetString(3)));
            var size=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(e));if(result.Count>0&&bytes+size>900000)break;
            result.Add(e);bytes+=size;
        }
        return Task.FromResult<IReadOnlyList<TorCloudEvent>>(result);
    });
    public Task CompleteAsync(IReadOnlyList<TorCloudEvent> events,bool success,string error="")=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var e in events){
            using var q=c.CreateCommand();q.Transaction=tx;
            q.CommandText=success?"DELETE FROM cloud_outbox WHERE event_id=$id;":
                "UPDATE cloud_outbox SET attempts=attempts+1,next_attempt=strftime('%Y-%m-%dT%H:%M:%fZ','now','+' || MIN(300,10 * (1 << MIN(attempts,5))) || ' seconds'),last_error=$error WHERE event_id=$id;";
            q.Parameters.AddWithValue("$id",e.EventId);if(!success)q.Parameters.AddWithValue("$error",error);q.ExecuteNonQuery();
        }
        if(success){using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO app_settings(key,value) VALUES('cloud.last_success',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";q.Parameters.AddWithValue("$v",DateTimeOffset.UtcNow.ToString("O"));q.ExecuteNonQuery();}
        tx.Commit();return Task.CompletedTask;
    });
    // C-4: the Cloud has answered for every event. Accepted and duplicate
    // events leave the queue; an event the Cloud refuses for good (rejected or
    // conflict) moves to cloud_outbox_rejected with the reason instead of
    // blocking the FIFO head forever. Nothing is deleted without a trace: the
    // local fiscal record is untouched, only its Cloud copy is parked.
    public Task SettleAsync(IReadOnlyList<TorCloudEvent> done,IReadOnlyList<(TorCloudEvent Event,string Verdict,string Reason)> refused)=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var tx=c.BeginTransaction();
        foreach(var (e,verdict,reason) in refused){
            using var q=c.CreateCommand();q.Transaction=tx;
            q.CommandText="""
                INSERT OR REPLACE INTO cloud_outbox_rejected(event_id,event_type,occurred_at,payload,target_url,device_code,rejected_at,verdict,reason)
                SELECT event_id,event_type,occurred_at,payload,target_url,device_code,strftime('%Y-%m-%dT%H:%M:%fZ','now'),$verdict,$reason FROM cloud_outbox WHERE event_id=$id;
                DELETE FROM cloud_outbox WHERE event_id=$id;
                """;
            q.Parameters.AddWithValue("$id",e.EventId);q.Parameters.AddWithValue("$verdict",verdict);
            q.Parameters.AddWithValue("$reason",reason.Length>500?reason[..500]:reason);q.ExecuteNonQuery();
        }
        foreach(var e in done){using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="DELETE FROM cloud_outbox WHERE event_id=$id;";q.Parameters.AddWithValue("$id",e.EventId);q.ExecuteNonQuery();}
        {using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO app_settings(key,value) VALUES('cloud.last_success',$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";q.Parameters.AddWithValue("$v",DateTimeOffset.UtcNow.ToString("O"));q.ExecuteNonQuery();}
        tx.Commit();return Task.CompletedTask;
    });
    public Task<string> StatusAsync()=>IoQueue.RunAsync(()=>{
        using var c=_db.OpenConnection();using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*),COALESCE(MAX(last_error),'') FROM cloud_outbox;";
        long n;string error;using(var r=q.ExecuteReader()){r.Read();n=r.GetInt64(0);error=r.GetString(1);}
        q.CommandText="SELECT COUNT(*) FROM cloud_outbox_rejected;";var refused=Convert.ToInt64(q.ExecuteScalar());
        q.CommandText="SELECT value FROM app_settings WHERE key='cloud.last_success';";var last=q.ExecuteScalar() as string;
        var at=DateTimeOffset.TryParse(last,out var t)?t.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"):"Noch keine Übertragung";
        return Task.FromResult($"Wartende Ereignisse: {n} · Letzte Bestätigung: {at}"+(refused>0?$" · Von der Cloud abgelehnt: {refused}":"")+(error.Length>0?" · "+error:""));
    });
}

public sealed class TorCloudSyncService : IAsyncDisposable
{
    private readonly TorCloudOutbox _outbox;
    private readonly ICloudSecretProtector _secrets;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private Task? _worker;
    private int _stockRefreshRequested;
    public TorCloudSyncService(SqliteDatabase db,ICloudSecretProtector? secrets=null,HttpMessageHandler? handler=null)
    {
        _outbox=new(db);_secrets=secrets??new WindowsCloudSecretProtector();
        _http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(70)};
    }
    public static Uri Endpoint(string baseUrl,string suffix)
    {
        if(!Uri.TryCreate(baseUrl.Trim().TrimEnd('/')+"/",UriKind.Absolute,out var root)||root.UserInfo.Length>0||root.Query.Length>0||root.Fragment.Length>0||
           (root.Scheme!="https" && !(root.Scheme=="http" && root.Host is "127.0.0.1" or "localhost" or "::1" or "[::1]")))
            throw new InvalidOperationException("HTTPS erforderlich; HTTP nur auf diesem PC (127.0.0.1). Keine Zugangsdaten in der URL.");
        return new Uri(root,suffix);
    }
    public Task<TorCloudConfiguration?> ConfigurationAsync()=>_outbox.GetConfigurationAsync();
    public Task<string> StatusAsync()=>_outbox.StatusAsync();
    public Task QueueStockAsync()=>_outbox.EnqueueStockAsync();
    /// <summary>
    /// Marks article/stock data dirty without blocking the cashier UI. The worker coalesces
    /// repeated article/inventory changes into one full snapshot and also performs a
    /// periodic repair snapshot so an abrupt power loss cannot leave Cloud stock stale forever.
    /// </summary>
    public void RequestStockRefresh()=>Interlocked.Exchange(ref _stockRefreshRequested,1);
    public void Start()=>_worker??=Task.Run(LoopAsync);
    public async Task SaveAsync(string url,string code,string token,bool enabled)
    {
        await _gate.WaitAsync();try{
            url=Endpoint(url,"").AbsoluteUri;
            code=code.Trim();if(code.Length<1||code.Length>120||code.Any(ch=>!char.IsAsciiLetterOrDigit(ch)&&ch!='-'&&ch!='_'))throw new InvalidOperationException("Ungültiger Gerätecode.");
            var previous=await ConfigurationAsync();
            if(string.IsNullOrWhiteSpace(token)){
                if(previous is null||previous.BaseUrl!=url||previous.DeviceCode!=code)throw new InvalidOperationException("Gerätetoken eingeben.");
                token=_secrets.Unprotect(previous.ProtectedToken);
            }
            if(token.Length>500||token.Any(char.IsControl))throw new InvalidOperationException("Ungültiges Gerätetoken.");
            var config=new TorCloudConfiguration(url,code,_secrets.Protect(token),enabled);
            // No network requirement to pause or queue while offline. Test is a separate action.
            await _outbox.SaveConfigurationAsync(config);
            if(enabled)RequestStockRefresh();
        }finally{_gate.Release();}
    }
    private async Task<JsonElement> RequestAsync(TorCloudConfiguration config,string route,object? body,CancellationToken ct,int timeoutSeconds=20)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var request=new HttpRequestMessage(body is null?HttpMethod.Get:HttpMethod.Post,Endpoint(config.BaseUrl,route));
        request.Headers.Add("X-Device-Code",config.DeviceCode);request.Headers.Add("X-Device-Token",_secrets.Unprotect(config.ProtectedToken));
        if(body is not null)request.Content=JsonContent.Create(body);
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var buffer=new MemoryStream();var block=new byte[8192];int read;
        while((read=await stream.ReadAsync(block,timeout.Token))>0){if(buffer.Length+read>1024*1024)throw new InvalidDataException("Cloud-Antwort zu groß.");buffer.Write(block,0,read);}

        // R72.2: HTTP status is authoritative. Redirects/non-2xx are rejected
        // before any success-body parsing. This preserves the safe HTTP code
        // even when a 3xx/4xx/5xx response has an empty or non-JSON body.
        if(!response.IsSuccessStatusCode)
        {
            var error="";
            if(buffer.Length>0)
            {
                try
                {
                    var failure=JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
                    if(failure.ValueKind==JsonValueKind.Object &&
                       failure.TryGetProperty("error",out var e))
                        error=e.GetString()??"";
                }
                catch
                {
                    // Arbitrary HTML/text error bodies are deliberately not exposed.
                }
            }

            if(error.Length>500)error=error[..500];
            throw new InvalidOperationException(
                $"Cloud HTTP {(int)response.StatusCode}" +
                (string.IsNullOrWhiteSpace(error)?"":": "+error));
        }

        JsonElement result;
        try{result=JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());}
        catch{throw new InvalidDataException($"Cloud HTTP {(int)response.StatusCode}: ungültige Serverantwort.");}

        if(!result.TryGetProperty("ok",out var ok)||ok.ValueKind!=JsonValueKind.True)
            throw new InvalidDataException("Cloud-Bestätigung fehlt.");

        return result;
    }
    public async Task SendManagedMailAsync(
        string recipient,
        string subject,
        string body,
        IReadOnlyList<string> attachments,
        CancellationToken ct=default)
    {
        const long maxFileBytes=6L*1024*1024;
        const long maxTotalBytes=8L*1024*1024;
        if(attachments.Count>10)throw new InvalidOperationException("TOR Mail erlaubt maximal 10 Anhänge.");

        long total=0;
        var files=new List<object>();
        foreach(var file in attachments)
        {
            if(string.IsNullOrWhiteSpace(file)||!File.Exists(file))
                throw new InvalidOperationException("E-Mail-Anhang fehlt: "+(file??""));
            var ext=Path.GetExtension(file).ToLowerInvariant();
            if(ext is not ".pdf" and not ".csv")
                throw new InvalidOperationException("TOR Mail erlaubt nur PDF- und CSV-Anhänge.");
            var info=new FileInfo(file);
            if(info.Length>maxFileBytes)throw new InvalidOperationException($"E-Mail-Anhang ist zu groß: {info.Name}");
            total=checked(total+info.Length);
            if(total>maxTotalBytes)throw new InvalidOperationException("E-Mail-Anhänge sind zusammen zu groß (max. 8 MB).");
            var bytes=await File.ReadAllBytesAsync(file,ct);
            files.Add(new
            {
                filename=info.Name,
                data_base64=Convert.ToBase64String(bytes)
            });
        }

        var config=await ConfigurationAsync()??throw new InvalidOperationException("TOR POS Cloud zuerst unter Geräte konfigurieren und speichern.");
        await RequestAsync(config,"api/v1/devices/mail/send",new
        {
            recipient=recipient.Trim(),
            subject,
            body,
            attachments=files
        },ct,60);
    }

    public async Task<JsonElement> DeviceApiAsync(string route,object? body,CancellationToken ct=default)
    {
        var config=await ConfigurationAsync()??throw new InvalidOperationException("TOR POS Cloud zuerst unter Geräte konfigurieren und speichern.");
        return await RequestAsync(config,route,body,ct);
    }
    public async Task TestAsync(){var config=await ConfigurationAsync()??throw new InvalidOperationException("Cloud zuerst speichern.");await RequestAsync(config,"api/v1/devices/ping",null,_stop.Token);}
    public async Task SyncOnceAsync()
    {
        await _gate.WaitAsync(_stop.Token);try{
            var config=await ConfigurationAsync();if(config is null||!config.Enabled)return;
            var batch=await _outbox.PendingAsync(config);if(batch.Count==0)return;
            try{
                // C-4: partial asks the Cloud for a verdict per event instead of
                // failing the whole batch on one bad event. An older Cloud ignores
                // the flag and answers all-or-nothing as before.
                var reply=await RequestAsync(config,"api/v1/devices/sync",new {partial=true,events=batch},_stop.Token);
                if(!reply.TryGetProperty("results",out var results)||results.ValueKind!=JsonValueKind.Array||results.GetArrayLength()!=batch.Count)throw new InvalidDataException("Unvollständige Cloud-Bestätigung.");
                var byId=batch.ToDictionary(x=>x.EventId,StringComparer.Ordinal);
                var done=new List<TorCloudEvent>();var refused=new List<(TorCloudEvent,string,string)>();
                foreach(var row in results.EnumerateArray()){
                    if(!row.TryGetProperty("event_id",out var id)||!byId.Remove(id.GetString()??"",out var sent)||!row.TryGetProperty("status",out var status))throw new InvalidDataException("Cloud-Bestätigung passt nicht zum Auftrag.");
                    switch(status.GetString()){
                        case "accepted" or "duplicate":done.Add(sent);break;
                        case "rejected" or "conflict":refused.Add((sent,status.GetString()!,row.TryGetProperty("error",out var why)&&why.ValueKind==JsonValueKind.String?why.GetString()??"":""));break;
                        default:throw new InvalidDataException("Cloud-Bestätigung passt nicht zum Auftrag.");
                    }
                }
                await _outbox.SettleAsync(done,refused);
            }catch(OperationCanceledException) when(_stop.IsCancellationRequested){throw;}
            catch(Exception ex){var message=ex is InvalidOperationException?ex.Message:"Cloud nicht bestätigt. Automatische Wiederholung; Daten bleiben gespeichert.";await _outbox.CompleteAsync(batch,false,message);}
        }finally{_gate.Release();}
    }
    private async Task LoopAsync()
    {
        var heartbeat=DateTimeOffset.MinValue;
        var stockRepair=DateTimeOffset.UtcNow.AddSeconds(20);
        while(!_stop.IsCancellationRequested){
            try{
                var now=DateTimeOffset.UtcNow;
                if(now>=heartbeat){await _outbox.EnqueueHeartbeatAsync();heartbeat=now.AddSeconds(60);}

                var requested=Interlocked.Exchange(ref _stockRefreshRequested,0)==1;
                if(requested || now>=stockRepair){
                    try{
                        var config=await ConfigurationAsync();
                        if(config is not null && config.Enabled)await _outbox.EnqueueStockAsync();
                    }catch(InvalidOperationException){ /* no/changed Cloud config: keep POS local-first */ }
                    stockRepair=now.AddHours(1);
                }

                await SyncOnceAsync();
            }catch(OperationCanceledException) when(_stop.IsCancellationRequested){break;}
            catch{ /* Retain queue and retry; never copy credentials or response bodies into logs. */ }
            try{await Task.Delay(TimeSpan.FromSeconds(10),_stop.Token);}catch(OperationCanceledException){break;}
        }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if(_worker is not null){try{await _worker.WaitAsync(TimeSpan.FromSeconds(2));}catch{}}
        _http.Dispose();
    }
}
