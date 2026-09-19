> **HISTORISCHER SECURITY-REVIEW-STAND.** Die repo-weite aktuelle Security Policy steht in `../SECURITY.md`. Dieses Dokument bleibt als Nachweis früherer Hardening-Arbeiten erhalten.

# TOR POS v0.7.33 – Security Revision

Implemented after external review:

- User login lockout: 5 failed password/PIN attempts -> 5 minute lock. Persisted in `users.locked_until`.
- Successful login clears lock state.
- Default staff slots are created inactive and marked as requiring configuration. They cannot be activated without a new password and 4-digit PIN.
- Erweitert / Techniker is protected by a per-installation service password (set on first use, changeable afterwards from System), hashed with the same PBKDF2-SHA256/600k as user credentials — not a single fixed value shared across every installation. Five wrong attempts lock that area for 5 minutes in the current settings session. (R75.2: replaced the earlier fixed 4-digit service password, which was a single SHA-256 digest baked into every shipped copy of the application and recoverable offline in under a second.)
- Tax summary rounding reconciles the final VAT group to the exact discounted gross receipt total, eliminating 1-cent drift between receipt total and VAT group gross sum.
