using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using TorPos.Core;

namespace TorPos.Infrastructure;

public static class TorSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TOR-POS-REPORT-SMTP-v1");

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string? protectedValue)
    {
        return TryUnprotect(protectedValue, out var plain) ? plain : "";
    }

    public static bool TryUnprotect(string? protectedValue, out string plain)
    {
        plain = "";
        if (string.IsNullOrWhiteSpace(protectedValue)) return true;
        try
        {
            var bytes = Convert.FromBase64String(protectedValue.Trim());
            plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch
        {
            plain = "";
            return false;
        }
    }
}

public sealed class ReportEmailService
{
    private readonly ISettingsRepository _settings;
    private readonly BusinessManagementService _management;
    private readonly GoogleGmailService? _google;

    public ReportEmailService(ISettingsRepository settings, BusinessManagementService management, GoogleGmailService? google = null)
    {
        _settings = settings;
        _management = management;
        _google = google;
    }

    public async Task<string> SendMonthlyReportsAsync(int year, int month, CancellationToken ct = default)
    {
        var values = await _settings.LoadAllAsync(ct);
        var recipient = values.TryGetValue("reports.email.recipient", out var r) ? r.Trim() : "";
        try { _ = new MailAddress(recipient); }
        catch { throw new InvalidOperationException("Gültige Empfänger-E-Mail fehlt."); }
        var transport = values.TryGetValue("reports.email.transport", out var mode) ? mode.Trim().ToLowerInvariant() : "smtp";

        var documents = await _management.BuildMonthlyReportBundleAsync(year, month, ct);
        var root = Path.Combine(AppPaths.DataDirectory, "Reports", "Monatsberichte");
        Directory.CreateDirectory(root);
        var folder = _management.CreatePdfPackage(documents, root, $"TOR-Monatsberichte-{year:0000}-{month:00}");
        var attachments = Directory.GetFiles(folder, "*.pdf", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToArray();
        var subject = $"TOR POS Monatsberichte {year:0000}-{month:00}";
        var body = $"Anbei die automatisch erstellten TOR POS Monatsberichte für {month:00}/{year:0000}.\r\n\r\n" +
                   "Die Erstellung und der Versand erzeugen keinen Z-Bericht und keinen Kassenabschluss.";

        if (transport == "google")
        {
            if (_google is null) throw new InvalidOperationException("Google-E-Mail-Dienst ist nicht verfügbar. TOR POS neu starten oder SMTP als Fallback wählen.");
            await _google.SendAsync(recipient, subject, body, attachments, ct);
        }
        else
        {
            var config = ReadConfig(values);
            Validate(config);
            await SendAsync(config, subject, body, attachments, ct);
        }
        return folder;
    }

    public async Task SendTestAsync(string recipient, string sender, string host, int port, bool ssl, string username, string password, CancellationToken ct = default)
    {
        var config = new MailConfig(
            recipient.Trim(),
            sender.Trim(),
            host.Trim(),
            port,
            ssl,
            username.Trim(),
            NormalizeAppPassword(password));
        Validate(config);
        await SendAsync(config,
            "TOR POS · E-Mail-Test",
            "Diese Test-E-Mail bestätigt, dass die monatliche Berichtszustellung über die gespeicherten SMTP-Einstellungen funktioniert.",
            Array.Empty<string>(),
            ct);
    }

    public static MailConfig ReadConfig(IReadOnlyDictionary<string,string> values)
    {
        static string Get(IReadOnlyDictionary<string,string> v, string key) => v.TryGetValue(key, out var x) ? x.Trim() : "";
        var recipient = Get(values, "reports.email.recipient");
        var sender = Get(values, "reports.email.sender");
        var host = Get(values, "reports.email.smtp.host");
        var user = Get(values, "reports.email.smtp.user");
        var protectedPassword = Get(values, "reports.email.smtp.password_protected");
        if (!TorSecretProtector.TryUnprotect(protectedPassword, out var plainPassword))
            throw new InvalidOperationException("Das gespeicherte SMTP-App-Passwort konnte mit diesem Windows-Benutzer nicht entschlüsselt werden. Bitte App-Passwort neu eingeben und speichern.");
        var password = NormalizeAppPassword(plainPassword);
        var port = int.TryParse(Get(values, "reports.email.smtp.port"), out var p) ? Math.Clamp(p, 1, 65535) : 587;
        var ssl = !bool.TryParse(Get(values, "reports.email.smtp.ssl"), out var useSsl) || useSsl;
        return new MailConfig(recipient, sender, host, port, ssl, user, password);
    }

    public static string NormalizeAppPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "";
        var result = new StringBuilder(password.Length);
        foreach (var ch in password)
            if (!char.IsWhiteSpace(ch))
                result.Append(ch);
        return result.ToString();
    }

