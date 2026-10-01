# XSR-736 Compile-time Function Patch rewriting

The architecture lock and project graph add one justified assembly: an independent managed
Roslyn CLI with its own build/test lifecycle. Desktop's build-only project reference never
references its output assembly or ships Roslyn. Native/AOT/RID globals are removed, and only
the build tool opts out of the SDK's executable runtime-dependency validation. Desktop keeps
that validation for product executables; the architecture gate enforces this separation.

The compiler rewrites explicit opt-in static string methods before CoreCompile, preserving
their public/internal signature and moving the original body to a private method with a cached
delegate. The initial pure string/Length expression and return/if/block subset rejects shapes
that could change Caller Info or require unsupported async/ref semantics. The opt-in attribute
is a compile error unless removed by rewriting: omission cannot silently ship an unpatched
method. Generated files stay under the finalized SDK obj/configuration/framework/RID path;
compile-item replacement runs even when generation is skipped. Relative #line mappings allow
embedded-source/debug compilation without workstation paths.

Desktop owns the `ui.resource.project-title.v1` catalog and grant, forwards admission through
the Sidecar supervisor, and injects the same numeric binding into resource presentation.
List and detail titles execute validated session programs locally; literal localization
flags, project identity and provider/download metadata retain their contracts. Normal
subsequent presentation rebuilds observe activation/retirement; this is not a live refresh adapter.

Managed and Linux NativeAOT validation pass 107 Desktop tests and compiler compilation/execution, expression
body, debug mapping, cached delegate and 18 rejection cases. Fresh and repeated incremental
builds pass without warnings/errors. The architecture gate passes 69 projects including
build-tool isolation and incremental item-replacement checks. Fresh NativeAOT output contains
no patch compiler or Roslyn artifacts. Standalone NativeAOT product publication also contains
neither tool nor Roslyn; shell/trim validation reports 52 semantic nodes and first-run
validation exits successfully. Changed-source whitespace verification passes.

A real two-hour NativeAOT idle composition fixture is running with a frozen binary and a
receipt recording its binary SHA-256, base commit plus XSR-736 worktree identity, and source
input hashes. No completed two-hour result is claimed here. Its embedded build version uses
local SDK defaults; it is not a clean-commit release or OS-window/Minecraft acceptance run.

Other ABI shapes and UI Patch/Event/Intent adapters remain open. This unit must not be
described as an arbitrary method rewriter or completion of the whole Sidecar platform.
