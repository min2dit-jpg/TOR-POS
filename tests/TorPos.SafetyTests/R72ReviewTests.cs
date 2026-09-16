using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;
using TorPos.Infrastructure;

public static class R72ReviewTests
{
    public static async Task Run(
        string root,
        Action<bool,string> assert,
        Func<Func<Task>,string,Task> reject)
    {
        var dir = Path.Combine(root,"r72");
        Directory.CreateDirectory(dir);
        var db = new SqliteDatabase(Path.Combine(dir,"r72.db"));
        var migrator = new SchemaMigrationService(
            db,
            new DatabaseBackupService(db),
            Path.Combine(dir,"migration-backups"));

        var migration = await migrator.InitializeDatabaseAsync();
        assert(
            migration.ToVersion == SchemaMigrationService.TargetSchemaVersion &&
            SchemaMigrationService.TargetSchemaVersion >= 4,
            "R72 KDF metadata and Angebot 40/50 schema survive later ordered migrations");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name='promotion_campaigns';";
            var sql=Convert.ToString(q.ExecuteScalar())??"";
            assert(sql.Contains("40")&&sql.Contains("50"),
                "R72 promotion database constraint accepts 40 and 50 percent");
        }

        var promotions=new PromotionCampaignService(db);
        var today=DateOnly.FromDateTime(DateTime.Now);
        var p40=await promotions.CreateAsync(new PromotionCreateRequest(
            "R72 40 PROZENT",40,today,today.AddDays(1),PromotionScope.All,0,"Alle Artikel"),"admin");
        var active40=await promotions.GetBestForProductAsync(100,10,today);
        assert(active40 is not null && active40.PromotionId==p40 && active40.DiscountPercent==40,
            "R72 40 percent Angebot can be created and applied");

        var p50=await promotions.CreateAsync(new PromotionCreateRequest(
            "R72 50 PROZENT",50,today,today.AddDays(1),PromotionScope.Product,100,"Test"),"admin");
        var active50=await promotions.GetBestForProductAsync(100,10,today);
        assert(active50 is not null && active50.PromotionId==p50 && active50.DiscountPercent==50,
            "R72 50 percent Angebot can be created and wins over lower active promotion");

        await reject(()=>promotions.CreateAsync(new PromotionCreateRequest(
            "NICHT ERLAUBT",35,today,today.AddDays(1),PromotionScope.All,0,"Alle Artikel"),"admin"),
            "R72 arbitrary promotion percentages remain blocked");

        assert(
            AuthenticationService.CurrentKdfAlgorithm=="PBKDF2-SHA256" &&
            AuthenticationService.CurrentPbkdf2Iterations==600_000,
            "R72 current credential KDF is PBKDF2-SHA256 with 600000 iterations");

        var auth=new AuthenticationService(db,new AuditLogRepository(db));
        await InsertUserAsync(db,"legacy-user","Legacy-Password-2026!","8642",180_000,"PBKDF2-SHA256");

