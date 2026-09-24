using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using TorPos.Core;

namespace TorPos.App;

/// <summary>
/// Werbe-TV web server: a deliberately tiny HTTP/1.1 server on a plain
/// TcpListener (no ASP.NET, no URL reservation, no admin rights) that answers
/// only GET/HEAD for the page, the playlist and the numbered pictures of
/// <see cref="AdTv"/>. It never touches the cart, the checkout or the TSE;
/// the till only hands it a finished slide list.
/// </summary>
internal sealed class AdTvServer : IAsyncDisposable
{
    private const int MaxConnections = 16;
    private const int MaxHeaderBytes = 8 * 1024;
    private const long MaxImageBytes = 30L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private volatile Snapshot _snapshot = new(Array.Empty<CustomerDisplaySlide>(), TimeSpan.FromSeconds(8), "TOR POS", "", "");

    private sealed record Snapshot(
        IReadOnlyList<CustomerDisplaySlide> Slides,
        TimeSpan Interval,
        string Company,
        string Code,
        string Version);

    public int Port { get; private set; }
    public bool Running => _listener is not null;
    public string? LastError { get; private set; }

    /// <summary>Starts, restarts (other port) or stops the server to match the settings.</summary>
    public void Apply(AdTvSettings settings)
    {
        if (!settings.Enabled || !AdTv.IsValidCode(settings.Code))
        {
            Stop();
            return;
        }

        _snapshot = _snapshot with { Code = settings.Code };
        if (Running && Port == settings.Port)
            return;

        Stop();
        try
        {
            var listener = new TcpListener(IPAddress.Any, settings.Port);
            listener.Start(backlog: 32);
            _listener = listener;
            _stop = new CancellationTokenSource();
            Port = settings.Port;
            LastError = null;
            _ = AcceptLoopAsync(listener, _stop.Token);
        }
        catch (Exception ex)
        {
            _listener = null;
            LastError = ex.Message;
            CrashLog.WriteException("Werbe-TV start", ex);
        }
    }

    public void SetSlides(IReadOnlyList<CustomerDisplaySlide> slides, TimeSpan interval, string company)
    {
        var parts = new List<string> { interval.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), company };
        foreach (var slide in slides)
        {
            parts.Add(slide.ImagePath);
            parts.Add(slide.Title);
            parts.Add(slide.PriceText);
            parts.Add(slide.OldPriceText);
            parts.Add(slide.Badge);
            try
            {
                var info = new FileInfo(slide.ImagePath);
                parts.Add(info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-");
            }
            catch
            {
                parts.Add("?");
            }
        }

        var current = _snapshot;
        _snapshot = new Snapshot(slides, interval, company, current.Code, AdTv.Version(parts));
    }

    public void Stop()
    {
        try { _stop?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _stop?.Dispose();
        _stop = null;
        _listener = null;
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    /// <summary>The till's own private LAN addresses, for the address shown in Einstellungen.</summary>
    public static IReadOnlyList<IPAddress> LocalLanAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && AdTv.IsAllowedClient(a))
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<IPAddress>();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch
            {
                return;
            }

            if (!await _connections.WaitAsync(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, ct); }
                catch { /* a broken TV connection is not an error of the till */ }
                finally
                {
                    client.Dispose();
                    _connections.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken serverCt)
    {
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (!AdTv.IsAllowedClient(remote))
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        timeout.CancelAfter(RequestTimeout);
        var ct = timeout.Token;
        var stream = client.GetStream();

        var requestLine = await ReadRequestLineAsync(stream, ct);
        if (requestLine is null)
            return;

        var fields = requestLine.Split(' ');
        if (fields.Length != 3 || !fields[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Bad Request"), head: false, ct);
            return;
        }

        var head = fields[0] == "HEAD";
        var snapshot = _snapshot;
        var route = AdTv.Route(fields[0], fields[1], snapshot.Code);
        switch (route.Kind)
        {
            case AdTvRouteKind.Page:
                await WriteAsync(stream, 200, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(AdTv.PageHtml(snapshot.Company, snapshot.Code)), head, ct);
                return;

            case AdTvRouteKind.Playlist:
                await WriteAsync(stream, 200, "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(AdTv.PlaylistJson(snapshot.Slides, snapshot.Interval, snapshot.Company, snapshot.Code, snapshot.Version)),
                    head, ct);
                return;

            case AdTvRouteKind.Image when route.ImageIndex < snapshot.Slides.Count:
                await WriteImageAsync(stream, snapshot.Slides[route.ImageIndex].ImagePath, head, ct);
                return;

            case AdTvRouteKind.MethodNotAllowed:
                await WriteAsync(stream, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Method Not Allowed"), head: false, ct);
                return;

            default:
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), head, ct);
                return;
        }
    }

    /// <summary>Reads the header block (bounded) and returns its first line.</summary>
    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                return null;
            // Browsers (Chrome) may try https:// first when the address is
            // typed without "http://". A TLS handshake starts with 0x16; close
            // at once so the browser falls back to http:// instead of waiting.
            if (length == 0 && buffer[0] == 0x16)
                return null;
            length += read;

            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var lineEnd = buffer.AsSpan(0, end).IndexOf("\r\n"u8);
                return Encoding.ASCII.GetString(buffer, 0, lineEnd >= 0 ? lineEnd : end);
            }
        }

        return null;
    }

    private static async Task WriteImageAsync(NetworkStream stream, string path, bool head, CancellationToken ct)
    {
        FileStream file;
        try
        {
            if (!CustomerDisplayAds.IsSupportedImage(path) || !File.Exists(path))
            {
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), head, ct);
                return;
            }
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        }
        catch
        {
            await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), head, ct);
            return;
        }

        await using (file)
        {
            if (file.Length > MaxImageBytes)
            {
                await WriteAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Not Found"), head, ct);
                return;
            }

            // The playlist adds ?v=<version> to every picture, so a changed
            // picture gets a new URL and the TV may cache the old one.
            await stream.WriteAsync(Header(200, AdTv.ContentType(path), file.Length, "public, max-age=86400"), ct);
            if (!head)
                await file.CopyToAsync(stream, ct);
        }
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string contentType, byte[] body, bool head, CancellationToken ct)
    {
        await stream.WriteAsync(Header(status, contentType, body.Length, "no-store"), ct);
        if (!head)
            await stream.WriteAsync(body, ct);
    }

    private static byte[] Header(int status, string contentType, long length, string cacheControl)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error"
        };
        return Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {length}\r\n" +
            $"Cache-Control: {cacheControl}\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Connection: close\r\n" +
            (status == 405 ? "Allow: GET, HEAD\r\n" : "") +
            "\r\n");
    }
}
