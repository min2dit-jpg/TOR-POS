using TorPos.Core;
using TorPos.Infrastructure;
using System.IO.Compression;
static class R55ReviewTests
{
 public static async Task Run(string root,Action<bool,string> check,Func<Func<Task>,string,Task> reject){
  var data=Path.Combine(root,"r55-data");Directory.CreateDirectory(data);var db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(data,"torpos.db"));
  var repo=new ProductRepository(db);var settings=new SettingsRepository(db);var parks=new ParkedReceiptRepository(db);var outbox=new OrderPrintOutbox(db);
  var cat=(await repo.GetCategoriesAsync()).First();var product=await repo.SaveAsync(new Product{CategoryId=cat.Id,Name="R55 Original",BasePriceCents=500});
  await settings.SaveManyAsync(new Dictionary<string,string>{{"device.kitchen_printer.enabled","true"},{"device.kitchen_printer.auto_print","true"},{"device.kitchen_printer.name","Kitchen A"},{"device.receipt_printer.enabled","false"}});
  var line=new CartLine{ProductId=product,ProductName="Original",Quantity=1,UnitPriceCents=500,VatRate=7};
  var order=await parks.ParkAsync(new[]{line},0,"test",true,training:true,orderPrint:true);
  var pending=(await outbox.PendingAsync()).Single();check(pending.Kitchen!.PickupNumber==order.PickupNumber&&pending.Kitchen.Lines.Single().Name=="Original","Order and kitchen snapshot persist together");
  var renamed=(await repo.GetByIdAsync(product))!;renamed.Name="Renamed";await repo.SaveAsync(renamed);
  check((await outbox.PendingAsync()).Single().Kitchen!.Lines.Single().Name=="Original","Pending kitchen snapshot is immutable");
  await settings.SaveManyAsync(new Dictionary<string,string>{{"device.kitchen_printer.name",""}});
  await reject(()=>parks.ParkAsync(new[]{line},0,"test",true,training:true,orderPrint:true),"Print configuration failure rolls back order");
  check(await parks.GetOpenCountAsync(training:true)==1&&(await outbox.PendingAsync()).Count==1,"No partial order/outbox after failure");
  await settings.SaveManyAsync(new Dictionary<string,string>{{"device.kitchen_printer.name","Kitchen A"}});
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="CREATE TRIGGER r55_fail_audit BEFORE INSERT ON audit_log WHEN NEW.event_type='ORDER_ACCEPT' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END;";q.ExecuteNonQuery();}
  await reject(()=>parks.ParkAsync(new[]{line},0,"test",true,training:true,orderPrint:true),"Audit failure rolls back complete order transaction");
  check(await parks.GetOpenCountAsync(training:true)==1&&(await outbox.PendingAsync()).Count==1,"Audit failure leaves no duplicate order or print");
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="DROP TRIGGER r55_fail_audit;";q.ExecuteNonQuery();}
  var journal=new PrintJobJournal(Path.Combine(data,"PrintJobs"));var submitted=0;
  await journal.SaveAsync(pending with{State="SUBMITTED"});
  await using(var dispatcher=new OrderPrintDispatcher(outbox,journal,r=>{submitted++;return Task.CompletedTask;}))await dispatcher.DispatchOnceAsync();
  check(submitted==0&&(await outbox.PendingAsync()).Count==0,"Restart does not resubmit journaled print");
  await parks.UpdateAsync(order.Id,new[]{line},0,orderPrint:true);check((await outbox.PendingAsync()).Single().Kitchen!.Note.StartsWith("ÄNDERUNG"),"Order edit atomically stores change ticket");
  await using(var dispatcher=new OrderPrintDispatcher(outbox,journal,async r=>{submitted++;await journal.SaveAsync(r with{State="SPOOL_ACCEPTED"});})){await dispatcher.DispatchOnceAsync();await dispatcher.DispatchOnceAsync();}
  check(submitted==1&&(await outbox.PendingAsync()).Count==0,"Dispatcher transfers each ticket once");
  await parks.CancelAsync(order.Id,orderPrint:true);check((await outbox.PendingAsync()).Single().Kitchen!.Note.StartsWith("STORNO"),"Cancellation stores kitchen cancellation");
  Directory.CreateDirectory(Path.Combine(data,"ProductImages"));await File.WriteAllTextAsync(Path.Combine(data,"ProductImages","customer.jpg"),"customer image bytes");

  var stagingBefore=Directory
      .EnumerateDirectories(Path.GetTempPath(),"tor-backup-*")
      .ToHashSet(StringComparer.OrdinalIgnoreCase);

  var backup=await new FullBackupService(db,data).CreateAsync(Path.Combine(root,"r55-backups"));

  var stagingAfter=Directory
      .EnumerateDirectories(Path.GetTempPath(),"tor-backup-*")
      .Where(x=>!stagingBefore.Contains(x))
      .ToArray();

  var restore=Path.Combine(root,"r55-restored");var result=await FullBackupService.VerifyRestoreAsync(backup,restore);
  check(
      result.Products>0&&
      File.ReadAllText(Path.Combine(restore,"ProductImages","customer.jpg"))=="customer image bytes"&&
      stagingAfter.Length==0,
      "Backup restores database/assets and releases temporary staging files");
  await reject(()=>FullBackupService.VerifyRestoreAsync(backup,restore),"Restore never overwrites existing target");
  using(var zip=ZipFile.Open(backup,ZipArchiveMode.Update)){var entry=zip.GetEntry("ProductImages/customer.jpg")!;entry.Delete();using var writer=new StreamWriter(zip.CreateEntry("ProductImages/customer.jpg").Open());writer.Write("tampered");}
  await reject(()=>FullBackupService.VerifyRestoreAsync(backup,Path.Combine(root,"r55-corrupt")),"Modified backup fails hash verification");
 }
}
