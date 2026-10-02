# Native font compatibility

## Failure and backend policy

An installed family name is not proof that Avalonia/Skia can load its typeface. On the
Linux native desktop, fontconfig selected OpenAI Sans from a WOFF2 file, but Avalonia
returned false for the default glyph typeface. Deferred `FormattedText` measurement
then threw during the first-run window's first render. Headless font implementations
did not reproduce this failure.

Keep a loadable platform default for each requested weight/style. Only on failure,
probe conventional desktop text families, then the remaining installed families.
Cache successful choices per font-manager lifetime and weight/style, so repeated
text drawing and caret hit testing do not rescan fonts. Labels, capsules, styled
runs, input text, selection and IME/caret measurement all use the same resolver.
Avalonia retains responsibility for character/script fallback and shaping. If no
family can be loaded, report an explicit failure rather than silently drawing no text.

This is confined to the Avalonia edge. It changes no public contracts, services,
fontconfig files, user settings or process environment, and introduces no bundled
font/license or reflection dependency.

## Acceptance

- Preserve usable defaults without enumerating installed fonts
- Skip unloadable candidates and discover nonstandard installed families
- Preserve requested weight/style and cache only successful resolutions
- Fail explicitly when no usable font exists; permit a later successful retry
- Exercise real formatted-text measurement, styled text, capsules and input
  selection/preedit/caret/hit testing through the shared policy
- Run the backend suite, architecture checks and native font smoke on the failing
  desktop without `FONTCONFIG_FILE` or other process font overrides
- Build with trimming analysis and validate NativeAOT where supported

The native smoke validates the installed font environment and drawing/measurement
paths. It does not certify physical display quality, OS IME behavior or long-run
GPU performance.

## Execution evidence (2026-10-02)

- .NET SDK 10.0.100 Release backend/test and Desktop builds completed with zero
  warnings/errors; all 12 backend cases passed, including the rendering/input checks
- The architecture executable passed for 69 projects after building missing reference
  outputs. Scoped whitespace verification for all five changed C# files and
  `git diff --check` passed
- Native `--native-font-smoke --expect-font-fallback` reported the original OpenAI
  Sans default as unloadable. Noto Sans loaded for normal/semibold/bold, each in
  normal/italic style; mixed English/Chinese measurement and all drawing/input paths
  passed without a fontconfig override
- The original Linux desktop environment completed all four first-run steps, restarted
  into the launcher, and navigated Settings in Simplified Chinese with readable text
  and no font exception
- Linked trimmed publication compiled the product but could not run the linker's
  `ComputeManagedAssemblies` task host (`MSB4216`, followed by `MSB4027`). Authorized
  retries, including an explicit SDK path, reproduced the environment/task-host
  failure. No trimmed executable or trimmed runtime smoke is claimed
- NativeAOT publication was not run in this environment. The unrelated existing
  `Nexa.Services.Tests` CS0407 (`Task`/`ValueTask`) compile failure was independently
  reproduced and left unchanged; this scoped result is not a full solution-test pass

These checks do not close the long-run or physical GPU/game acceptance gates.
