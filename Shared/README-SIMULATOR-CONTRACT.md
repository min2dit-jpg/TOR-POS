# TOR POS Simulator Contract

`Shared/simulator-contract.json` is the customer-facing UI contract shared with the
public TOR POS browser simulator.

## Purpose

The Windows application remains the product source of truth. This contract mirrors
only the parts that a website simulator needs to reproduce safely:

- public edition names (Einzelhandel / Gastronomie)
- top menu and cashier labels
- visual theme tokens
- demo-only category/product fixtures

Internal identifiers stay stable (`KIOSK` / `IMBISS`) and are intentionally
separate from public labels.

## Change rule

When a pull request changes a user-visible cashier structure, label, edition name,
or core theme token, update `Shared/simulator-contract.json` in the same pull
request.

`Desktop/tools/Verify-Simulator-Contract.ps1` is run in CI. It fails when the
contract no longer matches required TOR POS desktop labels/theme tokens or when a
demo catalog is structurally invalid.

The public website loads the contract from the repository's `main` branch. Once
a change is merged, the simulator can pick up the new contract on its next load.
The website keeps a local fallback so a temporary GitHub/network failure does not
break the simulator.

## Important

Demo catalog products and prices are illustrative fixtures, not live inventory,
not offers for sale, and not a commercial price list.
