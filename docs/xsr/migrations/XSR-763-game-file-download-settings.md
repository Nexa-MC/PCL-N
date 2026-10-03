# XSR-763 — Game file download policy

Global `network.file-concurrency` controls independently processed game files (1–64,
default 8), not OS threads or the shared Host HTTP/disk admission budget. Explicit legacy
`ToolDownloadThread` values migrate as slider + 1, capped at 64; the old default 63 retains
the existing XSR default. `network.file-retry` defaults true and permits one additional
attempt after download or integrity failure. Source failover and verification remain
mandatory and independent of the retry preference.

Settings owns durable values and resolves a small immutable policy before install or
launch file completion begins. The batch captures it once; subsequent settings changes
affect new work. Modpack base-game preparation uses the same install path. Archive files,
resource dependencies, Java acquisition and loader processor downloads keep their existing
budgets; controls and hints explicitly identify the game-file scope.

UI enables the existing catalog positions, without adding a second settings store. Tests
cover durable values, invalid limits, legacy slider conversion, bounded workers, captured
policy and real install/file-completion retry behavior. All verification and commit-last
contracts continue to apply when retry is disabled.
