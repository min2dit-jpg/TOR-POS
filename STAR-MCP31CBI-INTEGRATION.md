# Star mC-Print3 MCP31CBI Integration

## Hardware
User unit:
- Product family: Star mC-Print3
- Model: MCP31CBI
- Power: 24V / 2.7A

## Official Star facts used by TOR
- MCP31CBI is listed as a supported mC-Print3 model in current Star Windows Software.
- mC-Print3 uses StarPRNT emulation in Star's SDK documentation.
- Star provides a C# Desktop Windows SDK.
- mC-Print3 is a 3-inch / 576-dot / 203dpi class printer.

## TOR v0.4.2 implementation
Phase 1 uses the installed Star Windows printer driver:
- printer discovery through Windows installed printers
- automatic preference for names containing MCP31 / MCP30 / mC-Print3
- 80 mm receipt layout
- background print queue
- test receipt
- automatic receipt after sale
- Windows driver can handle USB/LAN/Bluetooth transport
- cutter and cash-drawer behavior can be configured in Star driver

## Why driver-first
This allows real receipt printing without bundling or redistributing third-party Star SDK binaries.
It also isolates the spooler from the cash-register UI.

## Phase 2 (R86: cut + drawer done, without the vendor SDK)
Instead of bundling the StarIO10 SDK, TOR sends the two commands that
matter most for daily operation - cut and cash-drawer kick - as raw
StarPRNT/ESC-POS bytes through the Windows print spooler's RAW datatype
(`RawPrinterIo.SendRaw`, `WritePrinter`), issued right after the GDI
receipt print completes. Both `GS V` (cut) and `ESC p` (drawer) are part
of the documented StarPRNT command set, not proprietary, so no SDK
redistribution question arises. See `R86-CHANGELOG.md`.

Still open, and genuinely blocked without either the official StarIO10
SDK or a direct USB/LAN port connection bypassing the spooler:
- detailed bidirectional printer status (paper/cover/cutter error state) -
  the Windows RAW spooler datatype does not reliably support synchronous
  `ReadPrinter` status read-back across USB/LAN/Bluetooth transports
- live paper-out monitoring (needs the same read-back)
- port discovery by StarIO

The byte values used for cut/drawer are correct per StarPRNT command
reference documentation but not yet confirmed against the physical
MCP31CBI - verify during the real hardware acceptance test.
