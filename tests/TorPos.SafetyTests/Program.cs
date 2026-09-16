using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;
using TorPos.App;

var checks=0;
void Assert(bool condition,string title){if(!condition)throw new Exception("FAIL: "+title);Console.WriteLine("PASS: "+title);checks++;}
async Task Reject(Func<Task> action,string title){try{await action();}catch{Assert(true,title);return;}throw new Exception("FAIL: "+title);}
// R107: plain Reject() above treats ANY thrown exception as success - exactly
// the gap the user's own review flagged ("a test for the card-refund check
// might hit the production lock before ever reaching that check"). This
// variant additionally asserts the caught message, so a test using it proves
// it hit the INTENDED gate, not merely some earlier one (e.g. FiscalRelease).
async Task RejectMessage(Func<Task> action,string mustContain,string title)
{
    try{await action();}
    catch(Exception ex)
    {
        if(!ex.Message.Contains(mustContain))
            throw new Exception($"FAIL: {title} (threw, but message did not contain expected text \"{mustContain}\" - actual: \"{ex.Message}\")");
        Assert(true,title);
        return;
    }
    throw new Exception("FAIL: "+title);
}
var root=Path.Combine(Path.GetTempPath(),"tor-safety-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var db=await SafetyDatabase.CreateCurrentAsync(
    Path.Combine(root,"test.db"));

// R72.3: ordinary SafetyTests use SafetyDatabase so each disposable fixture
// follows the same ordered schema migration path as TOR POS itself.
// R69ReviewTests is the deliberate exception because it tests migration edges.
var journal=new CheckoutJournal(db); var sales=new SaleRepository(db);
using(var c=db.OpenConnection())
{
 using var q=c.CreateCommand();q.CommandText="PRAGMA synchronous;";Assert(Convert.ToInt32(q.ExecuteScalar())==2,"FULL synchronization");
 q.CommandText="PRAGMA journal_mode;";Assert((string?)q.ExecuteScalar()=="wal","WAL enabled");
}
var engine=new SaleEngine();engine.Add(new Product{Id=1,Name="A",BasePriceCents=2000});
var snap=new CheckoutSnapshot(Guid.NewGuid().ToString("N"),CheckoutSnapshot.CopyLines(engine.Cart),0,PaymentMethod.Card,"tester",null);
engine.ChangeQuantity(0,1);Assert(snap.TotalCents==2000 && engine.TotalCents==4000,"Checkout snapshot is a deep copy");
engine.IsReadOnly=true;engine.Clear();engine.RemoveAt(0);engine.ChangeQuantity(0,5);engine.SetQuantity(0,10);engine.SetDiscount(100);engine.Add(new Product{Id=2,Name="B"});
Assert(engine.TotalCents==4000 && engine.Cart.Count==1,"Locked cart rejects all mutation methods");
await journal.BeginAsync(snap);
await Reject(()=>journal.BeginAsync(snap),"Duplicate operation ID refused");
await Reject(()=>journal.BeginAsync(snap with {OperationId=Guid.NewGuid().ToString("N")}),"Second unresolved checkout refused globally");
await journal.MarkTerminalSubmittedAsync(snap.OperationId,"test");
var reopened=new CheckoutJournal(new SqliteDatabase(Path.Combine(root,"test.db")));
var reopenedSent=(await reopened.GetOpenAsync()).Single();
Assert(reopenedSent.State=="SENT" && reopenedSent.TerminalRequestSubmitted,"Interrupted sent payment survives reopening");
await Reject(()=>journal.MarkTerminalSubmittedAsync(snap.OperationId,"retry"),"Second submission refused by expected state");
await journal.TransitionTerminalAsync(
    snap.OperationId,"SENT","UNKNOWN","lost connection",
    PaymentTerminalOutcome.Unknown,true,"TIMEOUT","lost connection");
