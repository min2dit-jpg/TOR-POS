using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;
static class R48ReviewTests {
 public static async Task Run(string root,Action<bool,string> assert,Func<Func<Task>,string,Task> reject){
 var path=Path.Combine(root,"r48.db");var db=await SafetyDatabase.CreateCurrentAsync(path);
 var repo=new ProductRepository(db);var category=(await repo.GetCategoriesAsync()).First();
 var p=new Product{CategoryId=category.Id,Name="Review",Sku="",Barcode="481234",BasePriceCents=250};
 var id=await repo.SaveWithStockAsync(p,10,0,"review");p=(await repo.GetByIdAsync(id))!;
 assert(long.TryParse(p.Sku,out var number)&&number>=100000,"Automatic article number assigned");
 var next=new Product{CategoryId=category.Id,Name="Next",Barcode="481235"};var nextId=await repo.SaveAsync(next);next=(await repo.GetByIdAsync(nextId))!;
 assert(long.Parse(next.Sku)==number+1,"Automatic article numbers advance");
 p.Name="Updated price";await repo.SaveAsync(p);
 using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="SELECT stock_quantity FROM products WHERE id="+p.Id;assert(Convert.ToDecimal(q.ExecuteScalar())==10,"Price/name edit preserves stock");}
 await reject(()=>repo.SaveWithStockAsync(p,20,9,"review"),"Stale stock prevents entire product save");
 using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="CREATE TRIGGER review_audit_failure BEFORE INSERT ON audit_log WHEN NEW.actor='FAIL' BEGIN SELECT RAISE(ABORT,'test'); END;";q.ExecuteNonQuery();}
 await reject(()=>repo.SaveWithStockAsync(new Product{CategoryId=category.Id,Name="MUST_ROLLBACK",Barcode="rollback"},3,0,"FAIL"),"Audit failure rolls back article and stock");
 assert(!(await repo.GetActiveProductsAsync()).Any(x=>x.Name=="MUST_ROLLBACK"),"Failed stock save leaves no partial new article");
 var management=new BusinessManagementService(db,new SettingsRepository(db),new AuditLogRepository(db));
 await reject(()=>management.TrySetInventoryAsync(p.Id,10,12,"FAIL"),"Inventory audit failure rolls back count");
 using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="SELECT stock_quantity FROM products WHERE id="+p.Id;assert(Convert.ToDecimal(q.ExecuteScalar())==10,"Inventory retains original count after audit failure");}
 var at=DateTimeOffset.Parse("2026-09-08T21:00:00Z");
 assert(await PickupSequence.NextSimulationAsync(db,true,at)==1,"Training starts at 001");
 assert(await PickupSequence.NextSimulationAsync(new SqliteDatabase(path),true,at)==2,"Training counter survives reopening");
 assert(await PickupSequence.NextSimulationAsync(db,false,at)==1,"Test and training counters separated");
 assert(await PickupSequence.NextSimulationAsync(db,true,at.AddHours(2))==1,"Counter resets on Berlin midnight");
 using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="SELECT COUNT(*) FROM app_sequence WHERE key LIKE 'pickup.20%'";assert(Convert.ToInt32(q.ExecuteScalar())==0,"Simulation never consumes production counter");}
 await SafetyDatabase.EnsureCurrentAsync(db);assert((await repo.GetByIdAsync(p.Id))!.Sku==p.Sku,"Reopening migration preserves assigned article number");
 }
}
