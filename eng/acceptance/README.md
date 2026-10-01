# Real Minecraft evidence

`minecraft-matrix.json` is a review queue, not a support list. It pins 1.21.1 rather than accepting an ambiguous `1.21.x`. Add other minor versions as independent cases. Java 25 and historical ARM64 candidates require explicit review of actual Loader/JVM availability; unavailable combinations need a reason, not a fabricated pass.

```sh
python -m unittest discover -s eng/acceptance -p 'test_*.py'
python eng/acceptance/verify.py --policy-only
python eng/acceptance/verify.py --evidence /path/to/reviewed-evidence --commit FULL_40_CHARACTER_SHA
python eng/acceptance/verify.py --evidence /path/to/reviewed-evidence --commit FULL_40_CHARACTER_SHA --require-all
```

Evidence records are top-level JSON files in the evidence directory. Artifacts live below the same directory and must be regular, non-linked files (each at most 64 MiB). The record itself is at most 1 MiB. Supply actual measured values; this incomplete example intentionally fails admission:

```json
{
  "schema": 1,
  "case_id": "1-20-1-fabric-java17-linux-x64",
  "commit": null,
  "review": {"approved": false, "reviewer": "reviewer-alias"},
  "result": "passed",
  "kind": "real-minecraft-world",
  "environment": "physical-desktop",
  "native_host": true,
  "authenticated_session": true,
  "forced_stop": false,
  "exit_code": null,
  "minecraft": "1.20.1",
  "loader": "Fabric",
  "loader_version": null,
  "java": 17,
  "platform": "linux-x64",
  "phases_seconds": {
    "host-connected": null,
    "client-ready": null,
    "world-entered": null,
    "world-left": null,
    "normal-exit": null
  },
  "artifacts": [
    {"kind": "world-visual", "path": "case/world.png", "sha256": null},
    {"kind": "host-observation", "path": "case/observations.json", "sha256": null}
  ]
}
```

Phase offsets use monotonic elapsed seconds from the same run. Enter and leave the world at least 600 seconds apart. Preserve Host identity/connection and normal-exit observations; inspect the world screenshot/video before approving. Redact account identities and paths before archiving. Hash verification binds bytes, not the truth of an observation: the reviewer remains responsible for provenance. Synthetic unit-test artifacts never become real evidence.

An unavailable combination uses `result: "not-applicable"`, a reviewed explanation (`reason`, 20–1000 characters), current commit and case identity. It is excluded, never marked supported. Pending cases remain pending; `--require-all` returns 1 when evidence is missing and invalid records return 2.
