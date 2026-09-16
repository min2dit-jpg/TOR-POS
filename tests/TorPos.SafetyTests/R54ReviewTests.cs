using TorPos.Core;
using TorPos.Infrastructure;
using TorPos.App;
static class R54ReviewTests
{
 public static async Task Run(string root,Action<bool,string> check,Func<Func<Task>,string,Task> reject)
 {
  var db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(root,"r54.db"));
  var repo=new ProductRepository(db);var parks=new ParkedReceiptRepository(db);var flow=new OrderWorkflowService(db);var settings=new SettingsRepository(db);
  var cat=(await repo.GetCategoriesAsync()).First();
  var part=await repo.SaveWithStockAsync(new Product{CategoryId=cat.Id,Name="Part",BasePriceCents=500},10,0,"test");
  var menu=await repo.SaveWithStockAsync(new Product{CategoryId=cat.Id,Name="Menu",BasePriceCents=700},0,0,"test",comboItems:new[]{new ProductComboItem(0,part,"Part",1,0)});
  var other=await repo.SaveAsync(new Product{CategoryId=cat.Id,Name="Other",BasePriceCents=900});
  await reject(()=>repo.ReplaceComboItemsAsync(other,new[]{new ProductComboItem(other,menu,"Nested",1,0)}),"Nested menu is rejected");
  await reject(()=>repo.ReplaceComboItemsAsync(part,new[]{new ProductComboItem(part,other,"Reverse nesting",1,0)}),"Existing component cannot become a nested menu");
  check((await repo.GetByIdAsync(menu))!.ComboItems.Single().ComponentProductId==part,"Rejected nesting preserves recipe");
  var menuOrder=await parks.ParkAsync(new[]{new CartLine{ProductId=menu,ProductName="Menu",Quantity=1,UnitPriceCents=700,VatRate=7}},0,"operator");
  await reject(()=>repo.ReplaceComboItemsAsync(menu,Array.Empty<ProductComboItem>()),"Open order protects recipe from later stock mismatch");
  await repo.ReplaceComboItemsAsync(menu,new[]{new ProductComboItem(menu,part,"Part",1,0)});
  check((await repo.GetByIdAsync(menu))!.ComboItems.Count==1,"Unchanged recipe can still be saved with an open order");
  await parks.CancelAsync(menuOrder.Id);
  var line=new CartLine{ProductId=part,ProductName="Part",Quantity=1,UnitPriceCents=500,VatRate=7};
  var order=await parks.ParkAsync(new[]{line},0,"operator",false,training:true);
  var normal=await parks.ParkAsync(new[]{line},0,"operator",true);
  check(order.PickupNumber==0,"Order without pickup number supported");
  var row=(await flow.ListAsync(true)).Single();check(row.Id==order.Id&&row.Payment=="Nicht bezahlt","Workflow isolates training and starts unpaid");
  await reject(()=>flow.UpdateAsync(order.Id,row.Version,"READY","","operator",true),"State cannot skip preparation");
  await reject(()=>flow.UpdateAsync(order.Id,row.Version,"PREPARING","","operator",false),"Cross-mode workflow update rejected");
  await flow.UpdateAsync(order.Id,row.Version,"PREPARING","Ohne Zwiebeln","operator",true);
  await reject(()=>flow.UpdateAsync(order.Id,row.Version,"PREPARING","stale","operator",true),"Stale editor cannot overwrite order");
  row=(await flow.ListAsync(true)).Single();await flow.UpdateAsync(row.Id,row.Version,"READY",row.Note,"operator",true);
  row=(await flow.ListAsync(true)).Single();await flow.UpdateAsync(row.Id,row.Version,"DELIVERED",row.Note,"operator",true);
  row=(await flow.ListAsync(true)).Single();check(row.State=="DELIVERED"&&row.Open,"Delivered but unpaid order remains visible");
  check((await parks.GetOpenByIdAsync(order.Id,training:true))!.OrderNote=="Ohne Zwiebeln","Kitchen note survives repository reload");
  check((await repo.GetByIdAsync(part))!.StockQuantity==10,"Preparation does not change stock");
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM sales";check(Convert.ToInt64(q.ExecuteScalar())==0,"Preparation does not create a sale");}
  await flow.CompleteSimulationAsync(order.Id,PaymentMethod.Cash,true);
  check((await flow.ListAsync(true)).Count==0,"Delivered simulated payment leaves active list");
  row=(await flow.ListAsync(true,true)).Single();check(row.Payment=="TEST · Bar simuliert"&&!row.Open,"Simulation is explicitly labelled and retained");
  await reject(()=>flow.CompleteSimulationAsync(order.Id,PaymentMethod.Cash,true),"Simulation cannot complete twice");
  check(await parks.GetOpenCountAsync()==1,"Training completion leaves real open orders untouched");
  var realRow=(await flow.ListAsync(false)).Single();await parks.UpdateAsync(normal.Id,new[]{line},0);
  await reject(()=>flow.UpdateAsync(normal.Id,realRow.Version,"PREPARING","stale","operator",false),"Cart edits invalidate workflow snapshot");
  await settings.SaveManyAsync(new Dictionary<string,string>{{"ui.language","TR"},{"business.mode","IMBISS"}});await SafetyDatabase.EnsureCurrentAsync(db);
  check(!(await settings.LoadAllAsync()).ContainsKey("ui.language"),"Upgrade removes legacy language preference");UiLanguage.Set("TR");check(UiLanguage.Current=="DE"&&UiLanguage.T("KASSE")=="KASSE","All UI callers remain German");
  var starter=new ImbissStarterCatalogService(db);await starter.EnsureAsync("IMBISS");
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="UPDATE products SET image_path='C:\\ProductImages\\tor-imbiss-doener.png' WHERE id=$id; UPDATE products SET image_path='C:\\Photos\\my-product.jpg' WHERE id=$other;";q.Parameters.AddWithValue("$id",part);q.Parameters.AddWithValue("$other",other);q.ExecuteNonQuery();}
  await starter.EnsureAsync("IMBISS");check(string.IsNullOrEmpty((await repo.GetByIdAsync(part))!.ImagePath),"Existing generated photo removed even after template marker");
  check((await repo.GetByIdAsync(other))!.ImagePath.EndsWith("my-product.jpg"),"Customer photo retained");
  await SafetyDatabase.EnsureCurrentAsync(db);check((await flow.ListAsync(true,true)).Single().State=="DELIVERED","Workflow survives restart/migration");
  var backups=new DatabaseBackupService(db);var backupFolder=Path.Combine(root,"r54-backups");
  var paths=await Task.WhenAll(backups.CreateBackupAsync(backupFolder),backups.CreateBackupAsync(backupFolder));

  var backupHandlesReleased=true;
  foreach(var path in paths)
  {
   var opened=false;

   for(var attempt=0;attempt<8&&!opened;attempt++)
   {
    try
    {
     using var exclusive=new FileStream(
         path,
         FileMode.Open,
         FileAccess.ReadWrite,
         FileShare.None);
     opened=true;
    }
    catch(IOException)
    {
     await Task.Delay(50*(attempt+1));
    }
   }

   if(!opened)
       backupHandlesReleased=false;
  }

  check(
      paths.Distinct().Count()==2&&
      paths.All(File.Exists)&&
      backupHandlesReleased,
      "Concurrent backups create distinct files and release snapshot handles");

 }
}
