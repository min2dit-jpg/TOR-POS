# R29 Device Timeout Guard

Stability changes based on R28:

- ZVT already had real TCP connect and command timeouts. R29 keeps that implementation and clamps configuration values:
  - connect timeout: 1..30 seconds (default 5)
  - command timeout: 10..300 seconds (default 120)
- TSE automatic probe on the main cashier screen gets a 10-second UI watchdog.
- TSE probe in the first-run wizard gets the same 10-second UI watchdog.
- A non-responsive probe no longer leaves the operator waiting on that screen; TOR reports USB/SDK check guidance.

Deliberately NOT changed in R29:
- fiscal StartTransaction / FinishTransaction lifecycle
- TSE outage fiscal semantics
- ZVT payment protocol
- sale commit / receipt numbering
- DSFinV-K
- database schema

Reason: productive TSE transaction timeouts can create an ambiguous fiscal state if a native SDK call continues after a client-side timeout. That path will be implemented and acceptance-tested with real Swissbit hardware in Stage 7 rather than hidden behind a superficial timeout.