await Reject(()=>sales.CommitAsync(snap),"Unknown payment cannot create a sale");
await journal.ResolveAsync(snap.OperationId,"UNKNOWN",true,"tester-admin","admin proof confirmed");
await Reject(()=>sales.CommitAsync(snap),"Fiscal gate blocks real booking even after approval");
var settings=new SettingsRepository(db); var audit=new AuditLogRepository(db);
// R63: update cache maintenance must remove only disposable leftovers and keep staged/newest installers.
var updateCache=Path.Combine(root,"updates");Directory.CreateDirectory(updateCache);
var oldDownload=Path.Combine(updateCache,"aborted.download");File.WriteAllText(oldDownload,"x");File.SetLastWriteTimeUtc(oldDownload,DateTime.UtcNow.AddDays(-2));
var old1=Path.Combine(updateCache,"TOR-POS-Pro-Setup-1.exe");var old2=Path.Combine(updateCache,"TOR-POS-Pro-Setup-2.exe");var new1=Path.Combine(updateCache,"TOR-POS-Pro-Setup-3.exe");var new2=Path.Combine(updateCache,"TOR-POS-Pro-Setup-4.exe");
foreach(var f in new[]{old1,old2,new1,new2})File.WriteAllText(f,"test");
File.SetLastWriteTimeUtc(old1,DateTime.UtcNow.AddDays(-4));File.SetLastWriteTimeUtc(old2,DateTime.UtcNow.AddDays(-3));File.SetLastWriteTimeUtc(new1,DateTime.UtcNow.AddDays(-2));File.SetLastWriteTimeUtc(new2,DateTime.UtcNow.AddDays(-1));
await settings.SaveManyAsync(new Dictionary<string,string>{{"update.staged_path",old2}});
await TorUpdateService.CleanupCacheAsync(settings,default,updateCache);
Assert(!File.Exists(oldDownload) && !File.Exists(old1),"R63 removes stale update download and obsolete installer cache");
Assert(File.Exists(old2) && File.Exists(new1) && File.Exists(new2),"R63 retains staged installer and two newest update installers");
// R64 FIX1: category ordering is a sort-only, atomic transaction.
var menuRepo = new ProductRepository(db);
var menuGroupId = await menuRepo.SaveGroupAsync(new ProductGroup(0, "R64 SORT TEST", 9000));
var menuCatA = await menuRepo.SaveCategoryAsync(new Category(0, menuGroupId, "R64 A", 7m, 30, "#123456"));
var menuCatB = await menuRepo.SaveCategoryAsync(new Category(0, menuGroupId, "R64 B", 19m, 10, "#654321"));
var menuCatC = await menuRepo.SaveCategoryAsync(new Category(0, menuGroupId, "R64 C", 7m, 20, "#224466"));
await menuRepo.ReorderCategoriesAsync(new[] { menuCatC, menuCatA, menuCatB });
using (var c=db.OpenConnection())
{
    using var q=c.CreateCommand();
    q.CommandText="SELECT id,sort_order FROM categories WHERE id IN ($a,$b,$c) ORDER BY sort_order;";
    q.Parameters.AddWithValue("$a",menuCatA);q.Parameters.AddWithValue("$b",menuCatB);q.Parameters.AddWithValue("$c",menuCatC);
    using var r=q.ExecuteReader();var ids=new List<long>();var sorts=new List<int>();
    while(r.Read()){ids.Add(r.GetInt64(0));sorts.Add(r.GetInt32(1));}
    Assert(ids.SequenceEqual(new[]{menuCatC,menuCatA,menuCatB}) && sorts.SequenceEqual(new[]{0,1,2}),
        "R64 category move normalizes order atomically");
}
await Reject(()=>menuRepo.ReorderCategoriesAsync(new[]{menuCatA,999999999L,menuCatC}),
    "R64 category reorder rejects missing category");
using (var c=db.OpenConnection())
{
    using var q=c.CreateCommand();
    q.CommandText="SELECT id,sort_order FROM categories WHERE id IN ($a,$b,$c) ORDER BY sort_order;";
    q.Parameters.AddWithValue("$a",menuCatA);q.Parameters.AddWithValue("$b",menuCatB);q.Parameters.AddWithValue("$c",menuCatC);
    using var r=q.ExecuteReader();var ids=new List<long>();var sorts=new List<int>();
    while(r.Read()){ids.Add(r.GetInt64(0));sorts.Add(r.GetInt32(1));}
    Assert(ids.SequenceEqual(new[]{menuCatC,menuCatA,menuCatB}) && sorts.SequenceEqual(new[]{0,1,2}),
        "R64 failed reorder rolls back without partial menu changes");
}

// R65: Windows device waits must be bounded so BAR/KARTE cannot freeze the UI.
Assert(await CheckoutIo.WaitBoundedAsync(Task.FromResult(65), TimeSpan.FromSeconds(1)) == 65,
    "R65 bounded device wait returns completed result");
