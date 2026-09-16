using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using TorPos.Core;
using TorPos.Infrastructure;

// R133: Stamm_TSE data (certificate, public key, signature algorithm, log time
// format) and Im Haus / Außer Haus per sale.
//
// The TSE data are read from the TSE's own TAR export (BSI TR-03153-1): the
// certificate file and a signed log message (TR-03151, ASN.1 DER). The export
// used here is built the same way - a real EC key, a real X.509 certificate and
// DER log messages - so the reader is tested on the structure, not on a mock of
// its own parser. The serial number of a TSE is the SHA-256 of its public key
// (AEAO zu § 146a Nr. 2.2.3.2); a certificate whose key does not hash to the
// serial of the log messages must not be taken.
public static class R133ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        var dir = Path.Combine(root, "r133-tse-master-data");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r133.db"));

        using (var c = db.OpenConnection())
        {
            using var q = c.CreateCommand();
            q.CommandText = "SELECT (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='tse_master_data') + (SELECT COUNT(*) FROM pragma_table_info('sales') WHERE name='im_haus');";
            assert(Convert.ToInt64(q.ExecuteScalar()) == 2 && SchemaMigrationService.TargetSchemaVersion >= 13,
                "R133 schema migration V13 adds tse_master_data and sales.im_haus");
        }

        // ---------- a TSE export ----------
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var certificate = new CertificateRequest("CN=R133 Test TSE", key, HashAlgorithmName.SHA384)
            .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));
        var publicKey = certificate.PublicKey.EncodedKeyValue.RawData;
        var serialBytes = SHA256.HashData(publicKey);
        var serial = Convert.ToHexString(serialBytes);

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var otherCertificate = new CertificateRequest("CN=Other", otherKey, HashAlgorithmName.SHA384)
            .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(5));

        byte[] EcdsaPlainSha384 = { 0x04, 0x00, 0x7F, 0x00, 0x07, 0x01, 0x01, 0x04, 0x01, 0x04 };
        var unixTime = Tlv(0x02, new byte[] { 0x65, 0x53, 0xF1, 0x00 });

        var export = Tar(
            ("info.csv", Encoding.ASCII.GetBytes("description:,\"R133 Test TSE\"\r\n")),
            ("A1B2_X509.cer", certificate.RawData),
            ("Other_X509.cer", otherCertificate.RawData),
            ("Unixt_1700000000_Sig-1_Log-Sys_UpdateTime.log", LogMessage(serialBytes, EcdsaPlainSha384, unixTime, system: true)),
            ("Unixt_1700000001_Sig-2_Log-Tra_No-1_Start_Client-KASSE1.log", LogMessage(serialBytes, EcdsaPlainSha384, unixTime, system: false)));

        var read = TseExportMasterDataReader.Read(new MemoryStream(export));
        assert(read.Count == 1 &&
               read[0].SerialNumber == serial &&
               read[0].SignatureAlgorithm == "ecdsa-plain-SHA384" && read[0].SignatureAlgorithmOid == "0.4.0.127.0.7.1.1.4.1.4" &&
               read[0].LogTimeFormat == "unixTime" && read[0].ProcessDataEncoding == "UTF-8" &&
               read[0].PublicKeyBase64 == Convert.ToBase64String(publicKey) &&
               read[0].CertificateBase64 == Convert.ToBase64String(certificate.RawData),
            "R133 from a TR-03153 export: the certificate whose key hashes to the serial, its public key, ecdsa-plain-SHA384 by OID, unixTime from the log time");

        var pem = Tar(
            ("cert.pem", Encoding.ASCII.GetBytes(certificate.ExportCertificatePem())),
            ("Unixt_1_Sig-1_Log-Tra_No-1_Finish.log", LogMessage(serialBytes, EcdsaPlainSha384, Tlv(0x18, Encoding.ASCII.GetBytes("20260917120000.123Z")), system: false)));
        var fromPem = TseExportMasterDataReader.Read(new MemoryStream(pem));
        assert(fromPem.Count == 1 && fromPem[0].LogTimeFormat == "generalizedTimeWithMilliseconds",
            "R133 a PEM certificate is read too, and a GeneralizedTime with milliseconds gives generalizedTimeWithMilliseconds");

        var utc = TseExportMasterDataReader.TryLogMessage(LogMessage(serialBytes, EcdsaPlainSha384, Tlv(0x17, Encoding.ASCII.GetBytes("260917120000Z")), system: false));
        var generalized = TseExportMasterDataReader.TryLogMessage(LogMessage(serialBytes, EcdsaPlainSha384, Tlv(0x18, Encoding.ASCII.GetBytes("20260917120000Z")), system: false));
        assert(utc?.TimeFormat == "utcTimeWithSeconds" && generalized?.TimeFormat == "generalizedTime",
            "R133 the log time format follows the ASN.1 type of logTime (DSFinV-K Anhang E TSE_ZEITFORMAT)");

        var wrongCertificate = Tar(
            ("Other_X509.cer", otherCertificate.RawData),
            ("Unixt_1_Sig-1_Log-Tra_No-1_Finish.log", LogMessage(serialBytes, EcdsaPlainSha384, unixTime, system: false)));
        assert(TseExportMasterDataReader.Read(new MemoryStream(wrongCertificate)).Count == 0,
            "R133 a certificate whose public key does not hash to the TSE serial is never attributed to that TSE");

        byte[] unknownOid = { 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x04, 0x03, 0x03 }; // 1.2.840.10045.4.3.3 (ecdsa-with-SHA384, X9.62)
        var unknown = TseExportMasterDataReader.Read(new MemoryStream(Tar(
            ("A1B2_X509.cer", certificate.RawData),
            ("Unixt_1_Sig-1_Log-Tra_No-1_Finish.log", LogMessage(serialBytes, unknownOid, unixTime, system: false)))));
        assert(unknown.Count == 1 && unknown[0].SignatureAlgorithm == "" && unknown[0].SignatureAlgorithmOid == "1.2.840.10045.4.3.3",
            "R133 an algorithm OID outside the DSFinV-K list keeps its OID and gets no invented name");

        // ---------- storing ----------
        var audit = new AuditLogRepository(db);
        var repository = new TseMasterDataRepository(db, audit);
        var tarPath = Path.Combine(dir, "export.tar");
        await File.WriteAllBytesAsync(tarPath, export);
        var imported = await repository.ImportFromTarAsync(tarPath, "admin");
        var again = await repository.ImportFromTarAsync(tarPath, "admin");
        var unknownPath = Path.Combine(dir, "export-other-algorithm.tar");
        await File.WriteAllBytesAsync(unknownPath, Tar(
            ("A1B2_X509.cer", certificate.RawData),
            ("Unixt_1_Sig-1_Log-Tra_No-1_Finish.log", LogMessage(serialBytes, unknownOid, unixTime, system: false))));
        await repository.ImportFromTarAsync(unknownPath, "admin");
        var stored = await repository.LoadAllAsync();
        long conflicts;
        await using (var c = db.OpenConnection())
        {
            await using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM audit_log WHERE event_type='TSE_MASTER_DATA_CONFLICT';";
            conflicts = Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        assert(imported.SequenceEqual(new[] { serial }) && again.SequenceEqual(new[] { serial }) &&
               stored.Count == 1 && stored[serial].SignatureAlgorithm == "ecdsa-plain-SHA384" && conflicts == 1,
            "R133 the TSE data are stored once per serial; a later export that disagrees keeps the stored record and is audited");

        var updateRefused = false;
        try
        {
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "UPDATE tse_master_data SET signature_algorithm='x';";
            await q.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { updateRefused = true; }
        assert(updateRefused, "R133 stored TSE master data cannot be changed afterwards");

        // ---------- sales: Im Haus and the export ----------
        var settings = new SettingsRepository(db);
        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R133 Imbiss",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
        });

        var receipt = 133000L;
        async Task<long> SaleAsync(object imHaus, string tseSerial)
        {
            await Task.Delay(15);
            await using var c = db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO sales(receipt_number,created_at,payment_method,subtotal_cents,total_cents,fiscal_status,transaction_type,cash_portion_cents,im_haus) VALUES($r,$at,'CASH',800,800,'TEST_FIXTURE','SALE',800,$h); SELECT last_insert_rowid();";
            q.Parameters.AddWithValue("$r", ++receipt);
            q.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));
            q.Parameters.AddWithValue("$h", imHaus);
            var id = Convert.ToInt64(await q.ExecuteScalarAsync());
            await using var item = c.CreateCommand();
            item.CommandText = "INSERT INTO sale_items(sale_id,product_id,product_name,quantity,unit_price_cents,vat_rate,line_total_cents) VALUES($s,1,'Döner',1,800,19,800);";
            item.Parameters.AddWithValue("$s", id);
            await item.ExecuteNonQueryAsync();
            await new SaleRepository(db).RecordTseResultAsync(id,
                SaleTseResult.SignedResult("KASSE1", "7", "12", tseSerial, "c2ln", DateTimeOffset.UtcNow));
            return id;
        }

        var inHouse = await SaleAsync(1, serial);
        var takeAway = await SaleAsync(0, serial);
        var legacy = await SaleAsync(DBNull.Value, "FEEDFACE");
        var sales = new SaleRepository(db);
        assert((await sales.GetByIdAsync(inHouse))!.ImHaus == true && (await sales.GetByIdAsync(takeAway))!.ImHaus == false && (await sales.GetByIdAsync(legacy))!.ImHaus is null,
            "R133 Im Haus / Außer Haus is read back per sale; a sale from before R133 stays unknown instead of defaulting");

        await Task.Delay(15);
        await new BusinessManagementService(db, settings, audit).CreateZArchiveAsync("kasse1", "TEST");
        var exporter = new DsfinvkExportService(db, settings);
        var from = DateTimeOffset.Now.AddHours(-1);
        var to = DateTimeOffset.Now.AddMinutes(1);
        var report = await exporter.ValidateAsync(from, to);
        var folder = await exporter.ExportAsync(from, to, Path.Combine(dir, "out"));

        var lines = File.ReadAllLines(Path.Combine(folder, "lines.csv")).Skip(1).ToArray();
        string Inhaus(long receiptNumber) => lines.Single(l => l.Contains($";\"{receiptNumber}\";")).Split(';')[10];
        assert(Inhaus(133001) == "\"1\"" && Inhaus(133002) == "\"0\"" && Inhaus(133003) == "",
            "R133 Bonpos.INHAUS is 1 for Im Haus, 0 for Außer Haus and empty only for the sale from before R133");

        var tse = File.ReadAllLines(Path.Combine(folder, "tse.csv")).Skip(1).ToArray();
        var known = tse.Single(l => l.Contains(serial)).Split(';');
        var unknownTse = tse.Single(l => l.Contains("FEEDFACE")).Split(';');
        assert(known[5] == "\"ecdsa-plain-SHA384\"" && known[6] == "\"unixTime\"" && known[7] == "\"UTF-8\"" &&
               known[8] == $"\"{Convert.ToBase64String(publicKey)}\"" &&
               (known[9].Trim('"') + known[10].Trim('"')) == Convert.ToBase64String(certificate.RawData) &&
               unknownTse[5] == "" && unknownTse[8] == "",
            "R133 Stamm_TSE carries algorithm, time format, encoding, public key and the certificate split over ZERTIFIKAT_I/II for the TSE on record");

        assert(report.Issues.Any(x => x.Code == "TSE_STAMMDATEN" && x.Message.Contains("FEEDFACE") && !x.Message.Contains(serial)) &&
               report.Issues.Any(x => x.Code == "INHAUS" && x.Message.StartsWith("1 ")),
            "R133 the hints name only the TSE without stored data and count only the sales without Im Haus information");
    }

    // ------------------------------------------------------------- builders

    private static byte[] Tlv(byte tag, byte[] value)
    {
        var length = value.Length < 0x80
            ? new[] { (byte)value.Length }
            : value.Length < 0x100
                ? new byte[] { 0x81, (byte)value.Length }
                : new byte[] { 0x82, (byte)(value.Length >> 8), (byte)value.Length };
        return new[] { tag }.Concat(length).Concat(value).ToArray();
    }

    private static byte[] LogMessage(byte[] serial, byte[] algorithmOid, byte[] logTime, bool system)
    {
        var parts = new List<byte[]>
        {
            Tlv(0x02, new byte[] { 0x02 }),                                                        // version
            Tlv(0x06, new byte[] { 0x04, 0x00, 0x7F, 0x00, 0x07, 0x03, 0x07, 0x01, system ? (byte)0x02 : (byte)0x01 }),
        };
        if (system)
        {
            parts.Add(Tlv(0x80, Encoding.ASCII.GetBytes("UpdateTime")));
            parts.Add(Tlv(0x81, Encoding.ASCII.GetBytes("system operation data")));
        }
        else
        {
            parts.Add(Tlv(0x80, Encoding.ASCII.GetBytes("StartTransaction")));
            parts.Add(Tlv(0x81, Encoding.ASCII.GetBytes("KASSE1")));
            parts.Add(Tlv(0x82, Array.Empty<byte>()));
            parts.Add(Tlv(0x83, Array.Empty<byte>()));
            parts.Add(Tlv(0x85, new byte[] { 0x01 }));
        }

        parts.Add(Tlv(0x04, serial));                                                               // serialNumber
        parts.Add(Tlv(0x30, Tlv(0x06, algorithmOid)));                                             // signatureAlgorithm
        parts.Add(Tlv(0x02, new byte[] { 0x05 }));                                                 // signatureCounter
        parts.Add(logTime);                                                                         // logTime
        parts.Add(Tlv(0x04, Enumerable.Repeat((byte)0xAB, 96).ToArray()));                         // signatureValue
        return Tlv(0x30, parts.SelectMany(p => p).ToArray());
    }

    private static byte[] Tar(params (string Name, byte[] Content)[] files)
    {
        using var stream = new MemoryStream();
        foreach (var (name, content) in files)
        {
            var header = new byte[512];
            Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
            Encoding.ASCII.GetBytes("0000644").CopyTo(header, 100);
            Encoding.ASCII.GetBytes(Convert.ToString(content.Length, 8).PadLeft(11, '0')).CopyTo(header, 124);
            Encoding.ASCII.GetBytes("00000000000").CopyTo(header, 136);
            header[156] = (byte)'0';
            Encoding.ASCII.GetBytes("ustar").CopyTo(header, 257);
            for (var i = 148; i < 156; i++)
                header[i] = (byte)' ';
            var checksum = header.Sum(b => b);
            Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(header, 148);
            stream.Write(header);
            stream.Write(content);
            var padding = (512 - content.Length % 512) % 512;
            stream.Write(new byte[padding]);
        }

        stream.Write(new byte[1024]);
        return stream.ToArray();
    }
}
