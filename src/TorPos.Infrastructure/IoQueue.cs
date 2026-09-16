using System.Threading.Channels;
namespace TorPos.Infrastructure;

// FIFO admission happens on the calling thread, before any Task.Run scheduling.
// Nested repository calls stay in their parent's operation to avoid self-deadlock.
public static class IoQueue
{
    private static readonly AsyncLocal<bool> Inside = new();
    private static readonly Channel<Func<Task>> Queue = Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(2048)
    { SingleReader=true, SingleWriter=false, AllowSynchronousContinuations=false });
    private static readonly Task Worker = Task.Run(async () =>
    {
        await foreach(var action in Queue.Reader.ReadAllAsync())
        {
            Inside.Value=true;
            try { await action().ConfigureAwait(false); }
            finally { Inside.Value=false; }
        }
    });
    public static Task RunAsync(Func<Task> action) => RunAsync(async () => { await action().ConfigureAwait(false); return true; });
    public static Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        if(Inside.Value) return action();
        var completion=new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!Queue.Writer.TryWrite(async () =>
        {
            try { completion.TrySetResult(await action().ConfigureAwait(false)); }
            catch(OperationCanceledException ex) { completion.TrySetCanceled(ex.CancellationToken); }
            catch(Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("Datenwarteschlange voll. Vorgang nicht gestartet."));
        return completion.Task;
    }
}
