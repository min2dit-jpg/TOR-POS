# Swissbit SDK Integration Plan

## TOR POS standard
Swissbit Hardware TSE 2 · USB

## Why this architecture
Swissbit states that one unified SDK exposes an identical interface across
Hardware TSE generations TSE 1, TSE 1.1 and TSE 2. TOR therefore integrates
Swissbit once, behind a vendor adapter, instead of coupling the cash register
business logic to one stick serial number or one reseller.

## Acceptance rule
A device should be accepted only when all relevant checks pass:
1. Detected by the official Swissbit TSE SDK.
2. Recognized as a supported Swissbit hardware TSE.
3. For TOR's standard deployment: USB hardware profile.
4. TSE certificate is valid for productive use.
5. Device initialization/administrative state is valid.
6. Client assignment for this TOR register is valid.

Seller/reseller is not part of the compatibility decision.

## Code layers
TorPos.Core:
- ITseProvider
- TseDeviceInfo
- TseProbeResult
- TseActivationRequest / Result

TorPos.Infrastructure:
- SwissbitHardwareTseProvider
- ISwissbitSdkBridge

Next:
- Obtain official Swissbit TSE SDK + drivers + integration documentation.
- Implement a concrete ISwissbitSdkBridge using only documented SDK calls.
- Test against real Swissbit Hardware TSE 2 USB.
- Test start/finish transaction lifecycle.
- Test timeout, removal during sale, restart and recovery.
- Read serial/BSI/certificate data from device, never manual trust.
- Implement TSE export and DSFinV-K only after signed transaction data is verified.
- Keep Admin PIN / PUK out of persistent storage and logs.

## Performance rule
TSE calls must not freeze the Avalonia UI thread.
All vendor calls require timeout/cancellation and structured error states.
A TSE failure must be visible and handled according to the fiscal workflow,
not hidden or silently treated as a successful signature.


## Update v0.6.8

Die zuvor nur abstrakte Bridge wurde durch `SwissbitWormApiBridge` ergänzt.

Die Bridge lädt `WormAPI.dll` dynamisch und implementiert:
Probe, sichere Aktivierung, Start/Update/FinishTransaction und TAR-Export.

Die Swissbit SDK-Binaries selbst sind weiterhin nicht Bestandteil des TOR-Pakets.
Siehe `SWISSBIT-SDK-RUNTIME-DE.md`.
