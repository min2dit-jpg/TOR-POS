using Microsoft.Data.Sqlite;
using System.Text.Json;
using TorPos.Core;
namespace TorPos.Infrastructure;

public sealed class OrderPrintOutbox(SqliteDatabase db)
{
 public static async Task AuditAsync(SqliteConnection c,SqliteTransaction tx,long id,string actor,string action,CancellationToken ct){
  using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO audit_log(created_at,actor,event_type,entity_type,entity_id,details) VALUES($at,$actor,$action,'PARKED_RECEIPT',$id,'Order transaction');";
  q.Parameters.AddWithValue("$at",DateTimeOffset.Now.ToString("O"));q.Parameters.AddWithValue("$actor",actor);q.Parameters.AddWithValue("$action",action);q.Parameters.AddWithValue("$id",id);await q.ExecuteNonQueryAsync(ct);
 }
 public static async Task EnqueueAsync(SqliteConnection c,SqliteTransaction tx,long orderId,string action,CancellationToken ct)
 {
  var settings=new Dictionary<string,string>();
  using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="SELECT key,value FROM app_settings;";using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))settings[r.GetString(0)]=r.GetString(1);}
  bool Flag(string key,bool fallback=false)=>bool.TryParse(settings.GetValueOrDefault(key),out var value)?value:fallback;
  var kitchen=Flag("device.kitchen_printer.enabled")&&Flag("device.kitchen_printer.auto_print",true);
  var pickup=action=="ACCEPT"&&Flag("imbiss.pickup_slip.auto_print",true)&&Flag("device.receipt_printer.enabled");
  if(!kitchen&&!pickup)return;
  long park,number;string actor,note;
  using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText="SELECT park_number,pickup_number,created_by,order_note FROM parked_receipts WHERE id=$id;";q.Parameters.AddWithValue("$id",orderId);using var r=await q.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new InvalidOperationException("Bestellung fehlt.");park=r.GetInt64(0);number=r.GetInt64(1);actor=r.GetString(2);note=r.GetString(3);}
  var at=DateTimeOffset.Now;
  async Task Store(string printer,KitchenPrintJob? job,PickupSlipPrintJob? slip){
   if(string.IsNullOrWhiteSpace(printer))throw new InvalidOperationException("Aktivierter Bestelldrucker ist nicht ausgewählt. Einstellungen prüfen.");
   var record=new PrintJobRecord(Guid.NewGuid().ToString("N"),"QUEUED",printer,null,null,"",Kitchen:job,PickupSlip:slip);
   using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="INSERT INTO order_print_outbox(id,order_id,action,payload,state,created_at) VALUES($id,$order,$action,$payload,'PENDING',$at);";
   q.Parameters.AddWithValue("$id",record.Id);q.Parameters.AddWithValue("$order",orderId);q.Parameters.AddWithValue("$action",action);q.Parameters.AddWithValue("$payload",JsonSerializer.Serialize(record));q.Parameters.AddWithValue("$at",at.ToString("O"));await q.ExecuteNonQueryAsync(ct);
  }
  if(kitchen){
   // R76: jede Bestellzeile trägt die Küchenstation ihrer Warengruppe (leer = Standard),
   // damit jede Station an ihren eigenen konfigurierten Drucker geroutet werden kann.
   var lines=new List<(long Id,string Name,decimal Quantity,string Station)>();
   using(var q=c.CreateCommand()){q.Transaction=tx;q.CommandText=
    "SELECT pri.product_id,pri.product_name,pri.variant_name,CASE WHEN COALESCE(pri.quantity_milli,0)<>0 THEN pri.quantity_milli ELSE CAST(ROUND(pri.quantity*1000.0) AS INTEGER) END,COALESCE(k.station,'') "+
    "FROM parked_receipt_items pri "+
    "LEFT JOIN products p ON p.id=pri.product_id "+
    "LEFT JOIN category_kitchen_data k ON k.category_id=p.category_id "+
    "WHERE pri.parked_receipt_id=$id ORDER BY pri.id;";
    q.Parameters.AddWithValue("$id",orderId);using var r=await q.ExecuteReaderAsync(ct);
    while(await r.ReadAsync(ct))lines.Add((r.GetInt64(0),r.GetString(1)+(string.IsNullOrWhiteSpace(r.GetString(2))?"":" · "+r.GetString(2)),QuantityStorage.FromMilli(r.GetInt64(3)),KitchenStations.Normalize(r.GetString(4))));}
   var grouped=new Dictionary<string,List<KitchenPrintLine>>();
   void Add(string station,KitchenPrintLine line){if(!grouped.TryGetValue(station,out var list))grouped[station]=list=new List<KitchenPrintLine>();list.Add(line);}
   foreach(var line in lines){
    Add(line.Station,new(line.Name,line.Quantity));
    using var q=c.CreateCommand();q.Transaction=tx;q.CommandText="SELECT p.name,CASE WHEN COALESCE(i.quantity_milli,0)<>0 THEN i.quantity_milli ELSE CAST(ROUND(i.quantity*1000.0) AS INTEGER) END FROM product_combo_items i JOIN products p ON p.id=i.component_product_id WHERE i.product_id=$id ORDER BY i.sort_order;";q.Parameters.AddWithValue("$id",line.Id);using var r=await q.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))Add(line.Station,new(r.GetString(0),line.Quantity*QuantityStorage.FromMilli(r.GetInt64(1)),true));
   }
   var instruction=action switch{"CHANGE"=>"ÄNDERUNG · VOLLSTÄNDIGER AKTUELLER AUFTRAG · ERSETZT VORHERIGEN BON","CANCEL"=>"STORNO · NICHT ZUBEREITEN",_=>""};
   var baseNote=string.Join(" · ",new[]{instruction,note}.Where(x=>!string.IsNullOrWhiteSpace(x)));
   string ResolveStationPrinter(string station){
    if(station!=KitchenStations.None){
     var prefix=KitchenStations.SettingsPrefix(station);
     if(Flag(prefix+".enabled")){
      var stationPrinter=settings.GetValueOrDefault(prefix+".name","");
      if(!string.IsNullOrWhiteSpace(stationPrinter))return stationPrinter;
     }
    }
    return settings.GetValueOrDefault("device.kitchen_printer.name","");
   }
   foreach(var (station,printLines) in grouped){
    var stationSuffix=station==KitchenStations.None?"":" · "+KitchenStations.DisplayName(station);
    await Store(ResolveStationPrinter(station),new(at,number,park,actor,printLines,baseNote+stationSuffix),null);
   }
  }
  if(pickup&&number>0)await Store(settings.GetValueOrDefault("device.receipt_printer.name",""),null,new(at,number,park,settings.GetValueOrDefault("company.name","TOR POS")));
 }
 public Task<IReadOnlyList<PrintJobRecord>> PendingAsync()=>IoQueue.RunAsync<IReadOnlyList<PrintJobRecord>>(async()=>{
  using var c=db.OpenConnection();using var q=c.CreateCommand();q.CommandText="SELECT payload FROM order_print_outbox WHERE state='PENDING' ORDER BY rowid LIMIT 25;";using var r=await q.ExecuteReaderAsync();var records=new List<PrintJobRecord>();while(await r.ReadAsync())records.Add(JsonSerializer.Deserialize<PrintJobRecord>(r.GetString(0))??throw new InvalidDataException("Bestelldruck beschädigt."));return records;
 });
 public Task ForwardedAsync(string id)=>IoQueue.RunAsync(async()=>{using var c=db.OpenConnection();using var q=c.CreateCommand();q.CommandText="UPDATE order_print_outbox SET state='HANDED_OVER' WHERE id=$id AND state='PENDING';";q.Parameters.AddWithValue("$id",id);await q.ExecuteNonQueryAsync();});

 // R119: a job is retried a bounded number of times and then parked as FAILED
 // so the queue keeps moving. Previously the dispatcher simply stopped at the
 // first failing job, so one ticket addressed to a printer that no longer
 // exists blocked every later kitchen ticket for good.
 public const int MaxAttempts = 5;

 /// <summary>Counts one failed attempt. Returns true once the job has been given up on.</summary>
 public Task<bool> FailedAsync(string id,string error)=>IoQueue.RunAsync(async()=>{
  using var c=db.OpenConnection();using var tx=c.BeginTransaction();
  int attempts;
  using(var bump=c.CreateCommand()){
   bump.Transaction=tx;
   bump.CommandText="UPDATE order_print_outbox SET attempts=attempts+1,last_error=$err WHERE id=$id AND state='PENDING'; SELECT COALESCE((SELECT attempts FROM order_print_outbox WHERE id=$id),0);";
   bump.Parameters.AddWithValue("$id",id);
   bump.Parameters.AddWithValue("$err",(error??"").Length>500?error![..500]:error??"");
   attempts=Convert.ToInt32(await bump.ExecuteScalarAsync());
  }
  var giveUp=attempts>=MaxAttempts;
  if(giveUp){
   using var park=c.CreateCommand();park.Transaction=tx;
   park.CommandText="UPDATE order_print_outbox SET state='FAILED' WHERE id=$id AND state='PENDING';";
   park.Parameters.AddWithValue("$id",id);
   await park.ExecuteNonQueryAsync();
  }
  tx.Commit();
  return giveUp;
 });

 /// <summary>Jobs the queue gave up on, newest first - shown in the Diagnose window.</summary>
 public Task<IReadOnlyList<(string Id,long OrderId,string Action,int Attempts,string LastError,string CreatedAt)>> FailedJobsAsync()=>
  IoQueue.RunAsync<IReadOnlyList<(string,long,string,int,string,string)>>(async()=>{
   using var c=db.OpenConnection();using var q=c.CreateCommand();
   q.CommandText="SELECT id,order_id,action,attempts,last_error,created_at FROM order_print_outbox WHERE state='FAILED' ORDER BY rowid DESC LIMIT 50;";
   using var r=await q.ExecuteReaderAsync();
   var rows=new List<(string,long,string,int,string,string)>();
   while(await r.ReadAsync())rows.Add((r.GetString(0),r.GetInt64(1),r.GetString(2),r.GetInt32(3),r.GetString(4),r.GetString(5)));
   return rows;
  });

 /// <summary>Puts a given-up job back into the queue after the printer was fixed.</summary>
 public Task RetryFailedAsync(string id)=>IoQueue.RunAsync(async()=>{
  using var c=db.OpenConnection();using var q=c.CreateCommand();
  q.CommandText="UPDATE order_print_outbox SET state='PENDING',attempts=0,last_error='' WHERE id=$id AND state='FAILED';";
  q.Parameters.AddWithValue("$id",id);
  await q.ExecuteNonQueryAsync();
 });
}
public sealed class OrderPrintDispatcher(OrderPrintOutbox outbox,PrintJobJournal journal,Func<PrintJobRecord,Task> submit,Action<Exception>? onError=null) : IAsyncDisposable
{
 readonly SemaphoreSlim wake=new(0,1);
 public void Notify(){try{wake.Release();}catch(SemaphoreFullException){/* Coalesced wake-up. */}}
 readonly SemaphoreSlim gate=new(1,1);readonly CancellationTokenSource stop=new();Task? worker;string? lastError;
 public void Start()=>worker??=Task.Run(async()=>{while(!stop.IsCancellationRequested){try{await DispatchOnceAsync();}catch(Exception ex){onError?.Invoke(ex);}try{await wake.WaitAsync(TimeSpan.FromSeconds(5),stop.Token);}catch(OperationCanceledException){break;}}});
 public async Task DispatchOnceAsync(){await gate.WaitAsync();try{foreach(var record in await outbox.PendingAsync()){
  if(stop.IsCancellationRequested)break;
  // An existing journal record means ownership already transferred. Never resubmit it.
  if(await journal.GetAsync(record.Id) is not null){await outbox.ForwardedAsync(record.Id);continue;}
  try{await submit(record);lastError=null;}
  catch(Exception ex){
   if(lastError!=ex.Message)onError?.Invoke(ex);
   lastError=ex.Message;
   // Ownership may still have transferred despite the exception - then the
   // job is done, not failed.
   if(await journal.GetAsync(record.Id) is not null){await outbox.ForwardedAsync(record.Id);continue;}
   // R119: count the attempt and move on once the job is given up on, instead
   // of stopping the whole queue at the first failure forever.
   if(await outbox.FailedAsync(record.Id,ex.Message))continue;
   break;
  }
  if(await journal.GetAsync(record.Id) is null)throw new InvalidOperationException("Drucker hat keinen dauerhaften Auftrag bestätigt.");
  await outbox.ForwardedAsync(record.Id);
 }}finally{gate.Release();}}
 public async ValueTask DisposeAsync(){stop.Cancel();if(worker is not null)try{await worker.WaitAsync(TimeSpan.FromSeconds(2));}catch(TimeoutException){} }
}
