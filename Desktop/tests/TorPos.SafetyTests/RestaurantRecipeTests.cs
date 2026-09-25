using TorPos.Core;
using TorPos.Infrastructure;

internal static class RestaurantRecipeTests
{
    public static async Task Run(Action<bool,string> assert)
    {
        var oldEdition = Environment.GetEnvironmentVariable("TOR_POS_PRODUCT_EDITION");
        Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION", "RESTAURANT");
        try
        {
            var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(Path.GetTempPath(), "recipe-"+Guid.NewGuid().ToString("N")+".db"));
            var repository = new ProductRepository(db);
            var group=await repository.SaveGroupAsync(new(0,"Test"));
            var category=await repository.SaveCategoryAsync(new(0,group,"Speisen",7m));
            var id=await repository.SaveAsync(new Product { CategoryId=category,Name="Burger",BasePriceCents=1200 });
            var product=(await repository.GetByIdAsync(id))!;
            var recipes = new RestaurantRecipeRepository(db);
            var meat = await recipes.SaveIngredientAsync(new(0,"Fleisch","g",true));
            await recipes.SaveRecipeAsync(product.Id, new[] { new RestaurantRecipeLine(meat,180m) });
            var persisted = await new RestaurantRecipeRepository(db).LoadRecipeAsync(product.Id);
            assert(persisted.Count==1 && persisted[0].IngredientId==meat && persisted[0].Quantity==180m,
                "Restaurant recipe survives repository recreation with exact ingredient quantity");
            var rejected=false;
            try { await recipes.SaveRecipeAsync(product.Id,new[]{new RestaurantRecipeLine(meat,-1m)}); }
            catch(ArgumentException){ rejected=true; }
            assert(rejected && (await recipes.LoadRecipeAsync(product.Id))[0].Quantity==180m,
                "Invalid recipe replacement preserves saved recipe atomically");
            rejected=false;
            try { await recipes.SaveRecipeAsync(product.Id,new[]{new RestaurantRecipeLine(999999,1m)}); }
            catch(InvalidOperationException){ rejected=true; }
            assert(rejected && (await recipes.LoadRecipeAsync(product.Id))[0].Quantity==180m,
                "Missing ingredient cannot erase a saved recipe");
            rejected=false;
            try { await recipes.SaveIngredientAsync(new(meat,"Fleisch","ml",true)); }
            catch(InvalidOperationException){ rejected=true; }
            assert(rejected, "Referenced ingredient unit cannot silently change recipe quantities");
        }
        finally { Environment.SetEnvironmentVariable("TOR_POS_PRODUCT_EDITION",oldEdition); }
    }
}
