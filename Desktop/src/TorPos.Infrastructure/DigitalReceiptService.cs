using System.Net;
using System.Net.Sockets;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R103: a minimal, hand-rolled HTTP/1.1 GET-only server over a raw
/// TcpListener - deliberately NOT System.Net.HttpListener, which needs a
/// one-time admin "netsh http add urlacl" reservation to bind any prefix
/// other than localhost. A TcpListener bound to IPAddress.Any needs no
/// such reservation and no elevation, which matters here: this is a
/// cashier-facing desktop app that must keep working for a normal Windows
/// user account, not just an admin session. Scope is intentionally tiny -
/// one route ("/r/{token}"), no keep-alive, no HTTPS, no request body
/// handling - this only ever needs to hand a phone browser one receipt
/// page reachable over the shop's own WiFi.
/// </summary>
public sealed class DigitalReceiptService : IDigitalReceiptService
{
    private readonly SqliteDatabase _db;
    private readonly ISettingsRepository _settings;
    private readonly ISaleRepository _sales;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    /// R115: the exact address the listener is bound to. MainWindow builds the
    /// QR URL from this instead of re-detecting the LAN IP, so the advertised
    /// link and the listening socket can never drift apart.
    public string? BoundAddress { get; private set; }

    // R115: a receipt server on the shop LAN gets no authentication beyond the
    // unguessable token, so it must at least not be trivially exhaustible.
    // Before this, every accepted connection was handled with no read timeout,
    // no request size limit and no concurrency cap - a handful of half-open
    // connections could hold sockets open indefinitely.
    private const int MaxConcurrentConnections = 16;
    private const int MaxRequestLineBytes = 8 * 1024;
    private const int MaxHeaderLines = 50;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _connections = new(MaxConcurrentConnections, MaxConcurrentConnections);

