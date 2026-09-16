using TorPos.Core;
using TorPos.Infrastructure;
using Microsoft.Data.Sqlite;
static class R53ReviewTests {
 public static async Task Run(string root,Action<bool,string> check,Func<Func<Task>,string,Task> reject){
  var db=await SafetyDatabase.CreateCurrentAsync(Path.Combine(root,"r53.db"));var repo=new ProductRepository(db);var settings=new SettingsRepository(db);
  await settings.SaveManyAsync(new Dictionary<string,string>{{"business.mode","IMBISS"}});
  var starter=new ImbissStarterCatalogService(db);await starter.EnsureAsync("IMBISS");
  var cats=await repo.GetCategoriesAsync();var category=cats.First(x=>x.Name=="Döner");var component=(await repo.GetActiveProductsAsync()).First();
  var product=new Product{CategoryId=category.Id,Name="Atomic menu",Barcode="53001",BasePriceCents=1000};
  var id=await repo.SaveWithStockAsync(product,10,0,"test",variants:new[]{new ProductVariant(0,0,"Small",700)},comboItems:new[]{new ProductComboItem(0,component.Id,component.Name,1,0)});
  var saved=(await repo.GetByIdAsync(id))!;check(saved.Variants.Count==1&&saved.ComboItems.Count==1&&saved.StockQuantity==10,"Article, variants, combo and stock save together");
  saved.Name="Must rollback";
  await reject(()=>repo.SaveWithStockAsync(saved,12,10,"test",variants:new[]{new ProductVariant(0,id,"Changed",800)},comboItems:new[]{new ProductComboItem(id,id,"Self",1,0)}),"Invalid combo rejects complete article edit");
  var after=(await repo.GetByIdAsync(id))!;check(after.Name=="Atomic menu"&&after.StockQuantity==10&&after.Variants.Single().Name=="Small"&&after.ComboItems.Single().ComponentProductId==component.Id,"Failed combo leaves all original article data intact");
  await reject(()=>repo.SaveWithStockAsync(new Product{CategoryId=category.Id,Name="No orphan",Barcode="53002"},2,0,"test",variants:new[]{new ProductVariant(0,0,"",20)}),"Invalid variant rejects new product and stock");
  check(!(await repo.GetActiveProductsAsync()).Any(x=>x.Barcode=="53002"),"No partial product after failed variant save");
  var pizza=(await repo.GetActiveProductsAsync()).First(x=>x.Name.StartsWith("Pizza "));
  await repo.ReplaceVariantsAsync(pizza.Id,new[]{new ProductVariant(0,pizza.Id,"Klein 26 cm",1234),new ProductVariant(0,pizza.Id,"Mittel 30 cm",2345),new ProductVariant(0,pizza.Id,"Groß 36 cm",3456)});
  var burger=cats.First(x=>x.Name=="Burger");
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();q.CommandText="UPDATE app_settings SET value='R51-upgrade-test' WHERE key='imbiss.catalog.template.version'; UPDATE category_master_data SET vat_rate=0 WHERE category_id=$cat; UPDATE products SET is_active=0 WHERE id=$product; UPDATE categories SET is_active=0 WHERE id=$cat; UPDATE product_groups SET is_active=0 WHERE name='Speisen';";q.Parameters.AddWithValue("$cat",burger.Id);q.Parameters.AddWithValue("$product",component.Id);q.ExecuteNonQuery();}
  await starter.EnsureAsync("IMBISS");
  using(var c=db.OpenConnection()){using var q=c.CreateCommand();
   q.CommandText="SELECT vat_rate FROM category_master_data WHERE category_id="+burger.Id;check(Convert.ToDecimal(q.ExecuteScalar())==0,"Template upgrade preserves configured VAT");
   q.CommandText="SELECT is_active FROM products WHERE id="+component.Id;check(Convert.ToInt64(q.ExecuteScalar())==0,"Template upgrade does not reactivate deleted product");
   q.CommandText="SELECT is_active FROM categories WHERE id="+burger.Id;check(Convert.ToInt64(q.ExecuteScalar())==0,"Template upgrade does not reactivate deleted category");
   q.CommandText="SELECT is_active FROM product_groups WHERE name='Speisen'";check(Convert.ToInt64(q.ExecuteScalar())==0,"Template upgrade does not reactivate deleted group");
   q.CommandText="SELECT SUM(price_cents) FROM product_variants WHERE product_id="+pizza.Id;check(Convert.ToInt64(q.ExecuteScalar())==7035,"Template upgrade preserves three custom pizza sizes and prices");
  }
 }
}
