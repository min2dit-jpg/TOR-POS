using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;
using TorPos.App;

// R127: TOR_TEST_CULTURE runs the whole suite under another Windows culture.
// The GitHub runner is en-US while the development PC is de-DE, and a test
// that builds its expectation with the current culture passed here and failed
// there (R71 after R123 - fixed in PR #3). Run with en-US and tr-TR before
// pushing; tr-TR also catches the Turkish dotted/dotless I casing trap.
if (Environment.GetEnvironmentVariable("TOR_TEST_CULTURE") is { Length: > 0 } testCulture)
{
    var culture = System.Globalization.CultureInfo.GetCultureInfo(testCulture);
    System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
    System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
    System.Globalization.CultureInfo.CurrentCulture = culture;
    System.Globalization.CultureInfo.CurrentUICulture = culture;
    Console.WriteLine($"Test culture: {culture.Name}");
}

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

Assert(
    InstallationEdition.DisplayName("KIOSK") == "Einzelhandel",
    "customer-facing KIOSK edition is labeled Einzelhandel without changing the technical code");
Assert(
    InstallationEdition.DisplayName("IMBISS") == "Gastronomie",
    "customer-facing IMBISS edition is labeled Gastronomie without changing the technical code");

var cloudGateRejected = false;
try
{
    TseProviderCatalog.RequireProviderRelease(
        TseProviderCatalog.FiskalyDirectCloud);
}
catch (InvalidOperationException ex)
{
    cloudGateRejected = ex.Message.Contains(
        "Cloud-TSE",
        StringComparison.OrdinalIgnoreCase);
}

if (!cloudGateRejected ||
    CloudTseRelease.FiskaltrustValidated ||
    CloudTseRelease.FiskalyValidated ||
    CloudTseRelease.DeutscheFiskalValidated ||
    FiscalRelease.MissingQualifications().Any(x =>
        x.Contains("Cloud", StringComparison.OrdinalIgnoreCase)))
{
    throw new Exception(
        "FAIL: Cloud TSE provider release gates must remain independent from the physical/global release qualification set.");
}

var localMiddleware =
    TseProviderCatalog.Get(
        TseProviderCatalog.FiskaltrustLocalMiddleware);
var directFiskaly =
    TseProviderCatalog.Get(
        TseProviderCatalog.FiskalyDirectCloud);

if (localMiddleware.Transport !=
        TseProviderTransport.LocalMiddleware ||
    localMiddleware.RequiresCloudReleaseGate ||
    directFiskaly.Transport !=
        TseProviderTransport.DirectCloudApi ||
    !directFiskaly.RequiresCloudReleaseGate)
{
    throw new Exception(
        "FAIL: Local fiskaltrust Middleware and direct Cloud TSE providers must remain distinct transports.");
}

var cloudGateRunsBeforeConfiguration = false;
try
{
    DirectCloudTseConfigurationPolicy.RequireForFiscalUse(
        new DirectCloudTseConfiguration(
            TseProviderCatalog.FiskalyDirectCloud,
            "",
            "",
            "",
            "",
            ""));
}
catch (InvalidOperationException ex)
{
    cloudGateRunsBeforeConfiguration = ex.Message.Contains(
        "Cloud-TSE",
        StringComparison.OrdinalIgnoreCase);
}

if (!cloudGateRunsBeforeConfiguration)
{
    throw new Exception(
        "FAIL: Direct Cloud TSE provider release gate must run after provider selection but before endpoint/tenant/TSS validation.");
}

var countingCloudClient =
    new CountingDirectCloudTseClient();
var blockedCloudProvider =
    new DirectCloudTseProvider(
        TseProviderCatalog.FiskalyDirectCloud,
        countingCloudClient);

var cloudProviderRejectedBeforeClient = false;
try
{
    await blockedCloudProvider.ProbeAsync();
}
catch (InvalidOperationException ex)
{
    cloudProviderRejectedBeforeClient =
        ex.Message.Contains(
            "Cloud-TSE",
            StringComparison.OrdinalIgnoreCase);
}

