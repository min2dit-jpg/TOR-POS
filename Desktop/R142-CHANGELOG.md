# R142 — Every Action in Training Mode Secured and Marked AVTraining

The last item of the open list: training in IMBISS order mode (left open in R135).

## The rules

- **DSFinV-K Anhang B, AVTraining:** *"Der Vorgangstyp ‚AVTraining' kennzeichnet
  alle Vorgänge, die zu Übungszwecken durchgeführt werden … Es können sämtliche
  Vorgänge im Trainingsmodus durchgeführt werden. … Alle Handlungen des
  Trainingsmodus müssen dokumentiert, gesondert gekennzeichnet und mittels der
  DSFinV-K abgebildet werden. Sie haben jedoch keine Auswirkungen auf den
  Kassenabschluss."*
- **DSFinV-K 4.2.6:** training bookings *"sind … zu protokollieren und abzusichern
  gemäß KassenSichV"*.

## What was wrong

- A training order (IMBISS order mode) was not secured. Its acceptance, change
  and cancellation left nothing in the TSE. Only the Vorgang begun at the first
  position ended as an aborted Vorgang (R136).
- An aborted training Vorgang was finished in the TSE and exported as
  **AVBelegabbruch**, not marked as training.

## The fix

- **Training orders** are secured like real orders (R137/R138):
  - acceptance, change, cancellation and a change at payment each as
    Bestellung-V1;
  - on a till where training is recorded (R135);
  - in the export with BON_TYP **AVTraining** ("Bestellung (Training)",
    "Bestelländerung (Training)", …);
  - without effect on the closing.
- **An aborted training Vorgang** is finished in the TSE with
  `AVTraining^0.00_0.00_0.00_0.00_0.00^` and exported as AVTraining
  ("Abbruch (Training)").
- **Where it applies.** One rule decides where Vorgänge are secured
  (`SaleModePolicy.SecuresVorgaenge`): a regular user where the till books for
  real, a training user where training is recorded. A test till secures
  nothing. The payment-time order change (R137) is shared by the real and the
  training checkout.

## Testing

`R142ReviewTests` (4 checks):
- the policy for regular user, training user and test till;
- a training abort is finished as AVTraining;
- a training order is secured as Bestellung-V1;
- export: training order and training abort as AVTraining, the real order stays
  AVBestellung, closing totals empty.

Safety suite **820/820** (816 before) under en-US, de-DE and tr-TR. UI layout
check passed.

## Note

Training aborts recorded under R136 were signed with AVBelegabbruch data. They
exist only on a till that recorded training for real, which no shipped build
did.
