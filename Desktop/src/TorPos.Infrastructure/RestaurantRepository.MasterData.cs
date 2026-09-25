using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed partial class RestaurantRepository
{
    public RestaurantRecipeRepository Recipes => new(_db);

    public async Task<IReadOnlyList<RestaurantArea>> ListAreasAsync()
    {
        using var c = _db.OpenReadConnection();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id,name,sort_order,is_active FROM restaurant_areas ORDER BY sort_order,id;";
        using var r = await q.ExecuteReaderAsync();
        var result = new List<RestaurantArea>();
        while (await r.ReadAsync()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3)!=0));
        return result;
    }
}
