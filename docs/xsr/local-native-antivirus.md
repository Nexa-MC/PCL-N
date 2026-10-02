# Local NativeAOT test quarantine

On 2026-10-02 the user's security log identified `Trojan/Injector.cns` and deleted
`tests/Nexa.Sidecar.Tests/bin/Release/net10.0/win-x64/native/Nexa.Sidecar.Tests.exe` while
Microsoft's `link.exe` was generating it. The resulting publish failed with MSB3030 because
the generated native executable no longer existed. The linker has a valid Microsoft
Authenticode signature. Static inspection of the test, protocol and transport sources found
no process-injection APIs. The removed binary was unavailable for hashing or analysis;
these facts alone do not establish a vendor false-positive verdict or a malware verdict.

The .NET runtime project has documented antivirus false positives for NativeAOT output:
<https://github.com/dotnet/runtime/issues/110541>. That is supporting context, not proof for
this particular security product, signature or removed binary. No sample is submitted to a
third party and no antivirus exception, exclusion, quarantine restoration or security-policy
change is made by repository tooling.

Run `pwsh -File eng/xsr/Test-Sidecar.ps1` for local protocol and real OS IPC validation. This
entry point explicitly runs the managed assembly and creates no NativeAOT test executable.
The managed test project also omits its unnecessary unsigned apphost. It does not skip pipes,
sockets, protocol checks or performance checks. Desktop release publication remains NativeAOT.

The dedicated Windows/macOS/Linux CI gate explicitly publishes and runs the NativeAOT test
executable, retaining the native compatibility requirement. Its result cannot be relabeled as
a clean bill of health from the user's antivirus. Any native image quarantined by security
software remains an external review item; do not vary payloads, padding, names or protection
settings to evade detection. Native artifacts are still subject to independent publisher trust
and vendor review.
