# R113 — Silent Unsigned Sales and Dead Outage Detection

The two **critical** findings from this session's full end-to-end audit of
the project (`TOR-POS-DERIN-INCELEME-2026-09-16.md`), fixed immediately at
the user's request. Both are fiscal-integrity bugs: not missing features,
but code that ran and did the wrong thing quietly.

## Fix 1 (F1) — A sale could be completed unsigned, with no trace

`SaleFiscalSigningService.SignAsync` and `OrderFiscalSigningService.SignAsync`
both started with two silent `return`s:

```csharp
if (settings["tse.status"] != "AKTIV") return;
if (settings["tse.client_id"] == "")    return;
```

Consequences, all three at once:

1. **No outage record** — `tse_outage_log` stayed empty.
2. **No audit entry** — nothing in `audit_log` either.
3. **No receipt marking** — because `sale.TseOutage` stayed `false`, and the
   printed receipt's mandatory *"TSE-AUSFALL / Vorgang ohne TSE-Signatur"*
   block is driven purely by that flag, an unsigned sale printed a receipt
   **indistinguishable from a properly signed one**.

So if `tse.status` ever drifted away from `AKTIV` on a productive till, the
register would keep selling and leave no evidence anywhere that its sales
were unsigned. A productive sale can only reach this method when
`FiscalComplianceService` already considered the TSE active, so finding it
inactive here is by definition an outage.

Both services now route these two states through a new
`TseFailSafeService.ReportUnavailableAsync(reason, actor)`, which opens the
outage and writes a `TSE_UNAVAILABLE` audit entry, and then apply
`SaleTseResult.Outage(reason)` so the sale/order is marked exactly like any
other TSE failure — including the receipt note. No signature is ever
fabricated; that part was always correct.

## Fix 2 (F2) — Outage detection existed but was never wired up

Two separate dead paths:

- **`TseFailSafeService.ProbeAsync` had no caller anywhere.** Every probe
  site (`MainWindow`, `DiagnosticsWindow`, `SettingsWindow`,
  `FirstRunSetupWindow`, `BusinessManagementService`) called the raw
  `ITseProvider` instead, so a TSE that was missing or unreachable during
  business hours recorded **nothing**. `MainWindow.AutoProbeTseAsync` — the
  operational probe that runs while the till is in use — now goes through
  the fail-safe wrapper, so a failed probe opens a documented outage and a
  later successful one closes it automatically.
- **`ITseOutageRepository.GetOpenAsync` had no production caller**, so an
  open outage was never visible anywhere in the running application. Added
  `TseFailSafeService.GetOpenOutageAsync` plus a red **TSE-AUSFALL** badge in
  the main window header (next to the existing stock/licence badges),
  showing since when the outage has been open with the reason as its
  tooltip. It refreshes with the fiscal status and immediately after every
  sale signing, since that is where an outage is now most likely to be
  opened.

`TseFailSafeService` is now injected into `MainWindow` through the standard
DI path (`App.axaml.cs` registration + `AppWindowFactory` resolution), the
same pattern R106 used for `ICardRefundLockRepository`.

## Two existing tests encoded the bug as intended behaviour

Worth recording, because it explains how F1 survived from R78 to R112:

- `R78ReviewTests` asserted `!unsignedSale.TseOutage` for the inactive-TSE
  and missing-client-ID paths ("signing without an active TSE leaves the
  sale untouched").
- `R83ReviewTests` asserted the same for the order path.

Both were written to check that TOR **never fabricates a signature** — which
is right, and still holds. But by also requiring `TseOutage == false` they
locked in the silence. Both are corrected here to assert the fixed
behaviour: no fabricated signature **and** a documented outage. A green
suite is only as good as what its assertions actually demand.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R113ReviewTests.cs` — 9 checks:
inactive TSE (flag, persisted `sale_tse_signatures.outage=1`, durable
`tse_outage_log` row, and a reason that states *why*), missing client ID,
a negative control proving a healthy TSE still signs normally and leaves no
outage, a failed probe opening an outage, `GetOpenOutageAsync` surfacing it
for the badge, and a later successful probe closing it again.

These paths are fully testable end-to-end: signing runs strictly after the
durable commit and never calls `FiscalRelease.RequireProduction()`, so
unlike `RecordStornoAsync`/`RecordReturnAsync` the success path is reachable
in the harness.

Full suite: **572/572 checks passed**, run twice for determinism (558
before; +9 new R113 checks, +5 added while correcting the R78/R83
assertions).