        var passwordLogin=await auth.LoginWithPasswordAsync("legacy-user","Legacy-Password-2026!");
        assert(passwordLogin.Success,
            "R72 legacy 180k password remains valid during seamless upgrade");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="SELECT password_iterations,pin_iterations FROM users WHERE username='legacy-user';";
            using var r=q.ExecuteReader(); r.Read();
            assert(r.GetInt32(0)==600_000 && r.GetInt32(1)==180_000,
                "R72 successful password login upgrades only password work factor");
        }

        var pinLogin=await auth.LoginWithPinAsync("legacy-user","8642");
        assert(pinLogin.Success,
            "R72 legacy 180k PIN remains valid during seamless upgrade");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="SELECT pin_iterations FROM users WHERE username='legacy-user';";
            assert(Convert.ToInt32(q.ExecuteScalar())==600_000,
                "R72 successful PIN login transparently upgrades PIN work factor to 600k");
        }

        await InsertUserAsync(db,"wrong-user","Correct-Password-2026!","9753",180_000,"PBKDF2-SHA256");
        var wrong=await auth.LoginWithPasswordAsync("wrong-user","Wrong-Password");
        assert(!wrong.Success,"R72 wrong password is rejected before KDF migration");
        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="SELECT password_iterations FROM users WHERE username='wrong-user';";
            assert(Convert.ToInt32(q.ExecuteScalar())==180_000,
                "R72 failed login never rewrites the stored password hash");
        }

        await InsertUserAsync(db,"unsupported-user","Known-Password-2026!","2468",600_000,"UNSUPPORTED-KDF");
        var unsupported=await auth.LoginWithPasswordAsync("unsupported-user","Known-Password-2026!");
        assert(!unsupported.Success,
            "R72 unknown KDF metadata fails closed even when plaintext would otherwise match");

        var status=await auth.GetKdfStatusAsync();
        assert(
            status.CurrentIterations==600_000 && status.LegacySecretCount>=2 &&
            status.UnsupportedSecretCount>=2 && !status.IsHealthy,
            "R72 diagnostic KDF status reports legacy and unsupported credential metadata");

        using (var c=db.OpenConnection())
        using (var q=c.CreateCommand())
        {
            q.CommandText="SELECT COUNT(*) FROM audit_log WHERE event_type IN ('PASSWORD_KDF_UPGRADED','PIN_KDF_UPGRADED');";
            assert(Convert.ToInt32(q.ExecuteScalar())==2,
                "R72 transparent password and PIN rehash events are auditable");
        }
    }

    private static async Task InsertUserAsync(
        SqliteDatabase db,string username,string password,string pin,int iterations,string algorithm)
    {
        var ph=LegacyHash(password,iterations);
        var ih=LegacyHash(pin,iterations);
        await using var c=db.OpenConnection();
        await using var tx=await c.BeginTransactionAsync();
        long id;
        await using (var q=c.CreateCommand())
        {
            q.Transaction=(SqliteTransaction)tx;
            q.CommandText="""
                INSERT INTO users(
                    username,password_hash,password_salt,password_kdf,password_iterations,
                    pin_hash,pin_salt,pin_kdf,pin_iterations,
                    role,is_admin,is_active,must_change_password,failed_attempts,locked_until,created_at)
                VALUES($u,$ph,$ps,$pkdf,$pi,$ih,$is,$ikdf,$ii,'MITARBEITER',0,1,0,0,'',$created);
                SELECT last_insert_rowid();
                """;
            q.Parameters.AddWithValue("$u",username);
            q.Parameters.AddWithValue("$ph",ph.Hash);q.Parameters.AddWithValue("$ps",ph.Salt);
            q.Parameters.AddWithValue("$pkdf",algorithm);q.Parameters.AddWithValue("$pi",iterations);
            q.Parameters.AddWithValue("$ih",ih.Hash);q.Parameters.AddWithValue("$is",ih.Salt);
            q.Parameters.AddWithValue("$ikdf",algorithm);q.Parameters.AddWithValue("$ii",iterations);
            q.Parameters.AddWithValue("$created",DateTimeOffset.Now.ToString("O"));
            id=Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        await using (var q=c.CreateCommand())
        {
            q.Transaction=(SqliteTransaction)tx;
            q.CommandText="INSERT INTO user_permissions(user_id,permissions,credentials_configured) VALUES($id,$p,1);";
            q.Parameters.AddWithValue("$id",id);
            q.Parameters.AddWithValue("$p",(long)(UserPermissions.Sale|UserPermissions.Training));
            await q.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static (string Salt,string Hash) LegacyHash(string secret,int iterations)
    {
        var salt=RandomNumberGenerator.GetBytes(32);
        var hash=Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),salt,iterations,HashAlgorithmName.SHA256,32);
        return (Convert.ToBase64String(salt),Convert.ToBase64String(hash));
    }
}
