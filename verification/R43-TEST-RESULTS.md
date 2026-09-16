# R43 Test Results

## Desktop static checks
- XAML/CSProj XML parsing: OK
- JSON parsing: OK
- 49 C# source files: structural bracket/string scan OK
- MainWindow XAML click handlers: 61/61 resolved
- Parked receipt cancellation SQL semantics: OPEN -> CANCELLED, no longer counted as open
- Variants editor is embedded in ARTIKEL; separate variant tab removed
- Receipt logo pipeline: settings key -> app-data asset -> ReceiptPrintJob -> printer drawing
- SumUp source files unchanged from R42: 2/2

See `Desktop/verification/r43-static-checks.txt`.

## Cloud regression
- Node test suite: **9/9 passed**
- The optional `portal-dom.cjs` check could not run in this container because its `jsdom` module is not installed. This is not a Cloud API test failure; the 9 API/data tests passed.

See `verification/r43-cloud-tests.log` and `verification/r43-portal-dom.log`.

## Not available in this environment
- .NET 10 SDK is not installed, so the Windows/Avalonia desktop application was not compiled here.
- Physical receipt printer/logo output was not tested here.
- Final acceptance must run `Desktop/1-SETUP-ERSTELLEN.bat` on the Windows TOR test PC and print a TESTBON with a real logo.
