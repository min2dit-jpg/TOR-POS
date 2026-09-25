using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

static class CloudTests
{
    public static async Task Run(string root,Action<bool,string> assert,Func<Func<Task>,string,Task> reject)
    {
        var path=Path.Combine(root,"cloud.db");var db=await SafetyDatabase.CreateCurrentAsync(path);
        var outbox=new TorCloudOutbox(db);var state=new FakeCloudState();
        await using(var cloud=new TorCloudSyncService(db,new TestSecrets(),new FakeCloudHandler(state))){
            await cloud.SaveAsync("https://cloud.example","KASSE-01","fake-device-secret",true);
            var config=(await cloud.ConfigurationAsync())!;
            assert(!config.ProtectedToken.Contains("fake-device-secret"),"Cloud configuration stores protected token, not plaintext");
            await reject(()=>cloud.SaveAsync("http://remote.example","KASSE-01","x",true),"Cloud refuses plaintext remote HTTP");
            await reject(()=>cloud.SaveAsync("https://user:pass@cloud.example","KASSE-01","x",true),"Cloud refuses credentials in URL");
            await outbox.EnqueueHeartbeatAsync();await outbox.EnqueueHeartbeatAsync();
            assert((await outbox.PendingAsync(config)).Count==1,"Offline heartbeat queue stays bounded");
            var original=(await outbox.PendingAsync(config)).Single().EventId;
            state.LoseReply=true;await cloud.SyncOnceAsync();
            assert(state.Accepted.Contains(original),"Uncertain network test server accepted event before losing response");
            assert((await cloud.StatusAsync()).Contains("Wartende Ereignisse: 1"),"Lost acknowledgement retains event durably");
            await reject(()=>cloud.SaveAsync("https://other.example","KASSE-01","x",true),"Cannot reassign pending data to another Cloud target");
        }
        // Re-open database and service as after a process restart. Retry keeps original ID.
        var reopened=new SqliteDatabase(path);var reopenedOutbox=new TorCloudOutbox(reopened);
        using(var c=reopened.OpenConnection()){using var q=c.CreateCommand();q.CommandText="UPDATE cloud_outbox SET next_attempt='';";q.ExecuteNonQuery();}
        await using(var cloud=new TorCloudSyncService(reopened,new TestSecrets(),new FakeCloudHandler(state))){
            state.LoseReply=false;await cloud.SyncOnceAsync();
            assert((await cloud.StatusAsync()).Contains("Wartende Ereignisse: 0")&&state.Accepted.Count==1,"Restart retries same event; duplicate acknowledgement clears local queue once");
            await cloud.QueueStockAsync();state.Incomplete=true;await cloud.SyncOnceAsync();
            assert((await cloud.StatusAsync()).Contains("Wartende Ereignisse: 1"),"HTTP 200 without matching event acknowledgement cannot delete queue");
            using(var c=reopened.OpenConnection()){using var q=c.CreateCommand();q.CommandText="UPDATE cloud_outbox SET next_attempt='';";q.ExecuteNonQuery();}
            state.Incomplete=false;
            var callsBeforeRedirect=state.Calls;
            state.Redirect=true;
            await cloud.SyncOnceAsync();
            var redirectStatus=await cloud.StatusAsync();
            assert(
                redirectStatus.Contains("HTTP 302") &&
                state.Calls==callsBeforeRedirect+1,
                "Redirect response is rejected once without following redirect or forwarding credentials");
            state.Redirect=false;
            await cloud.SaveAsync("https://cloud.example","KASSE-01","",false);
            var calls=state.Calls;await cloud.SyncOnceAsync();assert(state.Calls==calls,"Disabled sync retains events without network calls");
        }
        var snap=new CheckoutSnapshot("cloud-atomic",[new CartLine{ProductId=1,ProductName="Test",Quantity=2,UnitPriceCents=150,VatRate=19}],0,PaymentMethod.Cash,"test",null);
        using(var c=reopened.OpenConnection()){
            using(var tx=c.BeginTransaction()){TorCloudOutbox.EnqueueSale(c,tx,snap,900,0,DateTimeOffset.UtcNow);tx.Rollback();}
            using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM cloud_outbox WHERE event_id='sale-cloud-atomic';";
            assert(Convert.ToInt64(q.ExecuteScalar())==0,"Rolled-back sale transaction also rolls back Cloud event");
            using(var tx=c.BeginTransaction()){TorCloudOutbox.EnqueueSale(c,tx,snap,900,0,DateTimeOffset.UtcNow);tx.Commit();}
            assert(Convert.ToInt64(q.ExecuteScalar())==1,"Sale snapshot inserts exactly one durable event in caller transaction");
        }
        await reject(()=>new SaleRepository(reopened).CommitAsync(snap),"Cloud integration cannot bypass fiscal sale gate or checkout journal");
        // C-4: one event the Cloud refuses for good must not block the FIFO head.
        var c4Db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(root,"cloud-c4.db"));var c4State=new FakeCloudState();
        await using(var cloud=new TorCloudSyncService(c4Db,new TestSecrets(),new FakeCloudHandler(c4State))){
            await cloud.SaveAsync("https://cloud.example","KASSE-01","fake-device-secret",true);
            var config=(await cloud.ConfigurationAsync())!;
            using(var c=c4Db.OpenConnection()){using var tx=c.BeginTransaction();
                foreach(var id in new[]{"c4-head","c4-bad","c4-tail"})TorCloudOutbox.Insert(c,tx,config,TorCloudOutbox.Event("sale.completed",new {receipt_number=1},id));
                tx.Commit();}
            c4State.Reject.Add("c4-bad");await cloud.SyncOnceAsync();
            var status=await cloud.StatusAsync();
            assert(c4State.LastPartial&&c4State.Accepted.SetEquals(["c4-head","c4-tail"]),"C-4 till asks for per-event verdicts and good events around a refused one are delivered");
            assert(status.Contains("Wartende Ereignisse: 0")&&status.Contains("Von der Cloud abgelehnt: 1"),"C-4 refused event leaves the queue head and is shown as refused");
            using var c2=c4Db.OpenConnection();using var q=c2.CreateCommand();
            q.CommandText="SELECT verdict||'|'||reason||'|'||event_type FROM cloud_outbox_rejected WHERE event_id='c4-bad';";
            assert(q.ExecuteScalar() as string=="rejected|Ungültiger Betrag|sale.completed","C-4 refused event is parked with verdict, reason and payload");
        }
        if(OperatingSystem.IsWindows()){
            var secrets=new WindowsCloudSecretProtector();var protectedToken=secrets.Protect("roundtrip-secret");
            assert(protectedToken!="roundtrip-secret"&&secrets.Unprotect(protectedToken)=="roundtrip-secret","Windows DPAPI token roundtrip");
        }
        // Optional real local Node integration; never sends to a remote deployment.
        var url=Environment.GetEnvironmentVariable("TOR_TEST_CLOUD_URL");
        if(url is not null){
            if(!new Uri(url).IsLoopback)throw new Exception("Integration test requires loopback");
            var integrationDb=await SafetyDatabase.CreateCurrentAsync(Path.Combine(root,"cloud-integration.db"));
            await using var cloud=new TorCloudSyncService(integrationDb,new TestSecrets());
            await cloud.SaveAsync(url,"DEMO-KASSE-01","tor-demo-device-token-2026",true);await cloud.TestAsync();
            await new TorCloudOutbox(integrationDb).EnqueueHeartbeatAsync();await cloud.QueueStockAsync();
            using(var c=integrationDb.OpenConnection()){using var tx=c.BeginTransaction();TorCloudOutbox.EnqueueSale(c,tx,snap with {OperationId=Guid.NewGuid().ToString("N")},99991,0,DateTimeOffset.UtcNow);tx.Commit();}
            await cloud.SyncOnceAsync();
            assert((await cloud.StatusAsync()).Contains("Wartende Ereignisse: 0"),"Desktop HTTP client and real Node API acknowledge heartbeat, stock and receipt lines together");
        }
    }
}
sealed class TestSecrets:ICloudSecretProtector
{
    public string Protect(string value)=>Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    public string Unprotect(string value)=>System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
sealed class FakeCloudState {public bool LoseReply,Incomplete,Redirect,LastPartial;public int Calls;public HashSet<string> Accepted=new(),Reject=new();}
sealed class FakeCloudHandler(FakeCloudState state):HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        state.Calls++;
        if(state.Redirect)return new(HttpStatusCode.Found){Headers={Location=new Uri("https://unexpected.example")}};
        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        state.LastPartial=body.RootElement.TryGetProperty("partial",out var partial)&&partial.ValueKind==JsonValueKind.True;
        var results=body.RootElement.GetProperty("events").EnumerateArray().Select(e=>{
            var id=e.GetProperty("event_id").GetString()!;
            if(state.Reject.Contains(id))return new {event_id=id,status="rejected",error="Ungültiger Betrag"};
            return new {event_id=id,status=state.Accepted.Add(id)?"accepted":"duplicate",error=""};
        }).ToArray();
        if(state.LoseReply)throw new HttpRequestException("Lost reply; fake-device-secret must never be logged");
        var json=state.Incomplete?"{\"ok\":true,\"results\":[]}":JsonSerializer.Serialize(new {ok=true,results});
        return new(HttpStatusCode.OK){Content=new StringContent(json)};
    }
}
