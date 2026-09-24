using System.Text.Json;
using TorPos.Core;
namespace TorPos.Infrastructure;

public sealed record PrintJobRecord(
    string Id, string State, string Printer, ReceiptPrintJob? Receipt, ErrorSlipPrintJob? Error, string Note,
    ReportPrintJob? Report = null, KitchenPrintJob? Kitchen = null, PickupSlipPrintJob? PickupSlip = null,
    bool IsolatedLane = false);
public sealed class PrintJobJournal
{
    private readonly string DirectoryPath;
    public PrintJobJournal(string? directory=null) => DirectoryPath=directory ?? Path.Combine(AppPaths.DataDirectory,"PrintJobs");
    public Task<PrintJobRecord?> GetAsync(string id)=>IoQueue.RunAsync(async ()=>{
        if(!Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Ungültige Druck-ID.");
        var path=Path.Combine(DirectoryPath,id+".json");
        if(!File.Exists(path)) return null;
        var text=await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<PrintJobRecord>(text)??throw new InvalidDataException("Druckjournal beschädigt.");
    });
    public Task SaveAsync(PrintJobRecord job) => IoQueue.RunAsync(async () =>
    {
        Directory.CreateDirectory(DirectoryPath);
        var path=Path.Combine(DirectoryPath,job.Id+".json"); var tmp=path+".tmp";
        await using(var f=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None,bufferSize:4096,useAsync:true))
        {
            var bytes=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));
            await f.WriteAsync(bytes);
            await f.FlushAsync();
            f.Flush(true);
        }
        File.Move(tmp,path,true);
    });
    public async Task<IReadOnlyList<PrintJobRecord>> GetUncertainForPrinterAsync(
        string printer)
    {
        printer = (printer ?? "").Trim();
        if (printer.Length == 0)
            return Array.Empty<PrintJobRecord>();

        return (await GetUncertainAsync())
            .Where(x =>
                string.Equals(
                    x.Printer,
                    printer,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    // O-1: the main receipt printer only answers for its own jobs. A kitchen
    // printer lane (IsolatedLane) has its own queue and review; its unclear
    // job no longer blocks receipts on the main printer. Old journal files
    // without the flag still count, which is the safe side.
    public async Task<IReadOnlyList<PrintJobRecord>> GetUncertainForMainPrinterAsync() =>
        (await GetUncertainAsync()).Where(x => !x.IsolatedLane).ToArray();

    internal const string QuarantineFolder = "Beschaedigt";
    internal static readonly TimeSpan FinishedRetention = TimeSpan.FromDays(30);

    public Task<IReadOnlyList<PrintJobRecord>> GetUncertainAsync() => IoQueue.RunAsync<IReadOnlyList<PrintJobRecord>>(async () =>
    {
        Directory.CreateDirectory(DirectoryPath);
        var jobs=new List<PrintJobRecord>();
        foreach(var p in Directory.GetFiles(DirectoryPath,"*.json"))
        {
            PrintJobRecord? job;
            try
            {
                job=JsonSerializer.Deserialize<PrintJobRecord>(await File.ReadAllTextAsync(p));
            }
            catch(Exception ex) when (ex is JsonException or NotSupportedException)
            {
                job=null;
            }
            if(job is null)
            {
                // O-1: one damaged file used to make the whole journal
                // unreadable, so the printer stayed blocked for good. It is
                // moved aside and reported once as an unclear job that needs
                // the normal paper check.
                var id=Path.GetFileNameWithoutExtension(p);
                Quarantine(p);
                jobs.Add(new PrintJobRecord(Guid.TryParseExact(id,"N",out _)?id:Guid.NewGuid().ToString("N"),"UNKNOWN","",null,null,
                    "Druckjournal-Datei beschädigt und nach PrintJobs/"+QuarantineFolder+" verschoben. Letzten Bon auf Papier prüfen."));
                continue;
            }
            if(job.State is "QUEUED" or "SUBMITTED" or "UNKNOWN" or "NOT_SUBMITTED") jobs.Add(job);
        }
        return jobs;
    });

    // O-1: finished jobs (SPOOL_ACCEPTED, REVIEWED) were kept forever; one
    // file per receipt made the startup scan slower every day. Finished jobs
    // older than the retention are deleted; unclear ones are never touched.
    public Task<int> CleanupFinishedAsync(DateTimeOffset now, TimeSpan? retention = null) => IoQueue.RunAsync<int>(async () =>
    {
        if(!Directory.Exists(DirectoryPath)) return 0;
        var cutoff=(now-(retention??FinishedRetention)).UtcDateTime;
        var removed=0;
        foreach(var p in Directory.GetFiles(DirectoryPath,"*.json"))
        {
            if(File.GetLastWriteTimeUtc(p)>=cutoff) continue;
            try
            {
                var job=JsonSerializer.Deserialize<PrintJobRecord>(await File.ReadAllTextAsync(p));
                if(job?.State is "SPOOL_ACCEPTED" or "REVIEWED")
                {
                    File.Delete(p);
                    removed++;
                }
            }
            catch(Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Damaged files are handled by the uncertain scan; locked
                // files are retried on the next start.
            }
        }
        foreach(var tmp in Directory.GetFiles(DirectoryPath,"*.json.tmp"))
        {
            if(File.GetLastWriteTimeUtc(tmp)>=cutoff) continue;
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    });

    private void Quarantine(string path)
    {
        var folder=Path.Combine(DirectoryPath,QuarantineFolder);
        Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,Path.GetFileName(path));
        if(File.Exists(target)) target=Path.Combine(folder,Path.GetFileNameWithoutExtension(path)+"-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",System.Globalization.CultureInfo.InvariantCulture)+".json");
        File.Move(path,target);
    }
}