if (!cloudProviderRejectedBeforeClient ||
    countingCloudClient.Calls != 0 ||
    blockedCloudProvider.TransactionAvailable)
{
    throw new Exception(
        "FAIL: Unvalidated direct Cloud TSE provider must be rejected before any provider client call.");
}

var cloudVorgangEngine =
    new SaleEngine();
cloudVorgangEngine.Add(
    new Product
    {
        Id = 9_900_001,
        Name = "Cloud TSE Identity Test",
        BasePriceCents = 100
    });

var cloudVorgangTracker =
    new TseVorgangCartTracker();

var cloudVorgangStart =
    cloudVorgangTracker.OnCartChanged(
        cloudVorgangEngine.Cart,
        0,
        imHaus: false,
        fiscal: true,
        DateTimeOffset.UtcNow);

var canonicalCloudTransactionId =
    DirectCloudTransactionIdentity.RequireUuidV4(
        cloudVorgangStart.VorgangId);

if (cloudVorgangStart.Kind !=
        TseVorgangActionKind.Start ||
    canonicalCloudTransactionId !=
        cloudVorgangStart.VorgangId ||
    cloudVorgangTracker.VorgangId !=
        canonicalCloudTransactionId)
{
    throw new Exception(
        "FAIL: TOR Vorgang identity must be a stable UUIDv4 suitable for direct Cloud TSE retry idempotency.");
}

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
await Reject(()=>new AuthenticationService(db).ChangeAdminCredentialsAsync("admin","abc","1234"),"Replacement admin password below the 4-character floor refused");
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
Assert((await sales.SearchHistoryAsync(historyDay.AddDays(-1),historyDay)).Count==201, "Archive bounds multi-day results with one overflow sentinel");
Assert(
    (await sales.SearchHistoryAsync(historyDay.AddDays(-1), historyDay.AddDays(-1))).Count == 205,
    "Bon-Historie loads every receipt of a single day without a search/pagination step");

var oldOriginal = (await sales.SearchHistoryAsync(historyDay, historyDay, 91001)).Single();
var oldReversalReason = await sales.CheckReversalAllowedAsync(oldOriginal.Id, forFullStorno: true);
Assert(
    oldReversalReason?.Contains("Verkaufstag", StringComparison.OrdinalIgnoreCase) == true,
    "BON STORNO / Teilretoure pre-check blocks receipts from a previous day before any terminal refund");
await RejectMessage(
    () => sales.RecordStornoAsync(oldOriginal.Id, "tester", "test"),
    "Verkaufstag",
    "Authoritative BON STORNO repository gate rejects a previous-day receipt");
