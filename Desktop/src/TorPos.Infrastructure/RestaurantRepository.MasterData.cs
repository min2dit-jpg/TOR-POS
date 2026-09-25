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

    public async Task<IReadOnlyList<RestaurantTable>> ListAllTablesAsync()
    {
        using var c = _db.OpenReadConnection(); using var q = c.CreateCommand();
        q.CommandText = "SELECT id,area_id,code,display_name,seats,sort_order,is_active,version FROM restaurant_tables ORDER BY area_id,sort_order,id;";
        using var r=await q.ExecuteReaderAsync(); var rows=new List<RestaurantTable>();
        while(await r.ReadAsync()) rows.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6)!=0,r.GetInt64(7)));
        return rows;
    }

    public Task SaveAreaSettingsAsync(long id,string name,bool active)
    {
        name=name.Trim();
        if(name.Length is <1 or >80) throw new ArgumentException("Bereichsname: 1 bis 80 Zeichen erforderlich.");
        return IoQueue.RunAsync(async () =>
        {
            using var c=_db.OpenConnection(); using var tx=c.BeginTransaction();
            if(!active)
            {
                using var guard=c.CreateCommand(); guard.Transaction=tx;
                guard.CommandText="SELECT COUNT(*) FROM restaurant_sessions s JOIN restaurant_tables t ON t.id=s.table_id WHERE t.area_id=$id AND s.state IN ('OPEN','CHECK_REQUESTED');";
                guard.Parameters.AddWithValue("$id",id);
                if(Convert.ToInt64(await guard.ExecuteScalarAsync())>0) throw new InvalidOperationException("Bereich enthält offene Tische. Zuerst abschließen oder umbuchen.");
            }
            using var q=c.CreateCommand(); q.Transaction=tx;
            q.CommandText=id==0 ? "INSERT INTO restaurant_areas(name,sort_order,is_active) VALUES($name,0,$active);"
                : "UPDATE restaurant_areas SET name=$name,is_active=$active WHERE id=$id;";
            q.Parameters.AddWithValue("$id",id); q.Parameters.AddWithValue("$name",name); q.Parameters.AddWithValue("$active",active?1:0);
            await q.ExecuteNonQueryAsync(); tx.Commit();
        });
    }

    public Task SaveTableSettingsAsync(RestaurantTable table)
    {
        if(table.AreaId<=0 || string.IsNullOrWhiteSpace(table.Code) || string.IsNullOrWhiteSpace(table.DisplayName)
            || table.Code.Length>40 || table.DisplayName.Length>80 || table.Seats is <1 or >99)
            throw new ArgumentException("Bereich, Tischcode, Tischname und 1–99 Plätze erforderlich.");
        return IoQueue.RunAsync(async () =>
        {
            using var c=_db.OpenConnection(); using var tx=c.BeginTransaction();
            if(table.Id>0)
            {
                using var guard=c.CreateCommand(); guard.Transaction=tx;
                guard.CommandText="SELECT COUNT(*) FROM restaurant_sessions WHERE table_id=$id AND state IN ('OPEN','CHECK_REQUESTED');";
                guard.Parameters.AddWithValue("$id",table.Id);
                if(Convert.ToInt64(await guard.ExecuteScalarAsync())>0) throw new InvalidOperationException("Offenen Tisch zuerst abschließen. Für Bestellungen die Tischaktion UMBUCHEN verwenden.");
            }
            using var q=c.CreateCommand(); q.Transaction=tx;
            q.CommandText=table.Id==0
                ? "INSERT INTO restaurant_tables(area_id,code,display_name,seats,sort_order,is_active,version) VALUES($area,$code,$name,$seats,$sort,$active,1);"
                : "UPDATE restaurant_tables SET area_id=$area,code=$code,display_name=$name,seats=$seats,sort_order=$sort,is_active=$active,version=version+1 WHERE id=$id AND version=$version;";
            q.Parameters.AddWithValue("$area",table.AreaId); q.Parameters.AddWithValue("$code",table.Code.Trim());
            q.Parameters.AddWithValue("$name",table.DisplayName.Trim()); q.Parameters.AddWithValue("$seats",table.Seats);
            q.Parameters.AddWithValue("$sort",table.SortOrder); q.Parameters.AddWithValue("$active",table.IsActive?1:0);
            q.Parameters.AddWithValue("$id",table.Id); q.Parameters.AddWithValue("$version",table.Version);
            if(await q.ExecuteNonQueryAsync()!=1) throw new InvalidOperationException("Tisch wurde zwischenzeitlich geändert. Neu öffnen.");
            tx.Commit();
        });
    }
}
