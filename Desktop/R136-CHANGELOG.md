# R136 — TSE Transaction Starts When the Vorgang Begins

Fourth open fiscal decision from R130, decided by what the rules say, together
with the capture of Vorgangsbeginn (BON_START) and the TSE start time
(TSE_TA_START).

## The rules

- **§ 2 Satz 2 Nr. 2 KassenSichV / AEAO zu § 146a Nr. 2.2.2:** the recording
  system has to start logging the Vorgang in the TSE *"unmittelbar mit Beginn"*
  of the Vorgang. The Vorgangsbeginn is the time the TSE logs for that start.
- **AEAO Nr. 2.2.3.3:** the Vorgang is ended before its receipt is issued, and
  no Vorgang may stay open at a closing.
- **AEAO Nr. 1.11.1:** *Belegabbrüche* are among the Vorgänge to be secured.
- **DSFinV-K Anhang I:** Kassenbeleg-V1 — StartTransaction without data, no
  UpdateTransaction, FinishTransaction with the receipt data; an aborted
  Vorgang is finished as `AVBelegabbruch^0.00_0.00_0.00_0.00_0.00^`.
- **§ 6 Satz 1 Nr. 3 KassenSichV:** the receipt shows the time of Vorgangsbeginn
  and of Vorgangsbeendigung.

## What was wrong

TOR started and finished the TSE transaction together after payment. The TSE's
Vorgangsbeginn was therefore the end of the sale, BON_START and TSE_TA_START
stayed empty in the export, a cart emptied with C left no trace, and the printed
receipt showed neither Vorgangsbeginn nor Vorgangsende.

## The fix

- **Start.** The first position in an empty cart starts the Vorgang
  (`TseVorgangCartTracker`) and its TSE transaction (`TseVorgangService`), in the
  background — the cashier never waits for the TSE. Only a till that records
  fiscally (real booking or recorded training, R135) starts one.
- **End.**
  - Payment finishes the *same* transaction with the Kassenbeleg-V1 data.
  - Order acceptance finishes it with Bestellung-V1; the order stores its own
    Vorgangsbeginn.
  - Parking keeps the Vorgang and its transaction open; recalling the receipt
    continues it.
  - Emptying the cart (C), deleting a parked receipt, or a training order
    (still a simulation) ends it as **AVBelegabbruch**: the transaction is
    finished with the Anhang I abort data, and the positions are kept once in
    immutable tables (`aborted_vorgaenge`, `aborted_vorgang_items`).
- **TSE failure.** If the start failed (TSE not active or not reachable at the
  first position) and the TSE works again at payment, the transaction is started
  and finished then, so the Vorgang is still secured before its receipt;
  otherwise it is the documented outage it was before (R113/R129).
- **Restart.** Open transactions are kept in `tse_vorgaenge`. A recovered cart
  (crash recovery file or an unresolved payment) continues its Vorgang; an open
  Vorgang without a cart is ended as aborted on the next regular login.
- **Closing.** The Z-Bericht needs an empty cart. Transactions still open without
  a cart are aborted first, and the closing guard refuses while a Vorgang is
  open in the TSE (this also covers the R132 automatic closing from the
  settings). Aborted Vorgänge count as Vorgänge waiting for a closing.
- **Stored.** `sales.started_at` (Storno/Retoure: when recorded),
  `training_receipts.started_at`, `parked_receipts.vorgang_started_at`, and the
  TSE start log time with every TSE record (`start_log_time`), migration 16.
- **Export.** BON_START and TSE_TA_START are filled; aborted Vorgänge appear as
  `AVBelegabbruch` (`AB-n`) with their positions and TSE data, without effect on
  the closing totals. The BON_START hint now names only records from before R136.
- **Receipt.** Printed and digital receipt show *Vorgangsbeginn* (TSE start
  time; during an outage the till's own start) and *Vorgangsende*, also next to
  the TSE QR code, which does not carry them.
- **Update safety.** A payment journalled by an older build (snapshot without the
  new fields) still matches its cart at commit.

## Testing

`R136ReviewTests` (17 checks): migration; tracker start/abort/release/adopt with
copied positions and Im Haus VAT; start once without data; payment finishes the
same transaction number and stores both times; failed start caught up at
payment; AVBelegabbruch processData, one immutable record with positions;
parked Vorgang kept and continued, crash orphans aborted but a recovered cart
kept; deleted parked receipt aborted, closing guard refuses an open Vorgang;
order finished as Bestellung-V1 on its own transaction; cash movement keeps the
start time; export BON_START / TSE_TA_START, four AVBelegabbruch rows without
effect on the closing, no hint; master data guard counts aborted Vorgänge;
legacy journal snapshot normalises.

Safety suite **778/778** (761 before) under en-US, de-DE and tr-TR.

## Still open

- For a receipt that follows orders, DSFinV-K 2.7.2 allows printing the start of
  the first order as Vorgangsbeginn; TOR prints the start of the payment Vorgang.
- A parked KIOSK receipt keeps its Kassenbeleg-V1 transaction open until it is
  paid or deleted (no UpdateTransaction, Anhang I). Long-running Vorgänge are
  usually secured as Bestellung-V1; worth confirming with the tax advisor.
- Order changes after acceptance and order cancellations are not yet secured as
  their own Bestellung-V1 transactions (R131 hints BESTELLUNG/BESTELLSTORNO).
- Training in IMBISS order mode stays a simulation (R135).
- To be checked on real hardware: many open transactions on a Swissbit TSE, and
  a crash with an open transaction.