var neverCompletes = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
await Reject(
    () => CheckoutIo.WaitBoundedAsync(neverCompletes.Task, TimeSpan.FromMilliseconds(80)),
    "R65 bounded device wait times out instead of hanging checkout");

// R68: Schnellkassieren policy must never create an ambiguous tender.
var r68QuickOff = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
{
    ["pay.quick.default"] = "AUS",
    ["pay.quick.cash_exact"] = "false"
};
Assert(
    QuickCheckoutPolicy.ResolveMethod(r68QuickOff, cashEnabled: true, cardEnabled: true) is null,
    "R68 quick checkout OFF keeps explicit tender choice when both tenders are active");

var r68QuickCash = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
{
    ["pay.quick.default"] = "BAR",
    ["pay.quick.cash_exact"] = "true"
};
Assert(
    QuickCheckoutPolicy.ResolveMethod(r68QuickCash, cashEnabled: true, cardEnabled: true) == PaymentMethod.Cash,
    "R68 quick checkout resolves configured BAR without ambiguous payment method");

Assert(
    QuickCheckoutPolicy.UseExactCashWithoutDialog(
        r68QuickCash,
        PaymentMethod.Cash,
        invokedByQuickCheckout: true) &&
    !QuickCheckoutPolicy.UseExactCashWithoutDialog(
        r68QuickCash,
        PaymentMethod.Cash,
        invokedByQuickCheckout: false),
    "R68 exact cash shortcut applies only to Schnellkassieren and never changes normal F1 BAR");

var r68SingleCard = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
{
    ["pay.quick.default"] = "AUS"
};
Assert(
    QuickCheckoutPolicy.ResolveMethod(r68SingleCard, cashEnabled: false, cardEnabled: true) == PaymentMethod.Card,
    "R68 single enabled tender skips unnecessary payment-choice dialog");

// R67: deterministic performance diagnostics.
var perfR67 = new PerformanceCounters();
perfR67.RecordElapsed("checkout.test", 100);
perfR67.RecordElapsed("checkout.test", 700);
var perfSnapR67 = perfR67.SnapshotAll()["checkout.test"];
Assert(
    perfSnapR67.Count == 2 &&
    Math.Abs(perfSnapR67.AverageMs - 400) < 0.001 &&
    Math.Abs(perfSnapR67.MaxMs - 700) < 0.001 &&
    Math.Abs(perfSnapR67.LastMs - 700) < 0.001,
    "R67 performance counters retain count average max and last latency");
Assert(
    perfSnapR67.SlowCount == 1,
    "R67 performance counters count measurements above slow threshold");
var recentR67 = perfR67.Recent(10);
Assert(
    recentR67.Count == 2 &&
    recentR67[0].Milliseconds == 700 &&
    recentR67[0].IsSlow &&
    !recentR67[1].IsSlow,
    "R67 recent performance timeline preserves newest-first slow classification");
perfR67.Reset();
Assert(
    perfR67.SnapshotAll().Count == 0 && perfR67.Recent(10).Count == 0,
    "R67 performance reset clears metrics and recent timeline");

