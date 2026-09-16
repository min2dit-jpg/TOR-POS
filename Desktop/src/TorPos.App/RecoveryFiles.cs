using System.Text;
using TorPos.Infrastructure;
namespace TorPos.App;

internal static class RecoveryFiles
{
    public static Task WriteAsync(string path, string? json) => IoQueue.RunAsync(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (json is null) { if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
        var tmp = path + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes=Encoding.UTF8.GetBytes(json); f.Write(bytes); f.Flush(flushToDisk:true);
        }
        File.Move(tmp, path, true);
        return Task.CompletedTask;
    });
    public static Task<string?> ReadAsync(string path) => IoQueue.RunAsync(() => Task.FromResult(File.Exists(path)?File.ReadAllText(path):null));
    public static Task QuarantineAsync(string path) => IoQueue.RunAsync(() =>
    {
        if (File.Exists(path)) File.Move(path,path+".damaged-"+Guid.NewGuid().ToString("N"));
        // Durable sentinel: reopening must not silently clear the safety lock.
        File.WriteAllText(path+".blocked", "Recovery requires administrator review.");
        return Task.CompletedTask;
    });
}
