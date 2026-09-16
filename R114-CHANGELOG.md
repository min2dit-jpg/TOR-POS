# R114 — Update Signature Enforcement No Longer Waived on Loopback

Closes finding **G1** (High) from this session's full project audit
(`TOR-POS-DERIN-INCELEME-2026-09-16.md`).

## The problem

`TorUpdateService` wrapped **both** of its trust gates in `if (!IsLoopback(root))`:

- the pinned-certificate check on the manifest (`ValidateManifest`), and
- the pinned-certificate check **plus** `VerifyAuthenticodeAsync` on the
  downloaded installer.

So whenever the update server URL pointed at `127.0.0.1`, `localhost` or
`::1`, TOR staged the installer with **no signature verification at all**.
The only remaining check was SHA-256 against the manifest — but that manifest
comes from the same server as the file, so it verifies nothing an attacker
controlling that server couldn't trivially satisfy.

Why that mattered in practice:

- The update server URL is read from `app_settings` inside the SQLite
  database under `%APPDATA%`, which the **ordinary cashier account can
  write** — no admin rights needed.
- The staged installer is later started with `-Verb RunAs`, i.e. **elevated**.
- Plain HTTP is explicitly permitted for loopback, so no certificate is
  needed either.

The uncomfortable part is that the non-loopback path was already exemplary:
HTTPS mandatory, redirects disabled, pinned thumbprint required, Authenticode
verified, and **fail-closed** when `UpdateSignerThumbprint` is empty (as it is
today). The loopback exception quietly removed all of it.

## The fix

The decision moved into a pure policy function in
`TorPos.Core/ReleaseInfo.cs`:

```csharp
public static bool RequiresSignatureEnforcement(Uri server, bool developmentBuild) =>
    !developmentBuild || !IsLoopback(server);
```

`TorUpdateService` now calls it with `DevelopmentBuild`, which is `true` only
under `#if DEBUG`. A customer Release build therefore enforces the pinned
certificate and Authenticode **everywhere, including loopback**; with the
thumbprint still empty, that path correctly fails closed. Developers keep the
convenience in their own Debug builds.

Keeping the rule as a pure function in Core is deliberate: the safety suite
builds in Debug, so a test that merely exercised `TorUpdateService` could
never observe Release behaviour. The policy can be asserted directly instead,
for both build kinds, independently of how the tests themselves are compiled.

The HTTP-for-loopback allowance in `ValidateServerUrl` is intentionally kept:
with signatures now always enforced in a Release build, the transport is no
longer what grants code execution — the pinned Authenticode signature is.

## Testing

New `Desktop/tests/TorPos.SafetyTests/R114ReviewTests.cs` — 7 checks: a
production build requires enforcement for `127.0.0.1`, `localhost` and a
remote server; a development build waives it only for loopback and never for
a remote server; and a look-alike hostname (`localhost.attacker.example`,
`127.0.0.1.attacker.example`) is not treated as loopback, so it cannot borrow
the development exception even in a Debug build.

Full suite: **579/579 checks passed**, run twice for determinism (572
before). `dotnet build -c Release` verified clean, since this change is one of
the few whose behaviour differs between configurations.

## Still open on the update chain (not this change)

Finding **C1** from the same audit: the Cloud server performs **no**
server-side verification when serving an update — it never re-hashes the file
it streams, and the only Authenticode check in the whole publishing chain
lives in `PUBLISH-UPDATE.ps1`, which no test ever executes. Remote updates are
inert today (empty thumbprint on the client, `enabled:false` in the shipped
manifest), but both ends need closing before that is ever switched on.
