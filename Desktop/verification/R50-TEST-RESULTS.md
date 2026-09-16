# TOR POS R50 – Verification Results

Generation environment: Linux container, 2026-09-08.

## Completed here

- Cloud Node.js test suite: **16/16 passed**, 0 failed.
- XML parse: **10 files OK**.
- JSON parse: **10 files OK**.
- C# structural delimiter/token check: **60 files OK**.
- R50 starter content/source guards: OK.
- IMBISS-only starter guard: OK.
- R50 DE/TR/EN pickup settings translations: OK.
- Training ORDER calls use the separated training flag: OK.
- SumUp source comparison against R49: **2/2 files byte-identical by SHA-256**.
- Package integrity is checked after ZIP creation.

## R50 safety-test source added

`Desktop/tests/TorPos.SafetyTests/R50ReviewTests.cs` covers the intended .NET regression cases:

- starter catalog does not run for KIOSK;
- IMBISS starter is idempotent;
- Döner/Burger/Fingerfood/Pizza/Getränke starter data exists;
- starter pizza has exactly two sizes (26 cm / 32 cm);
- starter articles receive automatic article numbers;
- IMBISS starter articles remain hidden in a KIOSK session;
- real and TRAINING pickup-number sequences are independent;
- TRAINING and real open-order lists are isolated.

## Not executable in this environment

The generation container has no `dotnet`, `msbuild`, `csc` or `mcs`. Therefore the Desktop .NET 10 / Avalonia project was **not compiled here** and the .NET safety-test executable could not be run here.

Run on the Windows test machine:

`Desktop\1-SETUP-ERSTELLEN.bat`

and then perform the IMBISS acceptance flow from `0-BURADAN-BASLA-R50.txt`.

## Production / fiscal status

R50 does not change the existing production lock. Real fiscal production remains disabled until the separate TSE/DSFinV-K/hardware acceptance work is completed.
