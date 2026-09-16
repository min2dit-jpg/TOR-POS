# R36 – SumUp Solo connection diagnostics

Admin-only window in Settings / Devices. Supports reader listing, pairing and last-known status via official HTTPS Readers API.
No checkout, refund, transaction termination or deletion method is implemented. Payment buttons remain unchanged.
API key is window-scoped, masked, not persisted and excluded from error body/log output. Redirects disabled; timeout 15 seconds; response buffer capped at 1 MiB.
Account changes clear reader selection. UI prevents overlapping calls and cancels requests on close. Pairing is never retried automatically; timeout guidance directs user to list first.
53 checks passed including fake HTTP reader responses, offline status, invalid paths, pairing validation, error-body suppression and cancellation. Real network account / hardware validation remains outstanding.
Sources: https://developer.sumup.com/terminal-payments/cloud-api and https://developer.sumup.com/api/readers/create (accessed 2026-09-07).
