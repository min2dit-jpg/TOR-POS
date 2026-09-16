using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

/// <summary>
/// R133: reads Stamm_TSE data from a TSE TAR export (BSI TR-03153-1, chapter 5).
///
/// Nothing is taken from vendor-specific native calls whose exact signature is
/// not verified here: a wrong P/Invoke signature could crash the till. The
/// export is the standardised format every certified TSE must produce. It
/// contains the TSE certificate(s) and the signed log messages (ASN.1 DER,
/// BSI TR-03151), and the serial number of the TSE is by definition the
/// SHA-256 of its public key - so a certificate is only accepted for a serial
/// when that hash matches.
/// </summary>
public static class TseExportMasterDataReader
{
    public static IReadOnlyList<TseMasterData> Read(Stream tar)
    {
        var certificates = new Dictionary<string, (byte[] Der, byte[] PublicKey)>(StringComparer.OrdinalIgnoreCase);
        var logs = new Dictionary<string, (string AlgorithmOid, string TimeFormat)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, content) in TarEntries(tar))
        {
            // Log messages end in ".log" (TR-03153-1 file naming); everything
            // else is tried as a certificate, DER or PEM.
            if (!name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && TryCertificate(content) is { } certificate)
            {
                var serial = Convert.ToHexString(SHA256.HashData(certificate.PublicKey));
                certificates.TryAdd(serial, certificate);
                continue;
            }

            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && TryLogMessage(content) is { } log)
                logs.TryAdd(log.Serial, (log.AlgorithmOid, log.TimeFormat));
        }

        var result = new List<TseMasterData>();
        foreach (var (serial, log) in logs)
        {
            if (!certificates.TryGetValue(serial, out var certificate))
                continue;

            result.Add(new TseMasterData(
                SerialNumber: serial.ToUpperInvariant(),
                SignatureAlgorithm: TseSignatureAlgorithms.NameOf(log.AlgorithmOid),
                SignatureAlgorithmOid: log.AlgorithmOid,
                LogTimeFormat: log.TimeFormat,
                ProcessDataEncoding: "UTF-8",
                PublicKeyBase64: Convert.ToBase64String(certificate.PublicKey),
                CertificateBase64: Convert.ToBase64String(certificate.Der)));
        }

        return result;
    }

    // ------------------------------------------------------------------- TAR

    private static IEnumerable<(string Name, byte[] Content)> TarEntries(Stream tar)
    {
        var header = new byte[512];
        while (ReadExactly(tar, header))
        {
            if (header.All(b => b == 0))
                yield break;

            var name = Text(header, 0, 100);
            var prefix = Text(header, 345, 155);
            if (prefix.Length > 0)
                name = prefix + "/" + name;

            var sizeText = Text(header, 124, 12).Trim();
            var size = sizeText.Length == 0 ? 0L : Convert.ToInt64(sizeText, 8);
            var type = (char)header[156];

            var content = new byte[size];
            if (size > 0 && !ReadExactly(tar, content))
                yield break;

            var padding = (512 - size % 512) % 512;
            if (padding > 0 && !ReadExactly(tar, new byte[padding]))
                yield break;

            if (type is '0' or '\0')
                yield return (name, content);
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                return false;
            read += n;
        }

        return true;
    }

    private static string Text(byte[] buffer, int offset, int length)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset, length);
        return Encoding.ASCII.GetString(buffer, offset, (end < 0 ? offset + length : end) - offset);
    }

    // ----------------------------------------------------------- certificate

    private static (byte[] Der, byte[] PublicKey)? TryCertificate(byte[] content)
    {
        try
        {
            var text = Encoding.ASCII.GetString(content);
            using var certificate = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
                ? X509Certificate2.CreateFromPem(text)
                : content.Length > 0 && content[0] == 0x30
                    ? X509CertificateLoader.LoadCertificate(content)
                    : null;

            if (certificate is null)
                return null;

            return (certificate.RawData, certificate.PublicKey.EncodedKeyValue.RawData);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ log message

    /// <summary>
    /// TR-03151 log message: SEQUENCE { version, certifiedDataType, certified
    /// data..., serialNumber OCTET STRING, signatureAlgorithm SEQUENCE { OID,
    /// ... }, [seAuditData OCTET STRING], [signatureCounter INTEGER], logTime
    /// CHOICE { UTCTime | GeneralizedTime | INTEGER }, signatureValue OCTET STRING }.
    /// The certified data use context tags, so the first OCTET STRING directly
    /// followed by an algorithm SEQUENCE is the serial number, and the element
    /// before the final signature is the log time.
    /// </summary>
    public static (string Serial, string AlgorithmOid, string TimeFormat)? TryLogMessage(byte[] der)
    {
        try
        {
            var outer = Der.Read(der, 0);
            if (outer.Tag != 0x30)
                return null;

            var children = Der.Children(der, outer);
            for (var i = 0; i + 1 < children.Count; i++)
            {
                if (children[i].Tag != 0x04 || children[i + 1].Tag != 0x30)
                    continue;

                var algorithm = Der.Children(der, children[i + 1]);
                if (algorithm.Count == 0 || algorithm[0].Tag != 0x06)
                    continue;

                var last = children[^1];
                var time = children[^2];
                if (last.Tag != 0x04 || children.Count - 2 <= i + 1)
                    return null;

                var format = time.Tag switch
                {
                    0x02 => "unixTime",
                    0x17 => time.Length == 11 ? "utcTime" : "utcTimeWithSeconds",
                    0x18 => Encoding.ASCII.GetString(der, time.ValueOffset, time.Length).Contains('.') ? "generalizedTimeWithMilliseconds" : "generalizedTime",
                    _ => ""
                };
                if (format.Length == 0)
                    return null;

                return (
                    Convert.ToHexString(der, children[i].ValueOffset, children[i].Length),
                    Der.ObjectIdentifier(der, algorithm[0]),
                    format);
            }

            return null;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    internal static class Der
    {
        public readonly record struct Element(byte Tag, int ValueOffset, int Length)
        {
            public int End => ValueOffset + Length;
        }

        public static Element Read(byte[] data, int offset)
        {
            var tag = data[offset];
            var first = data[offset + 1];
            int length, valueOffset;
            if (first < 0x80)
            {
                length = first;
                valueOffset = offset + 2;
            }
            else
            {
                var count = first & 0x7F;
                if (count is 0 or > 4)
                    throw new ArgumentException("DER length");
                length = 0;
                for (var i = 0; i < count; i++)
                    length = checked((length << 8) | data[offset + 2 + i]);
                valueOffset = offset + 2 + count;
            }

            if (length < 0 || valueOffset + length > data.Length)
                throw new ArgumentException("DER length");
            return new Element(tag, valueOffset, length);
        }

        public static List<Element> Children(byte[] data, Element parent)
        {
            var children = new List<Element>();
            var offset = parent.ValueOffset;
            while (offset < parent.End)
            {
                var child = Read(data, offset);
                children.Add(child);
                offset = child.End;
            }

            return children;
        }

        public static string ObjectIdentifier(byte[] data, Element oid)
        {
            var parts = new List<string>();
            var first = data[oid.ValueOffset];
            parts.Add((first / 40).ToString(CultureInfo.InvariantCulture));
            parts.Add((first % 40).ToString(CultureInfo.InvariantCulture));
            long value = 0;
            for (var i = oid.ValueOffset + 1; i < oid.End; i++)
            {
                value = checked((value << 7) | (long)(data[i] & 0x7F));
                if ((data[i] & 0x80) == 0)
                {
                    parts.Add(value.ToString(CultureInfo.InvariantCulture));
                    value = 0;
                }
            }

            return string.Join(".", parts);
        }
    }
}

/// <summary>R133: stores Stamm_TSE data once per TSE serial number.</summary>
public sealed class TseMasterDataRepository
{
    private readonly SqliteDatabase _db;
    private readonly IAuditLog _audit;

    public TseMasterDataRepository(SqliteDatabase db, IAuditLog audit)
    {
        _db = db;
        _audit = audit;
    }

    /// <summary>
    /// Reads a TSE TAR export and stores what it finds. A TSE's certificate
    /// and algorithm do not change; if a different record already exists for
    /// the same serial, the stored one is kept and the conflict is audited.
    /// Returns the serial numbers now on record from this export.
    /// </summary>
    public async Task<IReadOnlyList<string>> ImportFromTarAsync(string tarPath, string actor, CancellationToken ct = default)
    {
        IReadOnlyList<TseMasterData> found;
        await using (var stream = File.OpenRead(tarPath))
            found = TseExportMasterDataReader.Read(stream);

        return await IoQueue.RunAsync(async () =>
        {
            var recorded = new List<string>();
            await using var c = _db.OpenConnection();
            foreach (var data in found)
            {
                var existing = await LoadAsync(c, data.SerialNumber, ct);
                if (existing is null)
                {
                    await using var q = c.CreateCommand();
                    q.CommandText = """
                        INSERT INTO tse_master_data(
                          serial_number,signature_algorithm,signature_algorithm_oid,log_time_format,
                          process_data_encoding,public_key,certificate,source,recorded_at)
                        VALUES($serial,$algo,$oid,$time,$enc,$key,$cert,$source,$at);
                        """;
                    q.Parameters.AddWithValue("$serial", data.SerialNumber);
                    q.Parameters.AddWithValue("$algo", data.SignatureAlgorithm);
                    q.Parameters.AddWithValue("$oid", data.SignatureAlgorithmOid);
                    q.Parameters.AddWithValue("$time", data.LogTimeFormat);
                    q.Parameters.AddWithValue("$enc", data.ProcessDataEncoding);
                    q.Parameters.AddWithValue("$key", data.PublicKeyBase64);
                    q.Parameters.AddWithValue("$cert", data.CertificateBase64);
                    q.Parameters.AddWithValue("$source", Path.GetFileName(tarPath));
                    q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
                    await q.ExecuteNonQueryAsync(ct);
                    await _audit.WriteAsync(actor, "TSE_MASTER_DATA_RECORDED", "TSE", data.SerialNumber,
                        $"algorithm={data.SignatureAlgorithm} ({data.SignatureAlgorithmOid}); log_time={data.LogTimeFormat}; source={Path.GetFileName(tarPath)}", ct);
                }
                else if (existing != data)
                {
                    await _audit.WriteAsync(actor, "TSE_MASTER_DATA_CONFLICT", "TSE", data.SerialNumber,
                        $"stored record kept; export {Path.GetFileName(tarPath)} differs", ct);
                }

                recorded.Add(data.SerialNumber);
            }

            return (IReadOnlyList<string>)recorded;
        });
    }

    public Task<IReadOnlyDictionary<string, TseMasterData>> LoadAllAsync(CancellationToken ct = default) =>
        IoQueue.RunAsync(async () =>
        {
            var all = new Dictionary<string, TseMasterData>(StringComparer.OrdinalIgnoreCase);
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT serial_number,signature_algorithm,signature_algorithm_oid,log_time_format,process_data_encoding,public_key,certificate FROM tse_master_data;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                all[r.GetString(0)] = Row(r);
            return (IReadOnlyDictionary<string, TseMasterData>)all;
        });

    internal static async Task<Dictionary<string, TseMasterData>> LoadAllAsync(SqliteConnection c, CancellationToken ct)
    {
        var all = new Dictionary<string, TseMasterData>(StringComparer.OrdinalIgnoreCase);
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT serial_number,signature_algorithm,signature_algorithm_oid,log_time_format,process_data_encoding,public_key,certificate FROM tse_master_data;";
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            all[r.GetString(0)] = Row(r);
        return all;
    }

    private static async Task<TseMasterData?> LoadAsync(SqliteConnection c, string serial, CancellationToken ct)
    {
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT serial_number,signature_algorithm,signature_algorithm_oid,log_time_format,process_data_encoding,public_key,certificate FROM tse_master_data WHERE serial_number=$serial;";
        q.Parameters.AddWithValue("$serial", serial);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Row(r) : null;
    }

    private static TseMasterData Row(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));
}
