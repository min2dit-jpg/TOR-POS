using System.Net.Sockets;

namespace TorPos.Infrastructure;

public sealed record FiskaltrustConnectivityReport(
    bool QueueTcpReachable,
    bool ScuTcpReachable,
    bool EchoSucceeded,
    string QueueTarget,
    string ScuTarget,
    string EchoMessage,
    IReadOnlyList<string> Errors,
    DateTimeOffset CheckedAt)
{
    public bool BasicConnectivityOk =>
        QueueTcpReachable &&
        ScuTcpReachable &&
        EchoSucceeded;
}

/// <summary>
/// Side-effect-free connectivity diagnostics for a local fiskaltrust setup.
/// It only opens TCP connections and calls Echo. No Sign/receipt/TSE operation
/// is created here.
/// </summary>
public sealed class FiskaltrustDiagnosticsService
{
    private readonly IFiskaltrustMiddlewareClient _client;

    public FiskaltrustDiagnosticsService(
        IFiskaltrustMiddlewareClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<FiskaltrustConnectivityReport> CheckAsync(
        Uri queueBaseUri,
        string scuHost = "127.0.0.1",
        int scuPort = 1401,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queueBaseUri);

        if (scuPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(
                nameof(scuPort),
                "SCU-Port muss zwischen 1 und 65535 liegen.");

        var normalized =
            new FiskaltrustMiddlewareOptions(
                queueBaseUri,
                Guid.Empty,
                Guid.Empty,
                "")
            .HttpBaseUri;

        var queueHost = normalized.Host;
        var queuePort = normalized.IsDefaultPort
            ? normalized.Scheme == Uri.UriSchemeHttps ? 443 : 80
            : normalized.Port;

        var queueTask =
            ProbeTcpAsync(queueHost, queuePort, TimeSpan.FromSeconds(2), ct);
        var scuTask =
            ProbeTcpAsync(scuHost, scuPort, TimeSpan.FromSeconds(2), ct);

        await Task.WhenAll(queueTask, scuTask);

        var queueOk = await queueTask;
        var scuOk = await scuTask;
        var echoOk = false;
        var echoMessage = "";
        var errors = new List<string>();

        if (!queueOk)
        {
            errors.Add(
                $"Queue TCP nicht erreichbar: {queueHost}:{queuePort}");
        }
        else
        {
            try
            {
                const string probe = "TOR POS DIAG";
                echoMessage = await _client.EchoAsync(probe, ct);
                echoOk = string.Equals(
                    echoMessage,
                    probe,
                    StringComparison.Ordinal);

                if (!echoOk)
                {
                    errors.Add(
                        $"Echo-Antwort unerwartet: '{echoMessage}'");
                }
            }
            catch (Exception ex)
            {
                errors.Add("Echo fehlgeschlagen: " + ex.Message);
            }
        }

        if (!scuOk)
        {
            errors.Add(
                $"SCU TCP nicht erreichbar: {scuHost}:{scuPort}");
        }

        return new FiskaltrustConnectivityReport(
            queueOk,
            scuOk,
            echoOk,
            $"{queueHost}:{queuePort}",
            $"{scuHost}:{scuPort}",
            echoMessage,
            errors,
            DateTimeOffset.Now);
    }

    private static async Task<bool> ProbeTcpAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken ct)
    {
        try
        {
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, linked.Token);
            return tcp.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