    public static bool LooksMaskedPassword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hasMask = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is not ('*' or '•' or '●')) return false;
            hasMask = true;
        }
        return hasMask;
    }

    public static string ResolveAppPassword(string? enteredPassword, string? storedProtectedPassword)
    {
        var entered = enteredPassword?.Trim() ?? "";
        if (LooksMaskedPassword(entered))
            throw new InvalidOperationException("Maskierte Zeichen sind kein App-Passwort. Bitte das echte Google App-Passwort eingeben.");
        if (!string.IsNullOrWhiteSpace(entered))
        {
            var normalized = NormalizeAppPassword(entered);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("SMTP App-Passwort fehlt.");
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(storedProtectedPassword))
            throw new InvalidOperationException("SMTP App-Passwort fehlt.");
        if (!TorSecretProtector.TryUnprotect(storedProtectedPassword, out var stored))
            throw new InvalidOperationException("Das gespeicherte SMTP-App-Passwort konnte mit diesem Windows-Benutzer nicht entschlüsselt werden. Bitte App-Passwort neu eingeben.");
        var result = NormalizeAppPassword(stored);
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("Das gespeicherte SMTP App-Passwort ist leer. Bitte neu eingeben.");
        return result;
    }

    public static bool IsGmailHost(string? host)
        => string.Equals(host?.Trim(), "smtp.gmail.com", StringComparison.OrdinalIgnoreCase);

    public static void Validate(MailConfig config)
    {
        try { _ = new MailAddress(config.Recipient); }
        catch { throw new InvalidOperationException("Gültige Empfänger-E-Mail fehlt."); }
        try { _ = new MailAddress(config.Sender); }
        catch { throw new InvalidOperationException("Gültige Absender-E-Mail fehlt."); }
        if (string.IsNullOrWhiteSpace(config.Host))
            throw new InvalidOperationException("SMTP-Server fehlt.");
        if (config.Port is < 1 or > 65535)
            throw new InvalidOperationException("SMTP-Port ist ungültig.");
        if (!string.IsNullOrWhiteSpace(config.Username) && string.IsNullOrWhiteSpace(config.Password))
            throw new InvalidOperationException("SMTP-Passwort/App-Passwort fehlt.");

        if (IsGmailHost(config.Host))
        {
            if (config.Port != 587)
                throw new InvalidOperationException("Für Gmail verwendet TOR POS smtp.gmail.com mit Port 587 (STARTTLS).");
            if (!config.UseSsl)
                throw new InvalidOperationException("Für Gmail muss TLS/SSL aktiviert sein (Port 587 / STARTTLS).");
            try { _ = new MailAddress(config.Username); }
            catch { throw new InvalidOperationException("Für Gmail muss SMTP-Benutzer die vollständige Gmail-/Google-Workspace-E-Mail-Adresse sein."); }
            if (string.IsNullOrWhiteSpace(config.Password))
                throw new InvalidOperationException("Google App-Passwort fehlt.");
        }
    }

    private static async Task SendAsync(MailConfig config, string subject, string body, IReadOnlyList<string> attachments, CancellationToken ct)
    {
        // R61: System.Net.Mail.SmtpClient was configured correctly for STARTTLS in R60,
        // but a real Gmail/Windows test still returned 530 / MustIssueStartTlsFirst.
        // The transport below therefore performs RFC 3207 explicitly so AUTH can never
        // be attempted before the TLS upgrade:
        // EHLO -> STARTTLS -> TLS handshake -> EHLO -> AUTH -> MAIL/RCPT/DATA.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await ExplicitStartTlsSmtpSender.SendAsync(config, subject, body, attachments, timeout.Token);
        }
        catch (SmtpStageException ex)
        {
            throw new InvalidOperationException(BuildSafeSmtpError(config, ex), ex);
        }
        catch (AuthenticationException ex)
        {
            throw new InvalidOperationException(
                "TLS-Verbindung zum SMTP-Server fehlgeschlagen.\n" +
                $"Server: {config.Host}:{config.Port}\n" +
                $"Detail: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                "SMTP-Server konnte nicht erreicht werden.\n" +
                $"Server: {config.Host}:{config.Port}\n" +
                $"Detail: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("SMTP-Verbindung hat das Zeitlimit von 60 Sekunden überschritten.");
        }
    }

    private static string BuildSafeSmtpError(MailConfig config, SmtpStageException ex)
    {
        var response = string.IsNullOrWhiteSpace(ex.SafeServerResponse) ? "Keine Serverantwort." : ex.SafeServerResponse;
        var authFailure = ex.Stage.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)
            || ex.ReplyCode is 534 or 535;

        if (IsGmailHost(config.Host) && authFailure)
        {
            return "Gmail-Anmeldung wurde nach erfolgreichem STARTTLS abgelehnt.\n" +
                   $"Phase: {ex.Stage}\n" +
                   $"SMTP-Code: {ex.ReplyCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}\n" +
                   "Prüfen: vollständige Gmail-Adresse, 2-Faktor-Authentifizierung und ein neues 16-stelliges Google App-Passwort.\n" +
                   $"Server: {response}";
        }

        if (IsGmailHost(config.Host) && ex.Stage == "STARTTLS")
        {
            return "Gmail STARTTLS konnte nicht aktiviert werden.\n" +
                   $"SMTP-Code: {ex.ReplyCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}\n" +
                   "Erwartet: smtp.gmail.com / Port 587 / STARTTLS.\n" +
                   $"Server: {response}";
        }

        return "SMTP-Sendung fehlgeschlagen.\n" +
               $"Phase: {ex.Stage}\n" +
               $"SMTP-Code: {ex.ReplyCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}\n" +
               $"Server: {response}";
    }

    private sealed class SmtpStageException : Exception
    {
        public SmtpStageException(string stage, int? replyCode, string safeServerResponse)
            : base($"SMTP phase {stage} failed with code {replyCode?.ToString(CultureInfo.InvariantCulture) ?? "?"}.")
        {
            Stage = stage;
            ReplyCode = replyCode;
            SafeServerResponse = safeServerResponse;
        }

        public string Stage { get; }
        public int? ReplyCode { get; }
        public string SafeServerResponse { get; }
    }

    private static class ExplicitStartTlsSmtpSender
    {
        private sealed record Reply(int Code, string Text, IReadOnlyList<string> Lines);

        public static async Task SendAsync(
            MailConfig config,
            string subject,
            string body,
            IReadOnlyList<string> attachments,
            CancellationToken ct)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(config.Host.Trim(), config.Port, ct);
            await using var network = tcp.GetStream();

            await ExpectAsync(network, "CONNECT", new[] { 220 }, ct);
            var ehlo = await CommandAsync(network, "EHLO tor-pos.local", "EHLO", new[] { 250 }, ct);

            if (!ehlo.Lines.Any(x => x.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                throw new SmtpStageException("STARTTLS", ehlo.Code, "Server bietet STARTTLS nach EHLO nicht an.");

            await CommandAsync(network, "STARTTLS", "STARTTLS", new[] { 220 }, ct);

            await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = config.Host.Trim(),
                EnabledSslProtocols = SslProtocols.None
            }, ct);

            // RFC 3207 requires EHLO again after the TLS state change.
            var secureEhlo = await CommandAsync(tls, "EHLO tor-pos.local", "EHLO-TLS", new[] { 250 }, ct);
            if (!secureEhlo.Lines.Any(x => x.Contains("AUTH", StringComparison.OrdinalIgnoreCase)))
                throw new SmtpStageException("AUTH-CAPABILITY", secureEhlo.Code, "Server bietet nach STARTTLS keine SMTP-Authentifizierung an.");

            if (!string.IsNullOrWhiteSpace(config.Username))
            {
                await CommandAsync(tls, "AUTH LOGIN", "AUTH", new[] { 334 }, ct);
                var user = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Username.Trim()));
                await CommandAsync(tls, user, "AUTH-USER", new[] { 334 }, ct, redactCommand: true);
                var password = Convert.ToBase64String(Encoding.UTF8.GetBytes(NormalizeAppPassword(config.Password)));
                await CommandAsync(tls, password, "AUTH-PASSWORD", new[] { 235 }, ct, redactCommand: true);
            }

            var from = new MailAddress(config.Sender).Address;
            var to = new MailAddress(config.Recipient).Address;
            await CommandAsync(tls, $"MAIL FROM:<{from}>", "MAIL-FROM", new[] { 250 }, ct);
            await CommandAsync(tls, $"RCPT TO:<{to}>", "RCPT-TO", new[] { 250, 251 }, ct);
            await CommandAsync(tls, "DATA", "DATA", new[] { 354 }, ct);

            var mime = await MailMimeBuilder.BuildAsync(from, to, subject, body, attachments, ct);
            var payload = DotStuff(mime) + ".\r\n";
            await WriteAsciiAsync(tls, payload, ct);
            await ExpectAsync(tls, "DATA-COMMIT", new[] { 250 }, ct);

            try { await CommandAsync(tls, "QUIT", "QUIT", new[] { 221 }, ct); }
            catch { /* Mail is already accepted; QUIT failure must not duplicate a sent report. */ }
        }

        private static async Task<Reply> CommandAsync(
            Stream stream,
            string command,
            string stage,
            IReadOnlyCollection<int> expected,
            CancellationToken ct,
            bool redactCommand = false)
        {
            // redactCommand exists to document that credentials must never be surfaced in exceptions/logs.
            _ = redactCommand;
            await WriteAsciiAsync(stream, command + "\r\n", ct);
            return await ExpectAsync(stream, stage, expected, ct);
        }

        private static async Task<Reply> ExpectAsync(Stream stream, string stage, IReadOnlyCollection<int> expected, CancellationToken ct)
        {
            var reply = await ReadReplyAsync(stream, ct);
            if (!expected.Contains(reply.Code))
                throw new SmtpStageException(stage, reply.Code, SanitizeServerReply(reply.Text));
            return reply;
        }

        private static async Task<Reply> ReadReplyAsync(Stream stream, CancellationToken ct)
        {
            var lines = new List<string>();
            string? first = null;
            int code = 0;
            for (var i = 0; i < 100; i++)
            {
                var line = await ReadAsciiLineAsync(stream, ct);
                if (line.Length < 3 || !int.TryParse(line[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var currentCode))
                    throw new SmtpStageException("PROTOCOL", null, "Ungültige SMTP-Serverantwort.");
                first ??= line;
                code = currentCode;
                lines.Add(line);
                if (line.Length < 4 || line[3] != '-')
                    return new Reply(code, string.Join(" | ", lines), lines);
            }
            throw new SmtpStageException("PROTOCOL", code, "SMTP-Serverantwort war ungewöhnlich lang.");
        }

        private static async Task<string> ReadAsciiLineAsync(Stream stream, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var one = new byte[1];
            while (true)
            {
                var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
                if (read == 0) throw new IOException("SMTP-Verbindung wurde vom Server geschlossen.");
                if (one[0] == (byte)'\n') break;
                if (one[0] != (byte)'\r') buffer.WriteByte(one[0]);
                if (buffer.Length > 8192) throw new IOException("SMTP-Serverzeile überschreitet 8192 Bytes.");
            }
            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static async Task WriteAsciiAsync(Stream stream, string value, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        private static string DotStuff(string mime)
        {
            var normalized = mime.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
            var lines = normalized.Split('\n');
            var sb = new StringBuilder(mime.Length + 32);
            foreach (var line in lines)
            {
                if (line.StartsWith(".", StringComparison.Ordinal)) sb.Append('.');
                sb.Append(line).Append("\r\n");
            }
            return sb.ToString();
        }

        private static string SanitizeServerReply(string value)
        {
            // Server replies are safe to expose, but cap their size to avoid filling the UI/log.
            var clean = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return clean.Length <= 1200 ? clean : clean[..1200] + "…";
        }
    }

    public sealed record MailConfig(string Recipient, string Sender, string Host, int Port, bool UseSsl, string Username, string Password);
}

/// <summary>
/// Sends the previous calendar month's report package once. If TOR POS was closed at the due time,
/// the missed mail is sent after the next start. Failures are retried after 15 minutes without
/// advancing the successful-period marker.
/// </summary>
public sealed class MonthlyReportScheduler : IAsyncDisposable
{
    private readonly ISettingsRepository _settings;
    private readonly ReportEmailService _email;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _loop;
    private DateTimeOffset? _retryNotBefore;

    public MonthlyReportScheduler(ISettingsRepository settings, ReportEmailService email, Func<DateTimeOffset>? now = null)
    {
        _settings = settings;
        _email = email;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Start()
    {
        if (_loop is not null) return;
        _loop = RunAsync(_stop.Token);
    }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var values = await _settings.LoadAllAsync(ct);
            if (!ReadBool(values, "reports.email.monthly.enabled", false)) return;
            var now = _now();
            if (_retryNotBefore is { } retry && now < retry) return;

            var day = ReadInt(values, "reports.email.monthly.day", 1, 1, 28);
            var time = ParseTime(values.TryGetValue("reports.email.monthly.time", out var rawTime) ? rawTime : "00:15");
            var due = MostRecentDue(now, day, time);
            if (now < due) return;

            var target = due.LocalDateTime.AddMonths(-1);
            var period = $"{target.Year:0000}-{target.Month:00}";
            if (values.TryGetValue("reports.email.monthly.last_period", out var sent) && string.Equals(sent, period, StringComparison.Ordinal))
                return;

            try
            {
                var folder = await _email.SendMonthlyReportsAsync(target.Year, target.Month, ct);
                var completed = _now();
                await _settings.SaveManyAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reports.email.monthly.last_period"] = period,
                    ["reports.email.monthly.last_success"] = completed.ToString("O"),
                    ["reports.email.monthly.last_error"] = "",
                    ["reports.email.monthly.last_folder"] = folder
                }, ct);
                _retryNotBefore = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _retryNotBefore = now.AddMinutes(15);
                var message = ex.Message.Length > 700 ? ex.Message[..700] : ex.Message;
                try
                {
                    await _settings.SaveManyAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["reports.email.monthly.last_error"] = $"{now:O} · {message}"
                    }, ct);
                }
                catch { }
            }
        }
        finally { _gate.Release(); }
    }

    public static DateTimeOffset MostRecentDue(DateTimeOffset now, int day, TimeOnly time)
    {
        day = Math.Clamp(day, 1, 28);
        var local = now.LocalDateTime;
        var candidate = new DateTime(local.Year, local.Month, day, time.Hour, time.Minute, 0, DateTimeKind.Unspecified);
        if (candidate > local) candidate = candidate.AddMonths(-1);
        return new DateTimeOffset(candidate, TimeZoneInfo.Local.GetUtcOffset(candidate));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await CheckNowAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static TimeOnly ParseTime(string? raw)
    {
        var parts = (raw ?? "").Trim().Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) && h is >= 0 and <= 23 && m is >= 0 and <= 59
            ? new TimeOnly(h, m) : new TimeOnly(0, 15);
    }
    private static bool ReadBool(IReadOnlyDictionary<string,string> v, string key, bool fallback) => v.TryGetValue(key, out var s) && bool.TryParse(s, out var b) ? b : fallback;
    private static int ReadInt(IReadOnlyDictionary<string,string> v, string key, int fallback, int min, int max) => v.TryGetValue(key, out var s) && int.TryParse(s, out var i) ? Math.Clamp(i, min, max) : fallback;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        _stop.Dispose(); _gate.Dispose();
    }
}
