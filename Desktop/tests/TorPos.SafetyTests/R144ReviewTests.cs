using TorPos.Core;
using TorPos.Infrastructure;

// R144: one serial number of the till - TSE client id, receipt, DSFinV-K
// KASSE_SERIENNR and the notification under § 146a Abs. 4 AO.
//
// AEAO zu § 146a Nr. 1.16.2.5 / 2.2.3.1 / 2.4.4 Nr. 6 and DSFinV-K (KASSE_SERIENNR:
// "die Identifikationsnummer …, die … gemäß § 146a Abs. 4 AO zu melden ist";
// QR code: "Seriennummer (Client-Id) der Kasse"). Until R144 the export carried
// TOR's 39-character identity, the TSE a client id typed in by hand (at most 30
// characters, so never the same), and the tax office data were not prepared.
public static class R144ReviewTests
{
    public static async Task Run(string root, Action<bool, string> assert)
    {
        const string identity = "TORPOS-538AE94ABA78A20581812151ACEB4C16";
        var serial = KassenSeriennummer.From(identity);
        assert(serial == "TORPOS-538AE94ABA78A2058181215" && serial.Length == 30 && KassenSeriennummer.IsValid(serial) &&
               KassenSeriennummer.From("KASSE-1") == "KASSE-1" &&
               !KassenSeriennummer.IsValid("KASSE/1") && !KassenSeriennummer.IsValid("KASSE_1") && !KassenSeriennummer.IsValid(identity),
            "R144 the serial number is the identity cut to the 30 characters a TSE client id allows, without \"/\" or \"_\"");

        assert(KassenSeriennummer.ClientIdMatches(" TORPOS-538AE94ABA78A2058181215 ", identity) &&
               !KassenSeriennummer.ClientIdMatches("KASSE-1", identity) &&
               !KassenSeriennummer.ClientIdMatches(identity, identity),
            "R144 the TSE client id matches only the serial number itself");

        var dir = Path.Combine(root, "r144-seriennummer");
        Directory.CreateDirectory(dir);
        var db = await SafetyDatabase.CreateCurrentAsync(Path.Combine(dir, "r144.db"));
        var settings = new SettingsRepository(db);
        var audit = new AuditLogRepository(db);
        var eas = (await new SystemIdentityRepository(db).GetAsync()).EasSerial;

        DsfinvkMasterData master;
        await using (var c = db.OpenConnection())
            master = await DsfinvkMasterDataStore.CurrentAsync(c, CancellationToken.None);
        assert(master.KasseSerial == KassenSeriennummer.From(eas) && master.KasseSerial.Length <= 30 && master.KasseId == eas,
            "R144 the DSFinV-K KASSE_SERIENNR is the serial number; the full identity stays Z_KASSE_ID");

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["company.name"] = "R144 Kiosk",
            ["company.street"] = "Hauptstraße 1",
            ["company.zip"] = "10115",
            ["company.city"] = "Berlin",
            ["company.tax_no"] = "27/123/45678",
            ["tse.client_id"] = "KASSE-1",
            ["tse.bsi_id"] = "BSI-K-TR-0456-2021",
        });
        var management = new BusinessManagementService(db, settings, audit);
        var mismatch = string.Join("\n", (await management.BuildKassenmeldungAsync()).Lines);
        assert(mismatch.Contains("Steuernummer: 27/123/45678") &&
               mismatch.Contains($"Seriennummer: {KassenSeriennummer.From(eas)}") &&
               mismatch.Contains("Zertifizierungs-ID (BSI-K-TR-nnnn-yyyy): BSI-K-TR-0456-2021") &&
               mismatch.Contains("Datum der Anschaffung: FEHLT") &&
               mismatch.Contains("ACHTUNG: TSE-Client-ID ist \"KASSE-1\""),
            "R144 the notification data list tax number, serial number, TSE certification id and dates as AEAO Nr. 1.16.2 asks, and name what is missing or inconsistent");

        await settings.SaveManyAsync(new Dictionary<string, string>
        {
            ["tse.client_id"] = KassenSeriennummer.From(eas),
            ["legal.kassenmeldung.anschaffung"] = "01.09.2026",
        });
        var matching = string.Join("\n", (await management.BuildKassenmeldungAsync()).Lines);
        assert(matching.Contains("Datum der Anschaffung: 01.09.2026") &&
               matching.Contains("= Seriennummer der Kasse - Bon, TSE, DSFinV-K und Mitteilung stimmen überein.") &&
               !matching.Contains("ACHTUNG"),
            "R144 with the client id set to the serial number, TSE, receipt, export and notification agree");
    }
}
