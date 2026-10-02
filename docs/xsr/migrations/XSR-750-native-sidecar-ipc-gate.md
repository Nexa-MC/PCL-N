# XSR-750 Native Sidecar IPC acceptance gate

The listener-ownership regression originally awaited a pipe write before starting the matching
read. Windows named pipes with zero/default buffer capacity can wait for a reader even for one
byte, so the fixture timed out without exercising transferred ownership. The existing framed
IPC regression already uses concurrent receive/send, matching the production receive loop.

Ownership validation now begins the receive before each send and checks both directions after
the listener is disposed. The listener is also disposed on fixture failure. The deadline stays
10 seconds; no IPC test is skipped and no production buffering or protocol behavior changes.

A dedicated Windows/macOS/Linux NativeAOT Sidecar job runs the entire protocol/transport suite
on real OS transports. It supplements the main Linux managed gate and does not constitute
physical Minecraft or native-window acceptance. Host setup, trust and device limits remain
recorded separately in the Alpha 6 ledger.

Local Windows NativeAOT publication was quarantined during linking, leaving no native file
to copy or inspect. `local-native-antivirus.md` records the evidence and limits. The local
managed entry point explicitly avoids producing a NativeAOT test executable while executing
all real OS IPC tests; the CI native gate remains mandatory. No antivirus policy is weakened.
