# Security Policy

## Scope

This policy covers the TOR POS Desktop and Cloud source code in this repository,
including authentication, licensing, updates, backup/restore, payment-state
handling, TSE/fiscal integration and cloud interfaces.

Hardware, firmware, vendor SDKs, payment networks and operating-system
components are also governed by their manufacturers' or providers' security
processes.

## Reporting a vulnerability

Please **do not publish a suspected security vulnerability in a public issue,
discussion, screenshot or log attachment** before it has been reviewed.

Use one of these private channels:

1. GitHub private vulnerability reporting / Security Advisory for this
   repository, when enabled.
2. Otherwise, the private TOR support/contact channel agreed with the customer,
   installer or project owner.

A useful report should include:

- affected TOR POS revision and numeric version;
- affected Desktop or Cloud component;
- reproducible steps;
- expected and observed behaviour;
- security impact;
- whether real customer, payment or fiscal data was involved;
- logs or screenshots with passwords, tokens, licence material, PAN/PIN/CVV,
  TSE credentials and personal data removed.

## Sensitive information

Never include secrets in commits, issues, CI logs or diagnostic screenshots.
This includes, in particular:

- passwords, PINs and recovery codes;
- API keys, OAuth tokens and SMTP/app passwords;
- private signing keys or licence-signing material;
- payment-card PAN, PIN or CVV;
- TSE PIN/PUK/credential seed;
- production database exports containing customer or employee data.

If a secret is exposed, treat it as compromised and rotate/revoke it through
the responsible provider rather than merely deleting it from Git history.

## Supported release

The authoritative current release is defined in
`Desktop/src/TorPos.Core/ReleaseInfo.cs`. Repository CI verifies that the
manifest, application project, installer and top-level release markers match
that source.

Security fixes should normally target the current supported release. Older
R*-CHANGELOG and review files are historical records, not supported release
branches by themselves.

## Security-sensitive release checks

Before a customer release, the project should have green CI for the current
commit and should separately verify all security-sensitive integrations that
cannot be proven by unit/regression tests alone, including where applicable:

- Windows build and startup;
- authentication and permission boundaries;
- backup and restore;
- update signature verification;
- payment-terminal ambiguous/UNKNOWN recovery;
- Swissbit TSE start/update/finish, outage and restart recovery;
- receipt/QR output and fiscal export;
- cloud authentication and event delivery;
- dependency and third-party licence inventory.

A successful automated test run does not by itself constitute production
fiscal approval or hardware certification.

## Dependencies and third-party software

Direct and redistributable third-party components are documented in
`Desktop/THIRD-PARTY-NOTICES.md`.

For a release build, review the actual transitive dependency set and the
licence/security status of bundled runtime files. Vendor SDKs such as Swissbit
WORM API files must only be redistributed when the relevant vendor rights and
version requirements have been confirmed.

## Disclosure and remediation

Security reports should be triaged privately, reproduced on a non-production
environment where possible, and fixed with regression coverage. Public
disclosure should avoid operational secrets and should occur only after a fix
or mitigation is available to affected installations.
