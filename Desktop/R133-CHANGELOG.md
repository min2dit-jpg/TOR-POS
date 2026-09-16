# R133 — TSE Master Data and Im Haus / Außer Haus per Sale

Two of the data gaps the R131 export named.

## 1. Stamm_TSE: certificate, public key, algorithm, log time format

DSFinV-K 3.2.7 (`Stamm_TSE`) asks per TSE for the serial number, the signature
algorithm (`TSE_SIG_ALGO`, e.g. `ecdsa-plain-SHA384`), the log time format
(`TSE_ZEITFORMAT`), the processData encoding, the public key and the
certificate (base64, 1,000 characters per field). TOR stored only the serial.

**Source: the TSE's own TAR export, not native calls.** The Swissbit SDK
headers are not available here, and a guessed P/Invoke signature could crash the
till on the first real TSE. The TAR export is the standardised format every
certified TSE must produce (BSI TR-03153-1, chapter 5). It contains the TSE
certificate and the signed log messages (BSI TR-03151, ASN.1 DER), and those
hold everything needed:

- **public key and certificate** from the certificate file (DER or PEM);
- **matched by definition:** the TSE serial number is the SHA-256 of its public
  key (AEAO zu § 146a Nr. 2.2.3.2). A certificate is only attributed to a TSE
  when its key hashes to the serial found in the log messages — a CA
  certificate or another TSE's certificate in the same archive is never taken;
- **signature algorithm** from the `signatureAlgorithm` OID of a log message,
  named after BSI TR-03111 (`ecdsa-plain-SHA224` … `ecdsa-plain-SHA3-512`); an
  OID outside that list is kept as OID and not given a name;
- **log time format** from the ASN.1 type of `logTime`: INTEGER → `unixTime`,
  UTCTime → `utcTime`/`utcTimeWithSeconds`, GeneralizedTime →
  `generalizedTime`/`generalizedTimeWithMilliseconds`.

**When it is read.** Right after a successful TSE activation TOR creates a TAR
export (still small at that point) under `%APPDATA%\TOR-POS-Pro\TseExports` and
takes the data from it. Every TSE export from the menu or the settings does the
same. A failure never undoes the activation — the next export fills the gap.

**Stored once.** `tse_master_data` (migration 13), one row per serial,
UPDATE/DELETE blocked by trigger. A later export that disagrees keeps the stored
record and is audited (`TSE_MASTER_DATA_CONFLICT`).

**Export.** `Stamm_TSE` now carries algorithm, time format, encoding, public
key and the certificate split over `TSE_ZERTIFIKAT_I/II`. A certificate longer
than 2,000 base64 characters blocks the export instead of being cut — the
unchanged official index.xml has no third certificate field. The hint names only
the TSEs without stored data and says to create a TSE export.

## 2. Im Haus / Außer Haus per sale

`Bonpos.INHAUS` ("Verzehr an Ort und Stelle") stayed empty: the choice decided
the VAT rate but was not stored with the sale.

- `sales.im_haus` (migration 13) is written from the checkout snapshot; a
  Storno and a Retoure take it over from their original.
- Sales from before R133 keep `NULL` — unknown, not silently "Außer Haus".
- The export writes `1`/`0` per position; the hint counts only the sales from
  before R133.

## Testing

`R133ReviewTests` (12 checks) builds a TR-03153 export with a real P-384 key,
a real X.509 certificate and DER log messages (system and transaction log):
serial = SHA-256 of the key, algorithm by OID, `unixTime`; PEM certificate and
GeneralizedTime with milliseconds; UTCTime and GeneralizedTime formats; a
certificate for another key is not attributed; an unknown OID keeps its OID;
stored once, disagreeing export audited, update refused; Im Haus read back
true/false/unknown; `INHAUS` 1/0/empty in `lines.csv`; `tse.csv` with algorithm,
format, encoding, public key and certificate; hints name only what is missing.

Two tests pinned the exact schema version (R132); they now require at least the
version that introduced their tables.

Safety suite **736/736** (724 before) under en-US, de-DE and tr-TR.

**To confirm with real hardware:** the reader follows TR-03153/TR-03151; the
first export of a real Swissbit TSE should be checked once (the hint
disappears when the data were found).
