# R121 — Wrong Fiscal Data, and the Last Two Housekeeping Items

Final items from the audit priority list
(`TOR-POS-DERIN-INCELEME-2026-09-16.md`): findings **F3**, **F4**, **İ5** and
**İ4**.

## F3 — A Retoure was signed as an aborted receipt

```csharp
"RETURN" => "AVBelegabbruch"
```

`AVBelegabbruch` means the process was **broken off**: no receipt completed,
no money moved. A Teilretoure is the opposite — a completed transaction that
hands money back, modelled in DSFinV-K as a normal `Beleg` carrying negative
positions. Every partial return would have been misclassified in the signed
data.

Now `"RETURN" => "Beleg"`, keeping the `Referenz-Beleg-Nr` that links it to the
original receipt — that reference is what makes the negative booking
traceable, and it was always right. A full BON STORNO keeps `AVBelegstorno`,
which genuinely is "cancel a receipt that was issued".

`R82ReviewTests` asserted `AVBelegabbruch^` and had to be corrected — the
third test in this audit found asserting a bug as intended behaviour, after
R78 and R83 in R113.

## F4 — Vorgangsende was taken from the PC clock

```csharp
sale.TseLogTime ?? DateTimeOffset.Now   // ProcessEnd
```

Vorgangsende is the TSE's log time. Substituting the system clock presented a
fabricated value as TSE-derived — and made the receipt validator's own
`Vorgangsende` check impossible to fail, since the field was never null.

Now `sale.TseLogTime` is passed through unchanged, and the validator treats
Vorgangsende like the other TSE-generated fields: **mandatory when signed,
legitimately absent during a TSE outage**. The outage note is what explains
the absence. Moving that one check inside the existing `if (!job.TseOutage)`
block matters: left outside it, the fix would have blocked printing the very
receipt an outage legally requires.

`ProcessStart` still comes from `sale.CreatedAt` — the register's own record
of when the transaction began, which is genuine, not fabricated, and stays
mandatory unconditionally.

## İ5 — Version and document drift

- `manifest.json` claimed version `0.7.33.630`, platforms `Windows, Linux`,
  and `excluded_now: TSE, DSFinV-K, ZVT` — all three of which have substantial
  implementations. Identity fields corrected; the Linux claim removed (the
  `build-linux.sh` cross-compile produces a binary that cannot run a till,
  because printing, TSE and the terminal are Windows-specific); TSE/ZVT/
  DSFinV-K moved to a new `implemented_but_gated` list. A duplicate
  `revision` key deeper in the file silently overrode the header one — JSON
  keeps the last occurrence — so it was renamed to
  `historical_revision_at_this_point`. Nothing reads this file at runtime; it
  is documentation, and the frozen R57-R63 validation sections are now marked
  as such.
- `COMMERCIAL-RELEASE-CHECKLIST-DE.md` was headed `0.7.19`. Updated, with an
  explicit note that **no box is ticked on purpose** and that the remaining
  items hang on external prerequisites, not on software work.
- `Cloud/README.md` claimed "15/15 Tests" (now 21) and described the loopback
  update exemption without the R114/R120 changes.

## İ4 — Version control and CI

Git is **not installed on this machine**, so no repository was created — that
is the user's step. Everything else is prepared:

- `.gitignore` at the project root, keeping build output, customer data,
  `Cloud/data/`, `.env` and the private licence signing key out of history.
- `.github/workflows/ci.yml` — desktop suite + explicit Release build on
  Windows (R114's waiver is behind `#if DEBUG`, so a Debug-only build would
  not cover it) and the Cloud tests on Node 22.
- `10-ALLE-TESTS.bat` at the project root: both suites in one run, which is
  the gate to use until CI exists. It skips the Cloud half with a clear
  message if Node is absent rather than failing.
- `VERSIONSKONTROLLE-UND-CI.md` with the exact commands, including the
  instruction to read `git status` before the first commit — a committed
  secret stays in history.

## Testing

Desktop: **614/614**, run twice for determinism (606 before; +8 R121, +1 from
correcting R82's assertion). Cloud: **21/21**, unchanged.
