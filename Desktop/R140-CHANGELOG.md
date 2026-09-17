# R140 — Receipt QR Code per DSFinV-K, TSE Data Printed Unchanged

The second item of the open list (it was found while reading the DSFinV-K for
R139).

## The rules

- **AEAO zu § 146a Nr. 2.4.1:** *"Alle Angaben müssen für jedermann ohne
  maschinelle Unterstützung lesbar oder aus einem QR-Code auslesbar … sein. Der
  QR-Code hat der DSFinV-K zu entsprechen (vgl. DSFinV-K – Anhang I Tz. 2.)."*
- **AEAO Nr. 2.4.4:** the TSE data are printed *"in dem Format …, in dem sie von
  der TSE an das elektronische Aufzeichnungssystem zurückgeliefert wurden.
  Nachträgliches Runden, Abschneiden oder Verändern dieser Daten ist unzulässig.
  Es wird nicht beanstandet, wenn ein als UnixTime gelieferter Zeitstempel als
  Coordinated Universal Time (UTC) ohne zusätzliche Zeitzone ausgegeben wird."*
  *"Sofern ein QR-Code gemäß Anhang I der DSFinV-K anstelle der … lesbaren Daten
  verwendet wird, gelten die vorgenannten Anforderungen als erfüllt."* Nr. 6: the
  serial of the recording system that was logged under § 2 Satz 2 Nr. 8
  KassenSichV.
- **DSFinV-K Anhang I Tz. 2:**
  `V0;<kassen-seriennummer>;<processType>;<processData>;<transaktions-nummer>;<signatur-zaehler>;<start-zeit>;<log-time>;<sig-alg>;<log-time-format>;<signatur>;<public-key>`.
  - Times use the format `YYYY-MM-DDThh:mm:ss.fffZ`.
  - The public key has to be included.
  - At least 3 cm edge length is recommended.
  - With the Tz. 2.7 facilitation, the start of the first order stays readable.

## What was wrong

- **QR code format.** The optional QR code (R81, setting "TSE-Angaben als
  QR-Code") replaced the TSE text lines with a format of TOR's own:
  `eAS:…|TSE:…|TXN:…|CTR:…|CHK:…`. It had no processData, no times, no algorithm
  and no public key, and no verification tool can read it. With the setting on,
  the receipt therefore lacked the TSE data the AEAO requires. R81's test had
  pinned that format (the eighth bug-pinning test).
- **Times.** Vorgangsbeginn and Vorgangsende (R136) were printed converted to
  local time and cut to seconds.
- **Serial.** The receipt showed TOR's system identity as "eAS". The TSE logs
  the client id, and the two cannot be equal: the client id allows at most
  30 characters.

## The fix

- **QR code** (`TseQrCodePayload`): exactly the Anhang I format.
  - Contents: client id, Kassenbeleg-V1, processData as signed, transaction
    number, signature counter, TSE start and end time, and the algorithm, log
    time format and public key from the TSE's own export (R133).
  - It is built only when every field is present. During a TSE outage, before
    the TSE export has been read, or in test mode, the printer prints the TSE
    data as text.
  - The code is printed larger (about 3.8 cm).
  - Bestellbeginn stays readable next to it.
- **Times:** TSE times on the printed and the digital receipt are UTC with
  milliseconds (`TseReceiptTime`), the format the export and the QR code use.
  Only the till's own start during an outage is local time.
- **Serial:** the receipt shows the client id the TSE logged for that sale;
  without a TSE result it shows the system identity as before.

## Testing

`R140ReviewTests` (4 checks):
- the official Anhang I example is reproduced character for character;
- no QR code without public key / start time / algorithm, or during an outage
  or in test mode;
- a TSE time is printed in UTC with milliseconds;
- the digital receipt shows the client id and the unchanged times.

R81's check now asserts the Anhang I payload.

Safety suite **813/813** (809 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Still open

- The QR code needs the TSE master data from a real TSE export (R133). A first
  real receipt should be checked with a verification app.
- Next items: training in IMBISS order mode, Z-Bericht payment split of
  Storno/Retoure.
