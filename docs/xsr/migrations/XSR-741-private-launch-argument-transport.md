# XSR-741 Private launch argument transport

## Boundary

`MinecraftProcessService.StartAsync` validates argument transport before calling any process
port. A configured JVM Host receives only `--jvm-host` in the operating-system argument vector;
the complete launch request uses its private, bounded stdin bootstrap. Without a configured
Host, the process port must explicitly guarantee private argument transport or launch fails
before the port is called. This applies to default services, both runtime composers and
injected ports. There is no direct-Java fallback or retry after Host failure.

The guard covers the entire argument vector. Manifest JVM/game arguments, legacy arguments,
inherited arguments and custom arguments can all contain expanded account credentials.
Recognizing an `--accessToken` flag cannot establish that the remaining arguments are safe.
Even apparently benign vectors are refused when their transport has no privacy guarantee.

## Compatibility

`IMinecraftProcessPort.UsesPrivateArgumentTransport` is an additive default interface member
whose default is `false`. The standard OS process port keeps that default. A trusted custom
in-process port may return `true` only when the supplied launch argument vector never enters
public OS process arguments. This is a transport implementation contract, not a capability
selected by a manifest, profile or Sidecar. Test ports that discard the supplied vector and
return an unrelated fixture process explicitly declare this behavior.

Desktop always configures the sibling `Nexa.Jvm.Host` path, including unpackaged builds.
A missing component fails launch and requires repairing the complete launcher installation.
Legacy hand-built plans may use a private custom port; exporting them to JVM Host still
requires the explicit main-class boundary established in XSR-726.

Bootstrap encoding and budgets precede child creation. Existing asynchronous output drains,
transfer cancellation, child termination, stdin closure and bootstrap buffer clearing remain
the authority for Host lifecycle. No assembly boundary or Sidecar ABI changes are introduced.

## Scope and validation

This closes the implicit public-argument transport. It does not claim isolation from malicious
trusted in-process code or same-account process memory, nor resolve the audit's separate OS
keychain and installation-attestation questions.

Validation records a pre-change synthetic credential reaching an unconfigured recording port,
then verifies refusal before that same port is called. Regression cases cover custom JVM/game
values, legacy plans, default and injected ports, cancellation, private fixture transport and
the existing real Host stdin/bootstrap tests. Managed and NativeAOT service/Desktop suites,
architecture checks, whitespace checks and product trimming are required before closing this
unit. An independent read-only candidate review follows the focused reproduction.

## Recorded checks

The pre-change recording port was called once without Host and received the synthetic
credential in both custom JVM and game values. The same reproducer against the candidate
reports zero calls without Host; the configured-Host control still reports one call with no
credential in argv. No OS process with a real account credential was created by this probe.

Three focused transport/bootstrap tests pass, including actual child stdin delivery and
cancellation. Full managed and Linux NativeAOT Services suites each pass 475 cases; managed
and Linux NativeAOT Desktop each pass 109. The 69-project architecture check and included-source whitespace check
pass. A fresh independent read-only candidate reviewer found no concrete surviving bypass or
unintended compatibility regression in scope; its inspection is separate from runtime tests.

The independently published NativeAOT product passes its 52-node shell and first-run checks;
compiler/Roslyn files are absent from both native Desktop outputs. Physical Minecraft launches
and platform-specific installation repair remain
external acceptance work, not conclusions drawn from these fixtures.