await RejectMessage(
    () => sales.RecordReturnAsync(oldOriginal.Id, new[] { new ReturnLineRequest(999999, 1m) }, "tester", "test"),
    "Verkaufstag",
    "Authoritative Teilretoure repository gate rejects a previous-day receipt");

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
await R103ReviewTests.Run(Assert);
await R106ReviewTests.Run(root,Assert,Reject);
await R107ReviewTests.Run(root,Assert,Reject,RejectMessage);
await R108ReviewTests.Run(Assert);
await R113ReviewTests.Run(root,Assert,RejectMessage);
await R114ReviewTests.Run(Assert);
await R116ReviewTests.Run(Assert);
await R117ReviewTests.Run(root,Assert);
await R118R119ReviewTests.Run(root,Assert);
await R121ReviewTests.Run(Assert);
await R122ReviewTests.Run(root,Assert,Reject);
await R123ReviewTests.Run(root,Assert);
await R124ReviewTests.Run(root,Assert);
R126ReviewTests.Run(Assert);
await R130ReviewTests.Run(root,Assert);
await R131ReviewTests.Run(root,Assert);
await R132ReviewTests.Run(root,Assert);
await R133ReviewTests.Run(root,Assert);
await R134ReviewTests.Run(root,Assert);
await R135ReviewTests.Run(root,Assert);
await R136ReviewTests.Run(root,Assert);
await R137ReviewTests.Run(root,Assert);
await R138ReviewTests.Run(root,Assert);
await R139ReviewTests.Run(root,Assert);
await R140ReviewTests.Run(root,Assert);
await R141ReviewTests.Run(root,Assert);
await R142ReviewTests.Run(root,Assert);
await R143ReviewTests.Run(root,Assert);
await R144ReviewTests.Run(root,Assert);
await R145ReviewTests.Run(root,Assert);
await R146ReviewTests.Run(root,Assert);
await R147ReviewTests.Run(root,Assert);
await R148ReviewTests.Run(root,Assert);
await R149ReviewTests.Run(root,Assert);
await R150ReviewTests.Run(Assert);
await R153ReviewTests.Run(root,Assert);
await R154ReviewTests.Run(root,Assert);
await R155ReviewTests.Run(root,Assert);
await R156ReviewTests.Run(root,Assert);
await R157ReviewTests.Run(Assert);
await R158ReviewTests.Run(Assert);
await R159ReviewTests.Run(Assert);
await R160ReviewTests.Run(Assert);
await R161ReviewTests.Run(Assert);
await R162ReviewTests.Run(root,Assert,Reject);
await R163ReviewTests.Run(Assert);
await R164ReviewTests.Run(Assert);
await R165ReviewTests.Run(Assert);
await R167ReviewTests.Run(root,Assert);
R168ReviewTests.Run(Assert);
await R169ReviewTests.Run(Assert);
await R170ReviewTests.Run(Assert);
await R171ReviewTests.Run(Assert);
await R172ReviewTests.Run(Assert);
await R173ReviewTests.Run(Assert);
await R174ReviewTests.Run(root, Assert);
await K1K2WeightedSnapshotTests.Run(root, Assert);
await V1V2ReportTests.Run(root, Assert);
await O6UiErrorGuardTests.Run(Assert);
await F3F4FiscalGateTests.Run(Assert);
await F6TseFinishJournalTests.Run(root, Assert);
await G3UpdateHelperTests.Run(Assert);
await O1PrintJournalTests.Run(root, Assert);
await O12CsvImportTests.Run(root, Assert);
await K3FactoryAdminSecurityTests.Run(root, Assert);
await F1SwissbitTimeAdminSafetyTests.Run(root, Assert);
await R175ReviewTests.Run(root, Assert);
await R176ReviewTests.Run(root, Assert);
await R177ReviewTests.Run(root, Assert);
await R179ReviewTests.Run(root, Assert);
await R180ReviewTests.Run(Assert);
await R181ReviewTests.Run(root, Assert);
await TseStartupWarningTests.Run(Assert);
await TseOutageHistoryTests.Run(Assert);
await CloudTseFoundationTests.Run(Assert);
await MultiLanguageTests.Run(Assert);
await TseLifecycleReleaseGateTests.Run(Assert);
await EditionSplitFoundationTests.Run(Assert);
await RestaurantFoundationTests.Run(Assert);
await CustomerDisplayAdsTests.Run(Assert);
await AdTvTests.Run(Assert);
await KassenSichV2026ReviewTests.Run(Assert);
await TrialLicenseReviewTests.Run(Assert);
await BarTestBonPreparationTests.Run(Assert);

