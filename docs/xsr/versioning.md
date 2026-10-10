# XSR product versioning

## Canonical format

The XSR product line begins at `2.0.0`. User-facing versions, build metadata, update manifests, and artifact identity use exactly one of these dotted forms:

```text
Stable: 2.0.0
Alpha:  2.0.0.alpha.1
Beta:   2.0.0.beta.1
CI:     2.0.0.ci.ffffff
```

Grammar:

```text
stable = MAJOR "." MINOR "." PATCH
alpha  = stable ".alpha." positive-integer
beta   = stable ".beta." positive-integer
ci     = stable ".ci." six-lowercase-hex-digits
```

The six CI digits are the lowercase first six hexadecimal characters of the source commit. A CI build with an unknown commit is invalid for publication.

The initial migration build was `2.0.0.alpha.1`. The current default development build is
`2.0.0.alpha.6`, codename **Firefly**. The main window title is `NexaCL`. Update and About pages display
`NexaCL Firefly v2.0.0.alpha.6`; every channel uses its actual informational version.
The codename is presentation metadata and does not change update or compatibility identity. Promotion is monotonic within a stage. Stable `2.0.0` contains no `stable`, `release`, or numeric fourth component.

## .NET and NuGet projection

The dotted product form is intentionally the canonical display and release identity. Some .NET tooling accepts only SemVer or four numeric assembly components, so `eng/xsr/Xsr.Version.props` exposes separate projections:

| Property | Example | Purpose |
|---|---|---|
| `XsrProductVersion` | `2.0.0.alpha.1` | canonical product/display/update/artifact identity |
| `XsrPackageVersion` | `2.0.0-alpha.1` | NuGet-compatible projection only |
| `InformationalVersion` | `2.0.0.alpha.1` | assembly informational identity |
| `AssemblyVersion` | `2.0.0.0` | CLR binding identity |
| `FileVersion` | `2.0.0.0` | numeric file metadata |

The hyphenated package projection must never leak back into product UI or update identity.

## Independent compatibility versions

The product version does not version every XSR contract. These axes remain independent:

- Plugin SDK version;
- Plugin API version;
- private Nexa.Plugin runtime version;
- Sidecar Protocol version;
- Manifest Schema version;
- Package Format version;
- Plugin UI IR version;
- PXML Language version;
- individual capability versions.

For example, XSR product `2.0.0.beta.1` may validate Plugin SDK `1.0.0-rc.1`, private Nexa.Plugin runtime `1.0.0`, and Sidecar Protocol v1. A product release never implies a bump to any of those independent versions.

## Upgrade path (one-way)

The update flow is one-way, and `UpdateEligibility` in `Nexa.Services` is its single decision
point:

- The legacy `1.4.x` line may upgrade to any `2.0.0` build — alpha, beta, or stable. The
  major-version crossing is intentional and is the migration bridge for every existing
  installation.
- A launcher on any `2.0.0` build is never offered a lower version. Downgrades do not exist:
  not to `1.4.x`, not to an older alpha, not to a CI build ranked below the running channel
  (stage order within one numeric version is stable > beta > alpha > ci).
- The candidate equal to the running version is a no-op. Two CI builds of the same numeric
  version differ only by commit, so moving between them is allowed while returning to the
  same commit is a no-op.

The comparison consumes both grammars: the canonical dotted XSR forms above and the legacy
display/tag shapes (`1.4.11`, `v1.4.11-release`, `1.1.8 beta`). Versions outside both
grammars are refused, never guessed.

## Build inputs

Runtime policy uses one immutable `LauncherBuildIdentity`. ProductVersion retains the full
prerelease identity; CoreVersion is display/interoperability only. CI remains a separate rollout
cohort while update discovery uses the alpha feed. Settings and telemetry use the same effective
diagnostics policy. No policy may reconstruct a channel from the numeric core.

New XSR projects import `eng/xsr/Xsr.Version.props`. Builds may set:

- `XsrVersionChannel=stable|alpha|beta|ci`;
- `XsrVersionSequence=N` for alpha/beta;
- `XsrCommitShort=ffffff` for CI, or provide `GITHUB_SHA` from which the first six characters are derived.

The build fails when the selected channel lacks its required sequence/hash or the resulting product version does not match the canonical grammar.

## Human-written release notes

Tagged Launcher Build runs require non-empty, human-written notes before any platform build.
Maintain the full description in the matching GitHub Release. An existing non-empty Release
body is authoritative; CI preserves it, its title, and its publication state when attaching
assets, including edits made while the build is running. CI never replaces it with Git logs.
The prepared body is also saved verbatim in the release metadata artifacts.

For a Release that does not yet exist, a manual tagged dispatch may supply `release_notes`.
An existing Release with an empty body must be filled in the Release editor; dispatch input
cannot override it. Empty input fails in the metadata job with an actionable error. If an
existing body is cleared during the build, publication fails without restoring old text. Missing
notes never produce an empty public release or silently fall back to generated commit text.
Read/authentication failures also fail closed. A newly created release requires both non-empty
notes and distribution assets. Branch CI remains a non-release build and uses an explicit
non-public placeholder instead of inventing a changelog.
The preparation artifact also records whether the Release was absent. Only that explicit
manual-input path may create a new Release; a previously existing Release removed during
the build is never silently recreated, and a draft is never implicitly made public.

The normal tag-push path works by writing the description in GitHub Release before its build
starts. The optional dispatch input is not a replacement for that path: at the time of this
change, default branch `dev` has no `launcher-build.yml`, so its Actions-page manual input
cannot be assumed available. Enabling that entry point is a separate change. Dispatching a
branch still creates CI artifacts, not a tagged release.

Changes here apply only to refs containing the new workflow/scripts. Re-running an older tag
uses its older implementation. This change does not move existing tags or repair the separate
historical-package lookup used to generate differential updates.

