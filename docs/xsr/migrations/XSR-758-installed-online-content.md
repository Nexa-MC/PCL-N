# XSR-758 installed content association and updates

Resource services identify local mods, resource packs and shaders from bounded streamed
SHA512/SHA1/CurseForge fingerprints, never filenames. Batch queries preload provider
fingerprints in batches of 100; detail resolution has at most two concurrent workers.
Desktop starts background association after local scan, publishes only into the matching
instance/page/file-size/mtime generation, and keeps original filenames and local evidence.
Unrecognized/offline files remain usable and are not represented as known up-to-date.

Resource-pack/shader update commands re-identify the expected current file, re-fetch target
metadata through the existing download service, verify kind and current Minecraft version,
and check shared game-directory process ownership. No guessed URL or UI-supplied digest
is trusted. Download failures retain the original; publication preserves a recoverable old
file and rolls back on failure. Updating does not modify an active game's resource directory.
Online identity is a separate Resources contract, not a dependency of Minecraft management.

Resources may reference the owned Minecraft.Management implementation/contracts to commit
a verified replacement through its existing recovery gate, running-process checks and trash
journal. This is a one-way dependency; Management never resolves a catalog or network service.
