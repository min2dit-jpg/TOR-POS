using System.Text.Json;
using TorPos.Core;
namespace TorPos.Infrastructure;

public sealed record PrintJobRecord(
    string Id, string State, string Printer, ReceiptPrintJob? Receipt, ErrorSlipPrintJob? Error, string Note,
    ReportPrintJob? Report = null, KitchenPrintJob? Kitchen = null, PickupSlipPrintJob? PickupSlip = null);
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

    public Task<IReadOnlyList<PrintJobRecord>> GetUncertainAsync() => IoQueue.RunAsync<IReadOnlyList<PrintJobRecord>>(async () =>
    {
        Directory.CreateDirectory(DirectoryPath);
        var jobs=new List<PrintJobRecord>();
        foreach(var p in Directory.GetFiles(DirectoryPath,"*.json"))
        {
            var text=await File.ReadAllTextAsync(p);
            var job=JsonSerializer.Deserialize<PrintJobRecord>(text)
                ?? throw new InvalidDataException("Druckjournal beschädigt.");
            if(job.State is "QUEUED" or "SUBMITTED" or "UNKNOWN" or "NOT_SUBMITTED") jobs.Add(job);
        }
        return jobs;
    });
}
