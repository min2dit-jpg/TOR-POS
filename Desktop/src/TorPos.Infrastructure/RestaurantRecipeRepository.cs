using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed class RestaurantRecipeRepository(SqliteDatabase db)
{
    public async Task<IReadOnlyList<RestaurantIngredient>> ListIngredientsAsync()
    {
        using var c=db.OpenReadConnection(); using var q=c.CreateCommand();
        q.CommandText="SELECT id,name,unit,is_active FROM restaurant_ingredients ORDER BY name;";
        using var r=await q.ExecuteReaderAsync(); var result=new List<RestaurantIngredient>();
        while(await r.ReadAsync()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt32(3)!=0));
        return result;
    }

    public Task<long> SaveIngredientAsync(RestaurantIngredient ingredient)
    {
        var name=ingredient.Name.Trim();
        if(name.Length is <1 or >120 || !new[]{"g","ml","Stück"}.Contains(ingredient.Unit))
            throw new ArgumentException("Zutat benötigt einen Namen und die Basiseinheit g, ml oder Stück.");
        return IoQueue.RunAsync(async () =>
        {
            using var c=db.OpenConnection(); using var tx=c.BeginTransaction();
            if(ingredient.Id>0)
            {
                using var guard=c.CreateCommand(); guard.Transaction=tx;
                guard.CommandText="SELECT COUNT(*) FROM restaurant_recipes r JOIN restaurant_ingredients i ON i.id=r.ingredient_id WHERE i.id=$id AND (i.unit<>$unit OR $active=0);";
                guard.Parameters.AddWithValue("$id",ingredient.Id); guard.Parameters.AddWithValue("$unit",ingredient.Unit);
                guard.Parameters.AddWithValue("$active",ingredient.IsActive?1:0);
                if(Convert.ToInt64(await guard.ExecuteScalarAsync())>0)
                    throw new InvalidOperationException("Verwendete Zutat: Einheit/Aktivierung erst nach Anpassung der Rezepturen ändern.");
            }
            using var q=c.CreateCommand(); q.Transaction=tx;
            q.CommandText=ingredient.Id==0
                ? "INSERT INTO restaurant_ingredients(name,unit,is_active) VALUES($name,$unit,$active) RETURNING id;"
                : "UPDATE restaurant_ingredients SET name=$name,unit=$unit,is_active=$active WHERE id=$id RETURNING id;";
            q.Parameters.AddWithValue("$id",ingredient.Id); q.Parameters.AddWithValue("$name",name);
            q.Parameters.AddWithValue("$unit",ingredient.Unit); q.Parameters.AddWithValue("$active",ingredient.IsActive?1:0);
            var id=Convert.ToInt64(await q.ExecuteScalarAsync());
            if(id<=0) throw new InvalidOperationException("Zutat wurde nicht gefunden.");
            tx.Commit(); return id;
        });
    }

    public async Task<IReadOnlyList<RestaurantRecipeLine>> LoadRecipeAsync(long productId)
    {
        using var c=db.OpenReadConnection(); using var q=c.CreateCommand();
        q.CommandText="SELECT ingredient_id,quantity_milli FROM restaurant_recipes WHERE product_id=$id ORDER BY ingredient_id;";
        q.Parameters.AddWithValue("$id",productId);
        using var r=await q.ExecuteReaderAsync(); var result=new List<RestaurantRecipeLine>();
        while(await r.ReadAsync()) result.Add(new(r.GetInt64(0),r.GetInt64(1)/1000m));
        return result;
    }

    public Task SaveRecipeAsync(long productId,IReadOnlyList<RestaurantRecipeLine> lines)
    {
        if(productId<=0 || lines.Any(x=>x.IngredientId<=0 || x.Quantity<=0 || x.Quantity>1000000m || decimal.Round(x.Quantity,3)!=x.Quantity)
            || lines.Select(x=>x.IngredientId).Distinct().Count()!=lines.Count)
            throw new ArgumentException("Rezeptur: eindeutige Zutaten mit positiver Menge (maximal 3 Nachkommastellen) erforderlich.");
        var snapshot=lines.ToArray();
        return IoQueue.RunAsync(async () =>
        {
            using var c=db.OpenConnection(); using var tx=c.BeginTransaction();
            using(var q=c.CreateCommand())
            {
                q.Transaction=tx; q.CommandText="SELECT COUNT(*) FROM products WHERE id=$id AND is_active=1;";
                q.Parameters.AddWithValue("$id",productId);
                if(Convert.ToInt64(await q.ExecuteScalarAsync())!=1) throw new InvalidOperationException("Aktiver Artikel fehlt.");
            }
            foreach(var line in snapshot)
            {
                using var q=c.CreateCommand(); q.Transaction=tx;
                q.CommandText="SELECT COUNT(*) FROM restaurant_ingredients WHERE id=$id AND is_active=1;";
                q.Parameters.AddWithValue("$id",line.IngredientId);
                if(Convert.ToInt64(await q.ExecuteScalarAsync())!=1) throw new InvalidOperationException("Aktive Zutat fehlt.");
            }
            using(var q=c.CreateCommand())
            {
                q.Transaction=tx; q.CommandText="DELETE FROM restaurant_recipes WHERE product_id=$id;";
                q.Parameters.AddWithValue("$id",productId); await q.ExecuteNonQueryAsync();
            }
            foreach(var line in snapshot)
            {
                using var q=c.CreateCommand(); q.Transaction=tx;
                q.CommandText="INSERT INTO restaurant_recipes VALUES($product,$ingredient,$quantity);";
                q.Parameters.AddWithValue("$product",productId); q.Parameters.AddWithValue("$ingredient",line.IngredientId);
                q.Parameters.AddWithValue("$quantity",(long)(line.Quantity*1000m)); await q.ExecuteNonQueryAsync();
            }
            tx.Commit();
        });
    }

    public async Task<IReadOnlyList<RestaurantOrderOption>> ListOptionsAsync()
    {
        using var c=db.OpenReadConnection(); using var q=c.CreateCommand();
        q.CommandText="SELECT id,name,is_active FROM restaurant_order_options ORDER BY name;";
        using var r=await q.ExecuteReaderAsync(); var result=new List<RestaurantOrderOption>();
        while(await r.ReadAsync()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt32(2)!=0));
        return result;
    }

    public Task SaveOptionAsync(RestaurantOrderOption option)
    {
        var name=option.Name.Trim();
        if(name.Length is <1 or >100 || name.Any(char.IsControl)) throw new ArgumentException("Bestelloption benötigt einen Namen (max. 100 Zeichen).");
        return IoQueue.RunAsync(async () =>
        {
            using var c=db.OpenConnection(); using var q=c.CreateCommand();
            q.CommandText=option.Id==0
                ? "INSERT INTO restaurant_order_options(name,is_active) VALUES($name,$active);"
                : "UPDATE restaurant_order_options SET name=$name,is_active=$active WHERE id=$id;";
            q.Parameters.AddWithValue("$name",name); q.Parameters.AddWithValue("$active",option.IsActive?1:0);
            q.Parameters.AddWithValue("$id",option.Id); await q.ExecuteNonQueryAsync();
        });
    }
}
