# R143 — Positions Cancelled During Capture Belong to the Receipt

The next candidate from the handoff list, checked against the spec first.

## The rules

- **DSFinV-K 4.2.3:** *"Vorzunehmende Stornierungen auf Positionsebene finden im
  Bereich der Bonpos statt. Dabei ist entweder in der ursprünglichen Position
  P_STORNO auf ‚1' zu setzen … oder ein zusätzlicher Positionsdatensatz zu
  erstellen, bei dem MENGE mit negiertem Vorzeichen dargestellt wird … In diesem
  Fall darf P_STORNO nicht auf ‚1' gesetzt werden."* P_STORNO marks *"eine
  (sofort während der Erfassung) stornierte Position"*.
- **DSFinV-K 4.2.1:** a Sofortstorno of a whole receipt is only for systems
  without a TSE. With a TSE, TOR ends such a Vorgang as AVBelegabbruch (R136).
- **AEAO zu § 146a Nr. 1.11.1:** *"Sofort-Stornierung eines unmittelbar zuvor
  erfassten Vorgangs"* is among the Vorgänge needed to document complete
  recording.

## What was wrong

- **SOFORT STORNO** of a line was written to the action log only; the receipt and
  the DSFinV-K export showed just the positions that were paid.
- **-1** and **MENGE ×** could lower a quantity without any trace at all.
- **Found on the way (R137 gap):** a recalled order emptied position by position
  (SOFORT STORNO of the last line, or -1) was cancelled without its cancellation
  being secured as a Bestellung-V1 record — unlike C and deleting a parked
  receipt.

## The fix

- **Collected from the cart itself.** The Vorgang tracker (R136) sees every cart
  change. A removed line or a lowered quantity is collected as a cancelled
  position at the rate that applied then, whichever key was used. Adding is no
  cancellation.
- **Stored with the receipt.** The cancelled positions travel in the payment
  journal. They are stored once, immutable, with the sale (`sale_cancelled_items`)
  or the training sale (`training_cancelled_items`, migration 18), and kept in
  the crash recovery file.
- **Aborted Vorgang.** An aborted Vorgang keeps them too, with its positions.
- **Export.** Behind the receipt's own positions, each cancelled position follows
  as the captured position and its negated counterpart (DSFinV-K 4.2.3, second
  option; P_STORNO stays 0).
  - They add up to nothing.
  - The receipt total and the TSE data (Kassenbeleg-V1 totals) are unchanged.
  - They are kept out of the closing totals.
- **Orders.** A secured order emptied position by position is now cancelled as
  its own Bestellung-V1 record (R137), like with C.

## Testing

`R143ReviewTests` (7 checks):
- lowered quantity and removed line are collected at the applicable rate;
  adding is ignored; payment clears them;
- an abort carries them, and pairs add up to nothing;
- the payment journal round trip keeps them;
- stored immutable and read back with the sale;
- export shows the position plus +2/-2 and +1/-1 pairs, with receipt total and
  closing unchanged;
- the training receipt documents them the same way;
- an abort keeps them without changing its total.

Safety suite **827/827** (820 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Still open

- Positions cancelled before an order was accepted are not part of the order's
  first Bestellung record; the order records document every change after
  acceptance (R137).
