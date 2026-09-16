# R122 — The Audit's Medium and Low Findings

With R113–R121 the critical and high findings were closed. This release takes
the rest of the list that is fixable in code: **G4**, **G5**, **F5**, **F6**,
**İ6** and the two low security notes.

## G4 — a factory admin had full power

`must_change_password` was enforced for staff inside `AuthenticationService`,
but for the admin only by the UI: `App.axaml.cs` opens
`RequiredAdminCredentialsWindow` after login and refuses to go further. That
works — but it is one check in one window, and every other entry point had to
remember the same rule by hand (`CheckoutReviewWindow` and
`MainWindow.Safety` each do).

The rule now lives in the session object itself:

```csharp
public bool Can(UserPermissions permission) =>
    !MustChangePassword && (IsAdmin || (Permissions & permission) == permission);
```

A caller that forgets now gets a **powerless** session instead of a full admin
one. Nothing legitimate is blocked: the credential-change dialog verifies the
current password through `ChangeAdminCredentialsAsync`, not through `Can()`,
and the flag is cleared on the session the moment the change succeeds. The
login itself still succeeds — it has to, or the dialog would be unreachable —
but it now leaves an `ADMIN_LOGIN_CREDENTIALS_UNCONFIGURED` entry in the audit
log, which is what was really missing: a till running on admin/admin left no
trace at all.

## G5 — the backup recovery code was hashed once, unsalted

`DeriveKek` was a single `SHA256.HashData` over the typed code. The code is 128
bits of randomness, so this was never a dictionary-attack problem — but two
installations with the same code shared key material, and precomputation was
reusable.

Now salted PBKDF2-SHA256 (600 000 iterations), with the salt and iteration
count carried **inside the container**, because a restore on a replacement
machine has the file and the code and nothing else.

Container format 1 stays readable forever — customer backups must keep
restoring, and there is a test that builds a format-1 file by hand and restores
it. Which format is *written* depends only on whether the installation has a
stored salt, and it only gets one by generating a new recovery code: the old
KEK cannot be converted, because deriving it needs the code, which TOR POS
deliberately never stored. `GetStatusAsync` therefore reports
`UsesLegacyKeyDerivation`, and the Backup settings page tells the operator that
one WIEDERHERSTELLUNGSCODE NEU ERSTELLEN is all it takes.

Same file, the low finding: `DecryptAsync` read `reader.ReadBytes(dpapiLen)`
with `dpapiLen` straight from the file. A corrupt or crafted `.tpe` could
declare two gigabytes and the restore attempt died with an
`OutOfMemoryException` instead of "damaged backup". Every read is now bounded
and every declared length sanity-checked.

## Training code

`"0000"` was a constant in `LoginWindow`, and the login screen printed it next
to its own input box. Training mode books nothing, signs nothing and cannot use
the card terminal, so this is about who can walk up and start a session — not a
fiscal matter. It is now `training.access_code` in app_settings, editable under
Einstellungen → Personal, factory value still `0000` so no installation is
locked out by the upgrade. The login screen names the code only while it is
still the factory one.

## F5 — the digital receipt claimed compliance it could not show

The printed receipt has refused to print without the mandatory §6 KassenSichV
fields since R63. The digital receipt — which may be the customer's **only**
copy of the Beleg — printed "Elektronischer Beleg gem. §6 KassenSichV"
unconditionally and then skipped whichever fields happened to be blank. A
receipt missing its Transaktionsnummer looked exactly like a complete one.

The field list moved to `TorPos.Core.FiscalReceiptFields` and both now ask it —
rather than giving the digital receipt a second hand-written copy, the mistake
R106 had to undo for the VAT formula. They act on the answer differently, on
purpose: the printer refuses to produce the document, the web page still
renders (the customer already scanned the code) but says plainly which entries
are missing and tells them to ask for a paper receipt. A genuine TSE outage is
still explained by the outage note, not reported as missing fields.

This also uncovered a fourth old test asserting the defect: `R103ReviewTests`
built a "real, non-test sale" with no company address, no eAS serial and no TSE
log time, and asserted the §6 line appeared anyway.

## F6 — the comment said the opposite of the code

Migration 8 stated that a failed signing attempt "simply never gets a row" in
`sale_tse_signatures`. It does — with `outage=1`, and that row is precisely
what puts the legally required TSE-AUSFALL note on the receipt. R113 exists
because the outage path used to write nothing.

Corrected, and the real consequence spelled out: `sale_id` is the primary key
and UPDATE/DELETE are blocked, so the record is **final** — one per sale,
signature or outage, and a sale recorded as an outage can never be signed
afterwards.

**That is a fiscal decision, not a refactor, so it is not being changed here.**
It is now an explicit open item in `ROADMAP.md` to confirm against real
TSE/DSFinV-K validation. What did change is the failure mode: a second result
for the same sale is refused with a stated rule instead of a raw `SQLite Error
19: UNIQUE constraint failed`, which reads like a database fault rather than
the rule it actually is. Nothing in this build calls it twice.

## İ6 — dead code

- **`GetDailySinceLastZAsync` removed** (interface and implementation). It had
  no caller anywhere and it carried the exact bug R117 had to fix in
  `GetOpenPeriodAsync`: it started the period at `max(midnight, last closing)`,
  so a shift running past midnight would have lost the sales between the last
  closing and 00:00. A dead method with a known-wrong period rule sitting in an
  interface is an invitation.
- **`qrSvg` removed** from `Cloud/qr-v6.js` and its import from `server.js`.
  The pairing endpoint returns `qr_matrix` and the browser draws it; nothing
  renders SVG server-side.
- **`tests/portal-dom.cjs` kept**, not deleted — it is a real, working portal
  test that needs `jsdom`, the one thing this otherwise dependency-free project
  will not install by default. It was "dead" only because nothing named it, so
  it is now `npm run test:dom` with the install command written down next to
  it.
- `TseFailSafeService.ProbeAsync` was on the audit's dead list; R113 already
  gave it its caller.
- `Cloud/torpos-integration/` keeps its one-line note on purpose, so older
  documentation pointing there does not dead-end.

## Testing

Desktop: **646/646**, run twice for determinism (614 before). Cloud:
**21/21**, unchanged.