    public DigitalReceiptService(SqliteDatabase db, ISettingsRepository settings, ISaleRepository sales)
    {
        _db = db;
        _settings = settings;
        _sales = sales;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        // R115: bind to one specific address instead of IPAddress.Any. Any
        // bound the receipt server to EVERY interface - including a guest
        // WiFi or a WAN-facing NIC - and it serves fiscal receipt content over
        // plain HTTP. Nothing is lost by narrowing it: the QR URL is built
        // from this very address, so without a reachable IPv4 the feature
        // cannot work at all and the server has no reason to listen.
        //
        // An explicit address can be configured for a till with several
        // adapters (the auto-detection takes the first "Up" non-loopback IPv4,
        // which on a machine with an active VPN may not be the shop LAN).
        var configured = (await _settings.GetAsync("receipt.digital_qr.bind_address", "", ct)).Trim();
        var lanIp = configured.Length > 0 ? configured : LocalNetworkAddress.FindLanIPv4();
        if (lanIp is null || !IPAddress.TryParse(lanIp, out var bindAddress))
            return;

        var port = ParsePort(await _settings.GetAsync("receipt.digital_qr.port", "8099", ct));
        var listener = new TcpListener(bindAddress, port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            // Port already in use or blocked - fail quietly. The caller
            // (MainWindow) checks IsRunning before offering the QR at all.
            return;
        }

        _listener = listener;
        Port = port;
        BoundAddress = lanIp;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public Task StopAsync()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    public async Task<string> RegisterAsync(long saleId, CancellationToken ct = default)
    {
        var token = DigitalReceiptToken.New();
        await using var c = _db.OpenConnection();
        await using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO digital_receipts(token,sale_id,created_at) VALUES($t,$s,$now);";
        q.Parameters.AddWithValue("$t", token);
        q.Parameters.AddWithValue("$s", saleId);
        q.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
        await q.ExecuteNonQueryAsync(ct);
        return token;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            // R115: refuse rather than queue when the cap is reached, so a
            // flood of connections can never pile up unbounded.
            if (!await _connections.WaitAsync(TimeSpan.Zero, ct))
                return;

            try
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestCts.CancelAfter(RequestTimeout);
                var requestCt = requestCts.Token;

                client.ReceiveTimeout = (int)RequestTimeout.TotalMilliseconds;
                client.SendTimeout = (int)RequestTimeout.TotalMilliseconds;

                using var stream = client.GetStream();
                var requestLine = await ReadLineAsync(stream, requestCt);
                // Headers are read (to consume the request properly) but
                // never inspected - this server has exactly one GET route
                // and needs nothing from them.
                string? header;
                var headerLines = 0;
                do
                {
                    header = await ReadLineAsync(stream, requestCt);
                    if (++headerLines > MaxHeaderLines)
                        return;
                }
                while (!string.IsNullOrEmpty(header));

                var (status, body) = await BuildResponseAsync(requestLine, requestCt);
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var head =
                    $"HTTP/1.1 {status}\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n" +
                    "Cache-Control: no-store\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), requestCt);
                await stream.WriteAsync(bodyBytes, requestCt);
                await stream.FlushAsync(requestCt);
            }
            catch
            {
                // A malformed request or a client that disconnects mid-read
                // must never take the server down - just drop that connection.
            }
            finally
            {
                _connections.Release();
            }
        }
    }

    private async Task<(string Status, string Body)> BuildResponseAsync(string? requestLine, CancellationToken ct)
    {
        var parts = (requestLine ?? "").Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
            return ("400 Bad Request", "<html><body>400 Bad Request</body></html>");

        var rawPath = parts[1].Split('?')[0];
        var path = Uri.UnescapeDataString(rawPath);
        if (!path.StartsWith("/r/", StringComparison.Ordinal) || path.Length <= 3)
            return ("404 Not Found", "<html><body>Nicht gefunden.</body></html>");

        var token = path[3..];

        // R115: tokens now expire. The 404 text below always claimed a link
        // could be "abgelaufen", but nothing ever enforced it - a QR kept
        // working forever. The window is configurable because it is a real
        // shop decision, not a technical constant.
        var ttlHours = ParseTtlHours(await _settings.GetAsync("receipt.digital_qr.ttl_hours", "24", ct));
        var expiresBefore = DateTimeOffset.Now.AddHours(-ttlHours).ToString("O");

        long? saleId = null;
        await using (var c = _db.OpenConnection())
        await using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT sale_id FROM digital_receipts WHERE token=$t AND created_at >= $oldest;";
            q.Parameters.AddWithValue("$t", token);
            q.Parameters.AddWithValue("$oldest", expiresBefore);
            var value = await q.ExecuteScalarAsync(ct);
            if (value is not null and not DBNull) saleId = Convert.ToInt64(value);
        }

        if (saleId is null)
            return ("404 Not Found", "<html><body>Bon nicht gefunden oder Link abgelaufen.</body></html>");

        var sale = await _sales.GetByIdAsync(saleId.Value, ct);
        if (sale is null)
            return ("404 Not Found", "<html><body>Bon nicht gefunden.</body></html>");

        var values = await _settings.LoadAllAsync(ct);
        string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        var companyName = Get("company.name", "TOR POS Pro");
        var address = string.Join(" ", new[] { "company.street", "company.zip", "company.city" }
            .Select(k => Get(k)).Where(v => !string.IsNullOrWhiteSpace(v)));
        var taxNumber = Get("company.tax_no");
        var vatId = Get("company.vat_id");

        // R107: a blank TSE signature alone does NOT mean "test sale" - a
        // real sale caught in a genuine TSE hardware outage also has no
        // signature (SaleFiscalSigningService.ApplyAsync sets TseOutage
        // instead). Only "no signature AND no recorded outage" is an actual
        // test/training/simulation sale that never reached signing at all;
        // an outage sale must fall through to DigitalReceiptHtml.Render's
        // own TseOutage-aware "TSE-Ausfall" messaging, not show "TESTBON"
        // (found by the user's own source review - previously this server
        // had no way to tell the two apart).
        var fiscalTestMode = string.IsNullOrWhiteSpace(sale.TseSignature) && !sale.TseOutage;

        // R122 (F5): the page now checks the same mandatory-field list the
        // printed receipt does, and the eAS serial is one of them.
        var easSerial = await ReadEasSerialAsync(ct);

        return ("200 OK", DigitalReceiptHtml.Render(sale, companyName, address, taxNumber, vatId, fiscalTestMode, easSerial));
    }

    private async Task<string> ReadEasSerialAsync(CancellationToken ct)
    {
        try
        {
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT eas_serial FROM system_identity WHERE id=1;";
            // R144: the serial number of the till, as on the printed receipt.
            return KassenSeriennummer.From((await q.ExecuteScalarAsync(ct))?.ToString() ?? "");
        }
        catch
        {
            // A missing serial makes the page report an incomplete Beleg,
            // which is the honest outcome; it must not break serving it.
            return "";
        }
    }

    private static async Task<string?> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0)
                return buffer.Count == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());

            if (one[0] == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                return Encoding.ASCII.GetString(buffer.ToArray());
            }

            // R115: a client that never sends a newline used to grow this
            // buffer without any limit.
            if (buffer.Count >= MaxRequestLineBytes)
                throw new InvalidDataException("Request line exceeds the allowed length.");

            buffer.Add(one[0]);
            if (buffer.Count > 8192)
                throw new InvalidOperationException("Request line too long.");
        }
    }

    private static int ParsePort(string value) =>
        int.TryParse(value, out var parsed) && parsed is > 0 and < 65536 ? parsed : 8099;

    /// R115: clamped rather than free-form - 0 would make every QR dead on
    /// arrival, and an unbounded value would defeat the expiry entirely.
    /// Max 8760 hours = one year.
    private static int ParseTtlHours(string value) =>
        int.TryParse(value, out var parsed) && parsed is > 0 and <= 8760 ? parsed : 24;
}
