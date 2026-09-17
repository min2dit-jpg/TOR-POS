# R144 — One Serial Number of the Till; Notification Data § 146a Abs. 4 AO

Found while checking the notification rules (AEAO zu § 146a Nr. 1.16) after R143.

## The rules

- **AEAO zu § 146a Nr. 1.16.2.5:** the notification contains *"die Seriennummer
  des elektronischen Aufzeichnungssystems"*. It identifies each system of a
  manufacturer uniquely (Nr. 2.2.3.1).
- **§ 2 Satz 2 Nr. 8 KassenSichV / AEAO Nr. 2.2.3.1:** this serial is logged by
  the TSE with every transaction (client id).
- **AEAO Nr. 2.4.4 Nr. 6:** the receipt carries *"die nach § 2 Satz 2 Nr. 8
  KassenSichV protokollierte Seriennummer"*.
- **DSFinV-K KASSE_SERIENNR:** *"Falls vorhanden, wird hier die
  Identifikationsnummer erwartet, die … gemäß § 146a Abs. 4 AO zu melden ist"*;
  no "/" or "_". The QR code (Anhang I) carries *"Seriennummer (Client-Id) der
  Kasse"*.
- **AEAO Nr. 1.16.2.1 – 1.16.2.7:** the notification data are tax number,
  TSE (BSI certification id and TSE serial number), type of system, number of
  systems, serial number, date of acquisition and date of decommissioning.

## What was wrong

- **Three numbers for one till.**
  - The DSFinV-K export carried TOR's internal identity (39 characters).
  - The TSE logged a client id typed in by hand, limited to 30 characters, so it
    could never be the same number.
  - Since R140 the receipt showed that client id.
- **Notification data.** TOR prepared none of the data for Mein ELSTER; the
  settings held only a status and a date.

## The fix

- **Kassen-Seriennummer** (`KassenSeriennummer`): the identity cut to 30
  characters ("TORPOS-" and 23 hex digits, unique per installation).
  - It is used as DSFinV-K KASSE_SERIENNR, on the printed and the digital receipt
    (also in test mode and during an outage), in the programming protocol and in
    the notification data.
  - The full identity stays the DSFinV-K Z_KASSE_ID.
- **TSE client id.**
  - The settings propose the serial number for a till without a client id.
  - A new mandatory readiness item "TSE-Client-ID = Kassen-Seriennummer" blocks
    production release while they differ.
  - Nothing is changed on an already activated TSE, and signing is not affected.
- **Notification data** (menu "Kassenmeldung § 146a Abs. 4 AO", admin, also as
  PDF): tax number, business premises, type of system, serial number,
  manufacturer/software, date of acquisition and decommissioning (new settings
  fields), BSI certification id and TSE serial number. Missing entries and a
  client id that differs from the serial number are named. TOR transmits
  nothing itself.

## Testing

`R144ReviewTests` (5 checks):
- serial number rule (30 characters, valid characters);
- client id comparison;
- DSFinV-K KASSE_SERIENNR is the serial number, the identity stays Z_KASSE_ID;
- notification data with missing date and client id mismatch named;
- matching client id with acquisition date.

Safety suite **832/832** (827 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Note for the business

On a till whose TSE was activated with another client id, the client id has to
be registered anew as the Kassen-Seriennummer (TSE administration) before
production release. The notification to the tax office uses the same number.
