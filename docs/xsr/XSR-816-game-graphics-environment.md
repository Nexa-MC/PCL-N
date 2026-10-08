# XSR-816 — Finite game graphics environment policy

2026-10-08. This boundary adds two next-launch, instance-overridable preferences:
`game.gpu-preference = auto | secondary` and `game.renderer = auto | mesa-software`.
The launch request and immutable plan carry those values. Immediately before the owned
private JVM host starts, a Platform provider captures current eligibility and returns a
bounded environment overlay. Services merge that overlay into the immutable launch plan;
an explicit conflicting user environment variable rejects the launch instead of silently
changing which graphics request is sent.

Launcher presentation has a separate restart-only native policy:
`appearance.hardware-acceleration-disabled` retains the legacy
`SystemDisableHardwareAcceleration` Boolean and forces Avalonia Software rendering on
Windows/Linux. Both early startup and standalone shell configure the same typed SDK
options; an already initialized native lifetime keeps its captured policy through shell
handoff. macOS does not advertise this software-force preference. This setting does not
alter the JVM environment described here, and game renderer choices do not reconfigure
the launcher's initialized presentation backend.

`auto` introduces no environment variables and preserves the normal OS/library selection.
On Linux, `secondary` requires distinct, observed PCI DRM devices, one observed boot-VGA
primary, and a secondary device using a known Mesa kernel driver. Its actual PCI identity
becomes Mesa's documented `DRI_PRIME=pci-0000_01_00_0` selector. The provider does not
infer a dedicated GPU, power class, VRAM capacity or that Minecraft actually used that GPU.
`mesa-software` requires observed local Mesa software DRI and Mesa GLX libraries with
bounded ELF headers matching the current process architecture, and sends
the documented `LIBGL_ALWAYS_SOFTWARE=1` environment request. Device/driver/library
evidence is recaptured for each launch, with fixed bounded filesystem paths and no shell.
Missing or ambiguous evidence rejects an explicit request as dependency unavailable.

Windows and macOS return PlatformUnsupported for these explicit selections: this private
embedded-JVM host has no implemented per-instance GPU-selection API on those platforms.
A global per-executable Windows registry preference would affect concurrent instances and
does not satisfy this contract. This is a finite supported-platform result, not a stored
string that is accepted without a consumer. Defaults work on every platform.

Tests cover real bounded temporary DRM/library fixtures, malformed/ambiguous PCI identities,
absent dependencies, no default overlay, conflict rejection and environment handoff into
the actual owned private bootstrap child. They do not claim physical GPU rendering or FPS.
