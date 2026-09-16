using TorPos.Core;
using TorPos.Infrastructure;
public static class R56ReviewTests
{
 public static async Task Run(string root,Action<bool,string> check)
 {
  var box=new Avalonia.Controls.TextBox { Text="Döner", SelectionStart=0,SelectionEnd=5 };
  TorPos.App.TouchTextEditor.Insert(box,"Menü");check(box.Text=="Menü"&&box.CaretIndex==4,"Touch keyboard replaces selection and preserves German characters");
  box.SelectionStart=box.SelectionEnd=4;TorPos.App.TouchTextEditor.Backspace(box);check(box.Text=="Men","Touch backspace removes preceding character");
  box.Text="A😀";box.CaretIndex=3;box.SelectionStart=box.SelectionEnd=3;TorPos.App.TouchTextEditor.Backspace(box);check(box.Text=="A","Touch backspace preserves Unicode surrogate integrity");
  box.Text="1234";box.PasswordChar='●';box.MaxLength=4;box.SelectionStart=box.SelectionEnd=4;TorPos.App.TouchTextEditor.Insert(box,"5");check(box.Text=="1234"&&box.PasswordChar=='●',"Touch input respects password length and masking");
  box.IsReadOnly=true;TorPos.App.TouchTextEditor.Backspace(box);check(box.Text=="1234","Touch input cannot alter read-only field");
  var db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(root,"r56.db"));
  var settings=new SettingsRepository(db);var repo=new ProductRepository(db);var parks=new ParkedReceiptRepository(db);
  var category=(await repo.GetCategoriesAsync()).First();var id=await repo.SaveAsync(new Product{CategoryId=category.Id,Name="Wake-up",BasePriceCents=100});
  await settings.SaveManyAsync(new Dictionary<string,string>{{"device.kitchen_printer.enabled","true"},{"device.kitchen_printer.name","Test"},{"device.receipt_printer.enabled","false"}});
  var outbox=new OrderPrintOutbox(db);var journal=new PrintJobJournal(Path.Combine(root,"r56-prints"));

  var received=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int count=0;
  await using var worker=new OrderPrintDispatcher(outbox,journal,async job=>{Interlocked.Increment(ref count);await journal.SaveAsync(job with{State="SUBMITTED"});received.TrySetResult();});
  int notifications=0;parks.PrintCommitted=()=>{Interlocked.Increment(ref notifications);worker.Notify();};
  worker.Start();await Task.Delay(150);
  var line=new CartLine{ProductId=id,ProductName="Wake-up",Quantity=1,UnitPriceCents=100,VatRate=7};
  var started=System.Diagnostics.Stopwatch.StartNew();
  var order=await parks.ParkAsync(new[]{line},0,"test",true,training:true,orderPrint:true);
  await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
  check(started.Elapsed<TimeSpan.FromSeconds(2),"Committed order wakes sleeping printer without five-second poll");
  for(int i=0;i<100;i++)worker.Notify();
  await worker.DispatchOnceAsync();
  check(count==1&&notifications==1,"Burst notifications never duplicate durable print job");
  await settings.SaveManyAsync(new Dictionary<string,string>{{"device.kitchen_printer.name",""}});
  try{await parks.ParkAsync(new[]{line},0,"test",true,training:true,orderPrint:true);}catch(InvalidOperationException){}
  check(notifications==1&&await parks.GetOpenCountAsync(training:true)==1,"Rolled-back order does not signal print worker");
 }
}
