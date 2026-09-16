# R30 – Crash Logging & Recovery Diagnostics

Controlled stability revision based on R29.

## Changes
- Preserves one log file per TOR POS process instead of overwriting the only log on every start.
- Keeps `TOR-POS-latest.log` as a predictable support path.
- Creates `TOR-POS.running` while the application is active.
- Normal Avalonia shutdown removes the marker.
- If the next start finds the marker, the previous session is recorded as an unclean/abnormal termination.
- Global AppDomain and unobserved Task exceptions continue to be logged; arbitrary fatal exceptions are not swallowed.
- Keeps the newest 30 per-session logs to prevent unlimited log growth.

## Log directory
`%LOCALAPPDATA%\TOR POS Pro\Logs`

## Scope
No changes to sale commit, TSE fiscal transaction flow, ZVT payment protocol,
receipt numbering, taxes, DSFinV-K or database schema.