var terminal=new ZvtPaymentTerminalService(settings,audit,journal);
await Reject(()=>terminal.PayAsync(2000,snap.OperationId),"Terminal service gate blocks before network request");
// Seed the durable outcome of a prior transaction, then replay the exact request.
// This verifies the retry path without enabling the fiscal build gate.
long saleId;
using(var c=db.OpenConnection())
{
 using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
 q.CommandText="INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status) VALUES(901,$now,'CARD',2000,2000,'TEST_FIXTURE'); SELECT last_insert_rowid();";
 q.Parameters.AddWithValue("$now",DateTimeOffset.Now.ToString("O"));saleId=Convert.ToInt64(q.ExecuteScalar());
 q.Parameters.Clear();q.CommandText="UPDATE checkout_operations SET state='COMMITTED',sale_id=$sale WHERE id=$id;";q.Parameters.AddWithValue("$sale",saleId);q.Parameters.AddWithValue("$id",snap.OperationId);q.ExecuteNonQuery();tx.Commit();
}
var retry=await sales.CommitAsync(snap);
Assert(retry.Id==saleId && retry.ReceiptNumber==901,"Retry returns original sale without fiscal gate bypass for new sales");
using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM sales;";Assert(Convert.ToInt32(q.ExecuteScalar())==1,"Retry does not insert a second sale");}
Assert(await reopened.FindSaleAsync(snap.OperationId)==saleId && (await reopened.GetOpenAsync()).Count==0,"Recovery identifies committed operation");
await Reject(()=>sales.CommitAsync(snap with {DiscountCents=1}),"Same ID with altered cart is rejected");
var cash=new CheckoutSnapshot(Guid.NewGuid().ToString("N"),snap.Lines,0,PaymentMethod.Cash,"tester",null);
await journal.BeginAsync(cash);await Reject(()=>sales.CommitAsync(cash),"Cash booking also requires fiscal release");
var fifo=new List<int>();var tasks=Enumerable.Range(0,40).Select(i=>IoQueue.RunAsync(async()=>{await Task.Delay(1);fifo.Add(i);})).ToArray();
await Task.WhenAll(tasks);Assert(fifo.SequenceEqual(Enumerable.Range(0,40)),"Database queue preserves admission order");
Assert(await IoQueue.RunAsync(()=>IoQueue.RunAsync(()=>Task.FromResult(42)))==42,"Nested repository calls do not deadlock");
var path=Path.Combine(root,"cart.json");
await Task.WhenAll(RecoveryFiles.WriteAsync(path,"old"),RecoveryFiles.WriteAsync(path,"new"),RecoveryFiles.WriteAsync(path,null));
Assert(!File.Exists(path),"Ordered recovery writes cannot resurrect a cleared cart");
await RecoveryFiles.WriteAsync(path,"damaged");await RecoveryFiles.QuarantineAsync(path);
Assert(File.Exists(path+".blocked") && Directory.GetFiles(root,"cart.json.damaged-*").Length==1,"Corrupt recovery retained with durable lock marker");
await Reject(()=>new AuthenticationService(db).ChangeAdminCredentialsAsync("admin","short","1234"),"Weak replacement admin credentials refused");
// A thrown queued operation must not terminate the worker.
await Reject(()=>IoQueue.RunAsync(()=>Task.FromException(new IOException("test"))),"Queue returns IO failure");
Assert(await IoQueue.RunAsync(()=>Task.FromResult(true)),"Queue continues after a failure");
var printStore=new PrintJobJournal(Path.Combine(root,"prints"));
var nativeCompletion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var nativeCalls=0;
var printer=new StarMcPrint3PrinterService(printStore,TimeSpan.FromMilliseconds(200),()=>
{ Interlocked.Increment(ref nativeCalls);entered.TrySetResult();return nativeCompletion.Task; });
var errorJob=new ErrorSlipPrintJob(DateTimeOffset.Now,"TEST","Test failure","TEST-1","1","tester");
var firstPrint=printer.PrintErrorSlipAsync(errorJob,"fake");
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var secondPrint=printer.PrintErrorSlipAsync(errorJob with {ErrorId="TEST-2"},"fake");
await Reject(()=>firstPrint.WaitAsync(TimeSpan.FromSeconds(5)),"Printer timeout surfaces without waiting for native completion");
await Reject(()=>secondPrint.WaitAsync(TimeSpan.FromSeconds(5)),"Already queued job blocked after earlier timeout");
Assert(nativeCalls==1,"No second native print call after timeout");
await printer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
Assert(!nativeCompletion.Task.IsCompleted,"Shutdown finishes while native print is still pending");
nativeCompletion.TrySetResult();
var recoveredPrinter=new StarMcPrint3PrinterService(printStore,TimeSpan.FromSeconds(1),()=> { Interlocked.Increment(ref nativeCalls);return Task.CompletedTask; });
await Reject(()=>recoveredPrinter.PrintErrorSlipAsync(errorJob,"fake"),"Restart preserves unknown print state without replay");
var unresolvedPrints=await printStore.GetUncertainAsync();
Assert(nativeCalls==1 && unresolvedPrints.Any(x=>x.State=="UNKNOWN") && unresolvedPrints.Any(x=>x.State=="NOT_SUBMITTED"),"Unknown and blocked print jobs both retained for review");
await recoveredPrinter.ResolveQueueAsync("admin=tester; checked Windows queue and receipt");
await recoveredPrinter.PrintErrorSlipAsync(errorJob,"fake");
Assert(nativeCalls==2 && (await printStore.GetUncertainAsync()).Count==0,"Explicit print review unlocks only subsequent requests");
await recoveredPrinter.DisposeAsync();
var testSnapshot = new CheckoutSnapshot("test-print", new[] { new CartLine { ProductName="Artikel", Quantity=2, UnitPriceCents=300, VatRate=19 } }, 100, PaymentMethod.Cash, "tester", null);
var testReceipt = SimulationReceipt.Create(testSnapshot, "TOR", "Berlin", 1000);
Assert(testReceipt.TotalCents==500 && testReceipt.TenderedCents==1000 && testReceipt.ChangeCents==500, "Simulation receipt contains discounted total and cash change");
Assert(testReceipt.ReceiptNumber==0 && testReceipt.FiscalTestMode && testReceipt.Header.Contains("KEIN FISKALBELEG") && testReceipt.TseTransactionNumber=="", "Simulation cannot impersonate a numbered fiscal receipt");
testSnapshot.Lines[0].Quantity=99;
Assert(testReceipt.Lines[0].Quantity==2, "Queued simulation receipt owns an independent cart snapshot");
var cardReceipt=SimulationReceipt.Create(testSnapshot with { Method=PaymentMethod.Card }, "TOR", "", 999999);
Assert(cardReceipt.TenderedCents==0 && cardReceipt.ChangeCents==0 && cardReceipt.PaymentLabel.Contains("SIMULATION"), "Card simulation has no cash tender or implied real payment");
await Reject(()=>Task.FromResult(SimulationReceipt.Create(testSnapshot,"TOR","",0)), "Underpaid test cash receipt refused");
long initialHistoryCount;
using (var c = db.OpenConnection())
{
    using var q = c.CreateCommand(); q.CommandText = "SELECT COUNT(*) FROM sales;";
    initialHistoryCount = Convert.ToInt64(q.ExecuteScalar());
}
// Synthetic historical rows are inserted only in this disposable test database.
using (var c = db.OpenConnection())
{
    using var q = c.CreateCommand();
    q.CommandText = """
        INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents) VALUES
        (91001,'2025-03-30T00:00:00+01:00','CASH',500,500),
        (91002,'2025-03-30T23:59:59+02:00','CARD',700,700),
        (91003,'2025-03-31T00:00:00+02:00','CARD',900,900);
        """;
    q.ExecuteNonQuery();
}
var historyDay=new DateOnly(2025,3,30);
var history=await sales.SearchHistoryAsync(historyDay,historyDay);
Assert(history.Count==2 && history[0].ReceiptNumber==91002, "Archive includes both date boundaries across DST and sorts newest first");
Assert((await sales.SearchHistoryAsync(historyDay,historyDay,method:PaymentMethod.Cash)).Single().ReceiptNumber==91001, "Archive filters cash receipts");
Assert((await sales.SearchHistoryAsync(historyDay,historyDay,91002,PaymentMethod.Card)).Single().TotalCents==700, "Archive combines number and payment filters");
Assert((await sales.SearchHistoryAsync(historyDay,historyDay,91003)).Count==0, "Archive excludes receipt outside selected calendar day");
await Reject(()=>sales.SearchHistoryAsync(historyDay.AddDays(1),historyDay), "Archive rejects reversed dates");
await Reject(()=>sales.SearchHistoryAsync(historyDay,historyDay,0), "Archive rejects invalid receipt number");
using (var c=db.OpenConnection())
{
    using var q=c.CreateCommand();
    q.CommandText="""
        WITH RECURSIVE n(v) AS (SELECT 1 UNION ALL SELECT v+1 FROM n WHERE v<205)
        INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents)
        SELECT 92000+v,'2025-03-29T12:00:00+01:00','CASH',100,100 FROM n;
        """;
    q.ExecuteNonQuery();
}
Assert((await sales.SearchHistoryAsync(historyDay.AddDays(-1),historyDay)).Count==201, "Archive bounds results with one overflow sentinel");
using (var c=db.OpenConnection())
{
    using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM sales;";
    Assert(Convert.ToInt64(q.ExecuteScalar())==initialHistoryCount+208, "Archive searches leave historical sales intact");
}
var sumupHandler = new FakeSumUpHandler();
using (var sumup = new SumUpConnectionService(sumupHandler))
{
    var readers = await sumup.ListAsync("MTEST", "fake-secret");
    Assert(readers.Single().Id=="rdr_test" && readers.Single().PairingStatus=="paired", "SumUp parses reader list without claiming online state");
    var status = await sumup.StatusAsync("MTEST", "fake-secret", "rdr_test");
    Assert(status.Contains("OFFLINE") && status.Contains("2026-09-07"), "SumUp preserves offline state and last activity");
    var paired = await sumup.PairAsync("MTEST", "fake-secret", "ABCD1234");
    Assert(paired.PairingStatus=="processing" && sumupHandler.PairPosts==1, "SumUp pairing sent once and processing is not treated as confirmed");
    var checkout = await sumup.StartOneEuroDeviceTestAsync("MTEST", "fake-secret", "rdr_test");
    Assert(checkout.CheckoutId=="chk_test" && checkout.AmountCents==100 && sumupHandler.CheckoutPosts==1, "SumUp device test sends exactly EUR 1.00 once");
    await sumup.TerminateCheckoutAsync("MTEST", "fake-secret", "rdr_test");
    Assert(sumupHandler.TerminatePosts==1, "SumUp device test terminate endpoint is called once");
    Assert(sumupHandler.OnlyExpectedCalls, "SumUp test flow calls only list, status, pair, checkout and terminate endpoints");
    await Reject(()=>sumup.ListAsync("../other", "fake-secret"), "SumUp refuses merchant path injection");
    await Reject(()=>sumup.StatusAsync("MTEST", "fake-secret", "../checkout"), "SumUp refuses reader path injection");
    await Reject(()=>sumup.StartOneEuroDeviceTestAsync("MTEST", "fake-secret", "../checkout"), "SumUp refuses test-checkout reader path injection");
    await Reject(()=>sumup.PairAsync("MTEST", "fake-secret", "123"), "SumUp validates pairing-code length");
    sumupHandler.Fail=true;
    try { await sumup.ListAsync("MTEST", "fake-secret"); throw new Exception("Expected HTTP error"); }
    catch (InvalidOperationException ex) { Assert(ex.Message.Contains("401") && !ex.ToString().Contains("fake-secret"), "SumUp error suppresses credential-echoing response body"); }
    Assert(sumupHandler.PairPosts==1 && sumupHandler.CheckoutPosts==1 && sumupHandler.TerminatePosts==1, "SumUp errors do not retry prior write operations");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    await Reject(()=>sumup.ListAsync("MTEST", "fake-secret", canceled.Token), "SumUp connection honors cancellation");
}
await CloudTests.Run(root,Assert,Reject);
await R48ReviewTests.Run(root,Assert,Reject);
await R49ReviewTests.Run(root,Assert,Reject);
await R50ReviewTests.Run(root,Assert,Reject);
await R51ReviewTests.Run(root,Assert,Reject);
await R53ReviewTests.Run(root,Assert,Reject);
await R54ReviewTests.Run(root,Assert,Reject);
await R55ReviewTests.Run(root,Assert,Reject);
await R56ReviewTests.Run(root,Assert);
await R57ReviewTests.Run(root,Assert);
await R58ReviewTests.Run(Assert);
await R59ReviewTests.Run(Assert);
await R60ReviewTests.Run(Assert);
await R69ReviewTests.Run(root,Assert,Reject);
await R70ReviewTests.Run(root,Assert,Reject);
await R71ReviewTests.Run(root,Assert,Reject);
await R72ReviewTests.Run(root,Assert,Reject);
await R73ReviewTests.Run(root,Assert,Reject);
await R74ReviewTests.Run(root,Assert,Reject);
await R75ReviewTests.Run(root,Assert,Reject);
await R76ReviewTests.Run(root,Assert,Reject);
await R77ReviewTests.Run(root,Assert,Reject);
await R78ReviewTests.Run(root,Assert,Reject);
await R79ReviewTests.Run(root,Assert,Reject);
await R80ReviewTests.Run(root,Assert,Reject);
await R81ReviewTests.Run(root,Assert,Reject);
await R82ReviewTests.Run(root,Assert,Reject);
await R83ReviewTests.Run(root,Assert,Reject);
await R85ReviewTests.Run(Assert,Reject);
await R86ReviewTests.Run(Assert,Reject);
await R88ReviewTests.Run(root,Assert,Reject);
await R89ReviewTests.Run(root,Assert,Reject);
await R90ReviewTests.Run(root,Assert,Reject);
await R91ReviewTests.Run(root,Assert,Reject);
await R92ReviewTests.Run(root,Assert,Reject);
await R93ReviewTests.Run(root,Assert,Reject);
await R94ReviewTests.Run(root,Assert,Reject);
await R95ReviewTests.Run(Assert,Reject);
await R96ReviewTests.Run(root,Assert,Reject);
await R97ReviewTests.Run(root,Assert,Reject);
await R98ReviewTests.Run(root,Assert,Reject);
await R99ReviewTests.Run(root,Assert,Reject);
await R100ReviewTests.Run(root,Assert);
await R101ReviewTests.Run(root,Assert,Reject);
await R102ReviewTests.Run(root,Assert,Reject);
await R103ReviewTests.Run(root,Assert,Reject);
await R106ReviewTests.Run(root,Assert,Reject);
await R107ReviewTests.Run(root,Assert,Reject,RejectMessage);
await R108ReviewTests.Run(Assert);
await R113ReviewTests.Run(root,Assert,RejectMessage);
await R114ReviewTests.Run(Assert);
await R115ReviewTests.Run(root,Assert);
await R116ReviewTests.Run(Assert);
await R117ReviewTests.Run(root,Assert);
await R118R119ReviewTests.Run(root,Assert);
await R121ReviewTests.Run(Assert);
await R122ReviewTests.Run(root,Assert,Reject);

