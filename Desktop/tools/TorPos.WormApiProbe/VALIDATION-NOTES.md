# Swissbit WORM API validation notes

Stand: R149 source baseline + uploaded `libWormAPI.so` 5.8.1-offline.

## What was verified

The uploaded library is an x86-64 ELF shared object and exports the complete set
of symbols currently required by TOR's `SwissbitWormApiBridge`.

The public `python-tse` wrapper by Bernd Wurst was used as an independent ABI
cross-check. The following TOR delegate signatures match the wrapper's ctypes
signatures:

- `worm_init(context**, mountPoint)`
- `worm_cleanup(context)`
- `worm_getVersion()`
- `worm_tse_runSelfTest(context, clientId)`
- `worm_tse_setup(context, seed, seedLen, puk, pukLen, adminPin, adminPinLen, timeAdminPin, timeAdminPinLen, clientId)`
- `worm_tse_registerClient(context, clientId)`
- `worm_tse_updateTime(context, uint64 unixTime)`
- `worm_user_login(context, userId, pin, pinLen, remainingRetries*)`
- `worm_user_logout(context, userId)`
- `worm_transaction_response_new(context)`
- `worm_transaction_response_free(response)`
- `worm_transaction_response_transactionNumber(response) -> uint64`
- `worm_transaction_response_signatureCounter(response) -> uint64`
- `worm_transaction_response_logTime(response) -> uint64`
- `worm_transaction_response_serialNumber(response, data**, len*)`
- `worm_transaction_response_signature(response, data**, len*)`
- `worm_transaction_start(context, clientId, processData, processDataLen, processType, response)`
- `worm_transaction_update(context, clientId, transactionNo, processData, processDataLen, processType, response)`
- `worm_transaction_finish(context, clientId, transactionNo, processData, processDataLen, processType, response)`

TOR uses 64-bit unsigned values for transaction number, signature counter,
log time and process-data lengths, which matches the reference wrapper's
`worm_uint = c_uint64`.

The uploaded 5.8.1-offline library also exposes useful additional symbols:

- `worm_signatureAlgorithm`
- `worm_logTimeFormat`
- `worm_info_tsePublicKey`
- `worm_transaction_listStartedTransactions`
- `worm_transaction_lastResponse`
- `worm_getLogMessageCertificate`

These are not required by TOR R149 today.

## Why the optional exports are interesting

TOR currently obtains signature algorithm, log-time format and public key from
the TSE TAR export and stores them as immutable TSE master data. That is a sound
independent source for receipt QR generation.

For acceptance diagnostics, however, the SDK-level getters above could be used
to cross-check the TAR-derived values without replacing the existing source of
truth.

`worm_transaction_listStartedTransactions` and
`worm_transaction_lastResponse` are especially useful for crash/restart
acceptance tests because they can show what the TSE itself still considers open
or last completed.

They should only be added to production code after the exact official Windows
SDK header/version has been checked. Public GitHub code is reference evidence,
not a substitute for the vendor header.

## Important non-findings

This validation does **not** prove:

- that the Windows `WormAPI.dll` has the same ABI as the uploaded Linux SO,
- that physical Hardware TSE 2 works with TOR,
- that activation credentials are correct,
- that Start/Update/Finish survives unplug/crash/power-loss scenarios,
- that the generated TAR passes a full fiscal acceptance test.

Those remain real-hardware acceptance items.

## Safe next hardware test order

1. Official Windows x64 `WormAPI.dll`: run `TorPos.WormApiProbe`.
2. Read SDK version and device info only.
3. Read TSE serial/form factor/certificate expiry.
4. On a dedicated test TSE, run self-test and client registration.
5. Start -> Update -> Finish one test transaction.
6. Compare transaction number, signature counter, log time, serial and signature
   with the TSE's TAR export.
7. Test restart with an open transaction and compare TOR recovery state with
   `listStartedTransactions` / `lastResponse` if the official SDK supports them.
8. Only then run DSFinV-K/TAR end-to-end acceptance.
