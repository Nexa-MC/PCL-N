# XSR-753: object-bound Windows update high-water

The historical high-water store assumes a protected path, then reopens names and replaces
the committed file. The Windows helper-side store instead consumes WindowsUpdateDirectory,
opens an exclusive protected leaf relative to that lease, and retains its handle through
monotonic comparison, journal append and durable flush. It does not replace files by path.

Each journal record has an NXH1 prefix, bounded ASCII version length, canonical public
version, SHA-256 checksum and DONE suffix. Total state is bounded to 1 MiB. A complete
malformed/checksum-invalid/non-monotonic record fails closed. An incomplete final frame
with a valid prefix represents an interrupted append; read preserves the last complete
version and append truncates only that suffix using the retained handle. No complete
record is rewritten. There is no automatic compaction or migration from the old text file.

The store serializes through the file's native sharing restrictions. Only sharing violations
are retried, for at most ten seconds. Permission, admission, corruption and capacity failures
are propagated. The helper calls this synchronous primitive outside the UI thread.

Contract tests cover committed persistence, downgrade rejection, interruption recovery,
corruption, bounded state and competing writers. The elevated Windows NativeAOT fixture
executes the store in a fresh protected directory and performs handle-bound fixture cleanup.
Architecture checks prevent production use of the historical path-based constructor.

Automatic replacement remains disabled. Preinstalled helper, signed installation identity,
protected Unix state, replacement/recovery journal, restart and physical power-loss acceptance
remain open. Append checksums detect corruption; they do not authenticate a publisher or
replace the directory/owner/ACL boundary.