const int ExpectedSafetyChecks = 646;

if (checks != ExpectedSafetyChecks)
{
    throw new Exception(
        $"SAFETY BASELINE MISMATCH: expected {ExpectedSafetyChecks}, actual {checks}. " +
        "Update the reviewed baseline intentionally before accepting a changed test count.");
}

Console.WriteLine($"ALL {checks} CHECKS PASSED");
SqliteConnection.ClearAllPools();Directory.Delete(root,true);

sealed class FakeSumUpHandler : System.Net.Http.HttpMessageHandler
{
    public int PairPosts; public int CheckoutPosts; public int TerminatePosts; public bool Fail; public bool OnlyExpectedCalls=true;
    protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path=request.RequestUri!.AbsolutePath;
        var allowed = path=="/v0.1/merchants/MTEST/readers" ||
            path=="/v0.1/merchants/MTEST/readers/rdr_test/status" ||
            path=="/v0.1/merchants/MTEST/readers/rdr_test/checkout" ||
            path=="/v0.1/merchants/MTEST/readers/rdr_test/terminate";
        if (request.RequestUri.Host!="api.sumup.com" || request.RequestUri.Scheme!="https" || !allowed) OnlyExpectedCalls=false;
        if (Fail) return new(System.Net.HttpStatusCode.Unauthorized) { Content=new System.Net.Http.StringContent("fake-secret") };

