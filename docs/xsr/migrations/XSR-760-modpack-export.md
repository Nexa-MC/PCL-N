# XSR-760 instance modpack export

Migrate dev's selected-file export behavior into an owned Install service, without
launcher executables, credentials or legacy assemblies. Export produces a standard MRPack
(also recognized with .zip) containing modrinth.index.json and selected overrides. Minecraft
and supported primary loader dependencies come from resolved install metadata. Unsupported
loader combinations are explicitly rejected rather than silently exporting vanilla.

Default selection includes mods/config; resource packs, shaders, saves and options are
explicit opt-ins. Known credential filenames, launcher state, logs, caches, telemetry, servers.dat, runtime
libraries/assets and snapshots are excluded. Only regular non-link files in fixed allowlisted
directories are accepted; each file caps at 512 MiB, actual total at 8 GiB, final archive at
2 GiB and file count at 50,000. Actual streamed bytes must equal admitted file size and mtime
must remain stable. Export rejects an active game or changed instance metadata, uses the
existing recovery gate and task service, never overwrites an existing destination, and removes
only its own partial file on failure/cancellation. Local-only resources are embedded in overrides;
the UI reminds users to check configuration for secrets and obtain permission before redistribution. The same package can be
inspected and installed by the existing bounded modpack importer.

Export commands/previews live in Management.Contracts. Install already references Management
and reuses its process ownership/recovery operations. Management must not reference Install's
implementation; the existing one-way graph remains acyclic.
