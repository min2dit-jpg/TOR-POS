# R128 — Owners Can Change Their Password

## The gap

R125 added `tools/provision.js`, which hands a new customer's owner a one-time
password. The portal had no way at all to change a password: that one-time
password — printed in a terminal and passed on by whoever set the customer up
— would have remained the owner's password for good.

## The fix

**`POST /api/password/change`** — current password, new password. Rules kept
simple enough to explain to a shop owner: at least 12 characters, not the
password it replaces, not the e-mail address. It shares the login rate limit,
so it cannot be used to guess the current password faster than the login form
could. On success every *other* session of that owner is ended — whoever knew
the old password must not stay logged in — while the session that made the
change continues.

**One-time passwords must be replaced first.** New column
`users.must_change_password`, set by `provision.js` on `create-customer` and
`reset-owner-password`. While it is set, `requireUser` answers everything except
`/api/me` and the password change with `428 PASSWORD_CHANGE_REQUIRED` —
checked *before* the 2FA requirement, so the owner first replaces the password
that was handed over and then enrols 2FA with their own. Existing accounts
(including the demo owner) default to 0 and are unaffected. `provision.js`
refuses to run against a database from an older Cloud version that does not
have the column yet, with a message saying to start the current server once.

**Portal.** Sicherheit has a *Passwort ändern* panel. An owner on a one-time
password is taken there straight after login, sees why, and cannot start 2FA
enrolment until the password is replaced; afterwards the portal continues
normally. The periodic refresh only switches to that view when not already on
it, so it does not jump the page while the owner is typing.

## Also

- `/api/health` and the start-up line report `0.12.0-R128`
  (`CLOUD_VERSION`, one constant since R127).
- Script cache-busters in `portal.html`/`login.html` bumped so browsers load
  the new `app.js`.

## Testing

- `R128` in `tests/cloud.test.js`: `/api/me` reports the obligation, portal data
  and 2FA enrolment are refused with `PASSWORD_CHANGE_REQUIRED`; wrong current
  password 401; too short, unchanged and e-mail-as-password 400; no session
  401; success ends the other session but keeps the current one; the one-time
  password stops working and the new one works; a password reset brings the
  obligation back; the demo owner is unaffected.
- `R125` provisioning test updated for the new first step — it previously
  expected portal data directly after logging in with the one-time password,
  which is exactly what R128 closes.
- New optional DOM test `tests/portal-password-dom.cjs` (real `portal.html` +
  `app.js` against a real server, needs jsdom): redirect to Sicherheit, no data
  loaded, 2FA button hidden, mismatch and length errors shown, success clears
  the fields and loads the business data. `npm run test:dom` runs it together
  with the existing portal DOM test; both pass.

Cloud: **25/25** (24 before).