        if (request.Method==System.Net.Http.HttpMethod.Post && path.EndsWith("/terminate"))
        {
            TerminatePosts++;
            return new(System.Net.HttpStatusCode.NoContent);
        }

        string json;
        if (request.Method==System.Net.Http.HttpMethod.Post && path.EndsWith("/checkout"))
        {
            CheckoutPosts++;
            var body=await request.Content!.ReadAsStringAsync(ct);
            if (!body.Contains("\"currency\":\"EUR\"") || !body.Contains("\"minor_unit\":2") || !body.Contains("\"value\":100")) OnlyExpectedCalls=false;
            json="""{"data":{"checkout_id":"chk_test","client_transaction_id":"tx_test"}}""";
        }
        else if (request.Method==System.Net.Http.HttpMethod.Post)
        {
            PairPosts++;
            var body=await request.Content!.ReadAsStringAsync(ct);
            if (!body.Contains("ABCD1234") || body.Contains("total_amount")) OnlyExpectedCalls=false;
            json="""{"id":"rdr_test","name":"TOR","status":"processing"}""";
        }
        else if(path.EndsWith("/status")) json="""{"data":{"status":"OFFLINE","state":"IDLE","connection_type":"Wi-Fi","last_activity":"2026-09-07T10:00:00Z"}}""";
        else json="""{"items":[{"id":"rdr_test","name":"TOR","status":"paired"}]}""";
        return new(System.Net.HttpStatusCode.OK) { Content=new System.Net.Http.StringContent(json) };
    }
}
