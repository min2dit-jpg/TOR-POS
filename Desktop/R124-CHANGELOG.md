# R124 — The Pickup Number No Longer Resets at Midnight

Audit finding İ1, the half R117 did not cover.

## The defect

Every Abholnummer counter was keyed by the Berlin calendar date
(`pickup.20260916`). An imbiss open past midnight watched its queue jump from
087 back to 001 in the middle of service, while orders 080–087 were still
waiting at the counter.

## The fix

The counter now runs per **service period**: from one Tagesabschluss to the
next — the same boundary R117 settled on for the Z-Bericht and the Kassensturz
(the most recent row in `daily_closings`, written by the Z-Abschluss). The key is
`pickup.period.<closing id>`, with separate `training.` and `test.` scopes as
before.

A till whose operator skips the Tagesabschluss would otherwise count up forever,
so the number wraps from 999 to 001 — the three digits every screen and ticket
already prints (`{PickupNumber:000}`).

Numbers already handed out are never renumbered. On the first start after the
upgrade the real counter begins again at 001, because the old date-keyed rows
are simply no longer read; install outside service hours.

The three allocation sites (direct sale, accepted order, simulation) each carried
their own copy of the counter SQL. They now share `PickupSequence.NextAsync`, so
the rule cannot drift between them — the same lesson R106 taught about the VAT
formula. `PickupSequence.BusinessDay` is gone; nothing needs a calendar day for
this any more.

## A fifth test that held the bug in place

`R48ReviewTests` asserted **"Counter resets on Berlin midnight"** — the exact
behaviour the audit reported. Corrected to assert that the counter resets at the
next Tagesabschluss instead.

The same file had a second, quieter problem that this change would have created:
it checked that simulations never touch the real counter with
`key LIKE 'pickup.20%'`. With the new key shape that query matches nothing, so
the check would have kept passing whatever the code did. It now looks for
`pickup.period.%`.

## Deliberately not changed: promotion end dates

The audit put "campaign validity ends at midnight" in the same family. It is not
the same kind of problem. `PROMOTION-RULES-R71.txt`, rule 4: a campaign is valid
*including* its start and end dates. A price advertised as valid until 16.09.
must not still apply on the 17th because the day has not been closed yet — that
would be a pricing decision with legal weight, not a bug fix. If a
"Tagesangebot until end of service" is ever wanted, it should be an explicit new
campaign option, not a change to how dates are read.

## Testing

`R124ReviewTests` (8 checks) through the real `ParkedReceiptRepository`: numbers
count up within a period, no counter is keyed by a date, the first order after a
Tagesabschluss is 001, an order accepted before the closing keeps its number,
training stays separate, 999 wraps to 001, simulation never consumes the real
queue.

Desktop: **663/663**, run twice (655 before).