// R155: 13 reviewed checks cover DATEV Kassenbuch Standard-ASCII
// structure/encoding, cash-only semantics, Z reconciliation, cash movements,
// immutable file/hash, migration/audit and tamper protection.
// R156: 12 reviewed checks lock the payment hub, fresh-sale AUSSER HAUS default,
// IM HAUS VAT snapshot, TSE/receipt tax propagation, DSFinV-K INHAUS,
// GEMISCHT split and multi-size UI snapshot coverage.
// R157: 5 reviewed checks lock the new TOR POS header branding and the
// grouped/aligned payment dialog without changing checkout behavior.
// R158: 4 reviewed checks lock stretched full-height menu cards across the
// Waren, Einstellungen, Kasse and Berichte workspaces.
// R159: 5 reviewed checks lock the site-like sidebar/detail navigation and
// the single-file Windows download package.
// R160: 4 reviewed checks lock proportional full-height sidebar cards and
// larger till-readable labels.
// R161: 4 reviewed checks lock the refreshed splash/login/application branding
// and visual snapshot coverage.
// R162: 7 reviewed checks lock password-or-PIN staff activation, removal of
// untouched factory credentials and the customer installer/desktop shortcut.
// R163: 5 reviewed checks lock ShellExecute launch and verified asInvoker
// manifest packaging for the customer installer.
// R164: 3 reviewed checks lock fresh admin TextBoxes on reload and the real
// employee-management double-load UI regression path.
// R165: 5 reviewed checks lock keyboard-wedge scanner capture, ENTER/TAB
// suffix support, catalog refresh and explicit scan feedback.
// R167: 11 reviewed checks lock the multi-brand terminal catalogue, verified
// ZVT routes, fail-closed proprietary profiles and first-run/setup assistants.
// R168: 7 reviewed checks lock one visible KASSIEREN entry point, one-page
// BAR/KARTE/GEMISCHT details and removal of the duplicate cashier buttons.
// R169: 12 reviewed checks lock conservative Epson/Star printer recognition,
// Windows driver/port discovery, first-run/settings integration and the
// explicit no-sale/no-receipt cash-drawer connection test.
// R170: 12 reviewed checks lock gram/kg normalization, manual disconnected
// weighing, kg receipt/stock semantics, scale settings and fail-closed
// connected-scale preparation.
// R171: 15 reviewed checks lock DSFinV-K Von/Bis export, complete Z-period
// semantics, USB/e-mail delivery, ZIP MIME handling and current release
// metadata / anti-drift CI enforcement.
// R172: 14 reviewed checks lock single-writer FIFO database execution,
// Swissbit hard-watchdog isolation/cancellation, fixed-point quantity storage
// and the validated source-code ZIP artifact.
// R173: 5 reviewed checks lock Unicode Windows spooler metadata and prevent
// Epson office/fax queues from being accepted as POS receipt printers.
// R174: 9 reviewed checks lock PowerShell 5.1 simulator verification,
// operating/Z-day promotion dates, cent-exact weighted promotions, checkout
// unit preservation and identical partial-return allocation.
// K-1/K-2: 7 behavioral checks lock CartLine unit snapshots, additive R193
// persistence, parked/sale reloads and stored line totals through KassenbelegText.
// R175: 8 reviewed checks lock read-only fiskaltrust/Swissbit discovery,
// Queue Echo + SCU/device probes, secret isolation and the still-closed
// physical-TSE production release gate.
// R176: 19 reviewed checks lock cumulative-cent partial returns, authoritative
// terminal refund quotes, no-Z business-day fallback, typed local fiskaltrust
// Sign requests, explicit DE cases, secret isolation, vendor-correct Epson/Star
// drawer protocols, cashier HID scanner fallback and fail-closed semantics.
// R177: 6 reviewed checks lock a real cashier scanner focus target, suffix-loss
// recovery and payment-side drawer opening independent of paper receipt output.
// R179: 10 reviewed checks lock cumulative partial-return tender allocation,
// visible HID scanner capture/timing, the unified drawer switch and Cloud
// reversal/weighted-promotion payload contracts.
// R180: 4 reviewed checks lock cashier-only unknown-EAN behavior,
// KeyDown/TextInput de-duplication and the non-blinking scanner capture.
// R181: 10 reviewed checks lock the 140 ms suffix-less path, bounded FIFO,
// edition-isolated business profiles and permanent licence-bound edition UI.
// TSE lifecycle/release gate: 13 checks lock 90/30-day certificate warnings,
// expired-certificate fail-safe behavior and generation-specific E2E evidence.
// Split-product foundation checks keep Einzelhandel/Gastro process, storage,
// compile-time identity, per-product demo identity, side-by-side installers and
// backup-first legacy migration - including a crash-interrupted WAL source -
// separated while the shared R181 source remains intact for rollback.
// Restaurant foundation: 101 checks lock signed Standard/Plus entitlement,
// Self Order III contributes 6 reviewed inbox/idempotency/immutability/race checks on top of Self Order II.
// Restaurant-only schema isolation, pairing/token hashing and revocation,
// guest/note concurrency, kitchen NOTE routing and fiscal reconciliation.
const int ExpectedSafetyChecks = 1395;

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
