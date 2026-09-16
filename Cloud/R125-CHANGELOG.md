# R125 — Onboarding Customers and Running the Cloud in Production

Audit findings **C3** (no way to onboard a customer) and the rest of **C4**
(operations at prototype level). R120 covered the C4 items inside request
handling; this covers what sits around the server.

## C3 — customers were created with hand-written SQL

Business, owner, branch, register and device token existed only as rows someone
had to INSERT into the live SQLite file, including a SHA-256 of the device token
computed separately. One wrong character in that hash and the till could never
connect, with nothing to say why.

`tools/provision.js` does it on the server, with the server's own validation and
hashing:

```
node tools/provision.js create-customer --customer TOR-000123 --name "Imbiss Beispiel" --owner-email inhaber@example.de --owner-name "Vorname Nachname"
node tools/provision.js add-branch --customer TOR-000123 --name "Filiale Mitte" --city Berlin
node tools/provision.js add-register --branch <id> --device-code IMBISS-MITTE-01 --name "Kasse 1" --edition IMBISS
node tools/provision.js issue-device-token --device-code IMBISS-MITTE-01
```

plus `revoke-device-tokens`, `issue-device-token --revoke-existing`,
`reset-owner-password` and `list`.

Design decisions:

- **A command-line tool, not a web admin page.** Anyone able to run it can
  already read the database file, so it opens no new way in. An admin panel
  would be a new internet-facing target guarding the most privileged action in
  the system.
- **Secrets are shown once and stored only as hashes** - the owner's one-time
  password and every device token.
- **The password and token hashing moved to `credentials.js`**, required by both
  the server and the tool. Two hand-written copies of those functions is how the
  tool could write credentials the server silently refuses.
- **Device codes follow the till's own rule** (`TorCloudSyncService.SaveAsync`:
  letters, digits, `-`, `_`, max. 120), so a code the tool accepts can always be
  typed into Einstellungen.
- **A password reset ends that owner's sessions** - a reset is usually a
  response to a lost or leaked password.
- **Only `OWNER` is created.** The audit noted the `role` column is decorative:
  the portal refuses every other role. An employee account would therefore be an
  account that cannot log in. A real role model is a product decision and is left
  open, stated in the README rather than half-built.

## C4 — operations

- **Expired sessions and 2FA challenges are deleted hourly.** They used to be
  removed only when that exact row was used again, so the table only grew.
- **The database is backed up while running**, when `TOR_CLOUD_BACKUP_DIR` is
  set: a consistent copy via `VACUUM INTO` (copying the `.db` file would miss what
  is still in the WAL), every 24 h by default, the newest 14 kept. A server that
  restarts often does not replace a week of daily backups with copies from one
  afternoon - a backup is only written when the newest one is older than the
  interval. Off by default so a developer checkout does not fill its disk.
- **`PRAGMA busy_timeout=5000`**, because the provisioning tool now writes to the
  same file while the server runs; without it a colliding write fails
  immediately with "database is locked".
- **`deploy/`**: a commented production environment file, a hardened systemd
  unit, and a Caddy configuration for HTTPS with automatic certificates that
  *overwrites* `X-Forwarded-For` - with `TOR_CLOUD_TRUST_PROXY=true` the server
  uses the first address in that header, so a client-supplied value surviving
  the proxy would let anyone bypass the login rate limiter (R120).

## Not done here

- TLS itself is the reverse proxy's job; the server still listens on
  `127.0.0.1` only.
- The deploy templates are written against systemd and Caddy documentation and
  have **not** been run on a real server from this machine.
- **The Cloud folder is not in the GitHub repository** - only `Desktop` is.

## Testing

Three new tests in `tests/cloud.test.js`, run against the real server process:
expired rows disappear on their own while a valid session survives; a startup
backup is a readable copy and retention removes the oldest first; and a customer
created by the tool can log in, sees none of another tenant's data, its till
authenticates with the issued token, rotation and revocation lock out old tokens
(403), a password reset ends existing sessions, and invalid input is refused
without leaving half-created rows.

Cloud: **24/24**, run twice (21 before).
