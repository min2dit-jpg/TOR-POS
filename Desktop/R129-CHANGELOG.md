# R129 — No Nachsignieren After a TSE Outage: Decided by the Legal Position

Closes the open fiscal decision R122 left on purpose (audit finding F6).

## The question

`sale_tse_signatures` holds exactly one final record per sale: a signature or
an outage. A sale recorded during a TSE outage can therefore never be signed
afterwards. R122 kept that deliberately but marked it as open: should sales from
an outage be signed once the TSE is back? The owner's instruction was to do
whatever the law says.

## What the law says

**AEAO zu § 146a, Nr. 1.14 — Ausfall der TSE** (BMF-Schreiben of 30.06.2023):

- 1.14.1 outage times and the reason are documented (may be automated);
- 1.14.2 the outage must be visible on the receipt — missing transaction number
  or a unique marking;
- 1.14.3 the system may keep being used until the cause is removed, the
  Belegausgabepflicht is unaffected;
- 1.14.4 the cause is removed without delay.

**Nr. 2.7:** the Belegausgabepflicht only lapses when the whole system or the
print/transmission path fails.

The **amendment of 17.03.2026** (GZ IV D 2 - S 0316-a/00027/008/020, following
the second KassenSichV amendment) was read in full: it changes Nr. 1.4, 1.12,
2.2, 2.4, 2.5.7 and the taxameter/Wegstreckenzähler parts, and leaves 1.14 and
2.7 untouched.

**DSFinV-K, file `TSE_Transaktionen`:** field `TSE_TA_FEHLER` — a free text for
problems in the communication between the recording system and the TSE. That is
where an outage sale is explained in an export.

**Nachsignieren appears nowhere** — not in § 146a AO, the KassenSichV, the AEAO
or the DSFinV-K. It could not repair anything either: **§ 2 KassenSichV**
requires the transaction to be started *unmittelbar*, with the times fixed by
the security module. A signature obtained hours later carries the TSE's later
time, not the time of the sale, so the sale would still not have been secured
when it happened — it would only look as if it had been.

Some vendors re-sign outage receipts as a product feature; others state the
legislator does not provide for it. Neither is the law. The law asks for the
four points above, and TOR does not add a signature the rules do not ask for and
that would misstate when the sale was secured.

## Decision

**No Nachsignieren.** `sale_tse_signatures` stays one final record per sale.

TOR already meets all four points of Nr. 1.14:

| AEAO | TOR |
|---|---|
| 1.14.1 times and reason | `tse_outage_log`: `started_at`, `ended_at`, `reason`; delete forbidden by trigger; opened/closed automatically by `TseFailSafeService` on every failed or successful TSE call and probe, each with an audit entry |
| 1.14.2 visible on the receipt | "TSE-AUSFALL / Vorgang ohne TSE-Signatur" on the Bon (R113; before R113 an unavailable TSE produced no note at all) |
| 1.14.3 keep selling, receipt still issued | sales continue during an outage and print a receipt |
| 1.14.4 fix without delay | TSE-AUSFALL badge in the till header while an outage is open (R113), so it cannot go unnoticed |

## Changed

No program behaviour changed; comments and documents only.

- `SchemaMigrationService.cs` migration 8 and `Infrastructure.cs`
  `RecordTseResultAsync`: the "deliberate but still open" wording replaced by the
  legal basis. Migrations run by version number, not by text checksum, so
  editing a migration's SQL comment does not affect existing databases.
- `RECHTLICHE-ANFORDERUNGEN-DE.md` section 7: legal basis, the no-re-signing
  rule and the official sources.
- `ROADMAP.md`: the open decision is closed; new item for the DSFinV-K export
  (still blocked on purpose): list outage sales in `TSE_Transaktionen` without
  signature fields and fill `TSE_TA_FEHLER` from `tse_outage_log.reason`
  (max. 200 characters) — neither drop them nor sign them later.

## Sources

- AEAO zu § 146a AO, BMF 30.06.2023:
  https://www.bundesfinanzministerium.de/Content/DE/Downloads/BMF_Schreiben/Weitere_Steuerthemen/Abgabenordnung/AO-Anwendungserlass/2023-06-30-AEAO-Par-146-AO.pdf
- Änderung des AEAO zu § 146a, BMF 17.03.2026:
  https://www.bundesfinanzministerium.de/Content/DE/Downloads/BMF_Schreiben/Weitere_Steuerthemen/Abgabenordnung/AO-Anwendungserlass/2026-03-17-aenderung-aeao-146a.pdf
- § 2 KassenSichV: https://www.gesetze-im-internet.de/kassensichv/__2.html
- DSFinV-K (BZSt):
  https://www.bzst.de/DE/Unternehmen/Aussenpruefungen/DigitaleSchnittstelleFinV/digitaleschnittstellefinv_node.html

This is a technical reading of the official texts, not tax advice. It should be
confirmed by a Steuerberater before production use.

## Testing

Safety suite unchanged at 671/671 — no behaviour changed, and R122 already
tests that a second TSE result for the same sale is refused with a clear rule
instead of a raw UNIQUE error.
