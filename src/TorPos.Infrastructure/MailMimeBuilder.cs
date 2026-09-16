using System.Globalization;
using System.Net.Mail;
using System.Text;

namespace TorPos.Infrastructure;

public static class MailMimeBuilder
{
    public static async Task<string> BuildAsync(
        string from,
        string to,
        string subject,
        string body,
        IReadOnlyList<string> attachments,
        CancellationToken ct = default)
    {
        from = new MailAddress(from).Address;
        to = new MailAddress(to).Address;

        static IEnumerable<string> Base64Lines(byte[] data)
        {
            var b64 = Convert.ToBase64String(data);
            for (var i = 0; i < b64.Length; i += 76)
                yield return b64.Substring(i, Math.Min(76, b64.Length - i));
        }

        var sb = new StringBuilder();
        sb.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("From: <").Append(from).Append(">\r\n");
        sb.Append("To: <").Append(to).Append(">\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(subject))).Append("?=\r\n");
        sb.Append("MIME-Version: 1.0\r\n");

        if (attachments.Count == 0)
        {
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
            foreach (var line in Base64Lines(Encoding.UTF8.GetBytes(body))) sb.Append(line).Append("\r\n");
            return sb.ToString();
        }

        var boundary = "----TORPOS-" + Guid.NewGuid().ToString("N");
        sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        foreach (var line in Base64Lines(Encoding.UTF8.GetBytes(body))) sb.Append(line).Append("\r\n");

        foreach (var path in attachments)
        {
            var data = await File.ReadAllBytesAsync(path, ct);
            var fileName = Path.GetFileName(path);
            var escaped = Uri.EscapeDataString(fileName);
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Type: application/pdf\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n");
            sb.Append("Content-Disposition: attachment; filename*=UTF-8''").Append(escaped).Append("\r\n\r\n");
            foreach (var line in Base64Lines(data)) sb.Append(line).Append("\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        return sb.ToString();
    }

    public static string ToBase64Url(string mime)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(mime))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
