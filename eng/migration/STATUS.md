# NexaCL migration execution record — 2026-10-21

The user authorized this one-time Action in `Nexa-MC/PCL-N` to migrate the existing
publisher Secrets and repository history, release Beta 1, then archive the old repository.
The encrypted envelope is bound to the exact run, source SHA, branch and random nonce.
The public artifact contains no private transport key. Both failed runs removed their
private transport keys successfully; the delivered encrypted envelopes were removed from
branch HEAD. No plaintext PAT is stored in Git, artifacts or workflow configuration.

## Completed

- The current XSR branch and all 6,210 reachable ancestor commits are now available at
  `Nexa-MC/NexaCL:refactor/xsr`, SHA `1b69f8c3a8553d8c7970e6659024242f68b35d09`.
  The effective cloud connection can push Contents, unlike the PAT in the runner.
- The destination's original `main` commit `c0479eb3c1f31fb993d6a55e6bb7d647533bf883`
  is preserved. Its only added commit registers the unchanged migrated workflow definitions
  so manual XSR validation can run without repository-administration rights. It remains
  the default branch until authorized Administration access is available.
- `Nexa-MC/NexaCL-Update:main` was initialized at
  `19753aa431eaf07fc3f75f455e2c42ebed8cd708` with its verified publisher workflow, README,
  original LICENSE and original publisher public key. The first live synchronization check
  passed: https://github.com/Nexa-MC/NexaCL-Update/actions/runs/39533892392 .
  No public source release exists yet; this run checked discovery and did not publish assets.
- The local XSR checkout tracks `origin = Nexa-MC/NexaCL`; the old source is retained as
  `legacy = Nexa-MC/PCL-N`. Both remote XSR branch SHAs were verified identical.
- Local Release solution build: zero warnings/errors; architecture tests: 72 projects passed;
  release tooling: 70 tests passed; native update/discovery/loopback scenarios: all seven passed.
- Destination CI is running against the exact migrated source commit:
  https://github.com/Nexa-MC/NexaCL/actions/runs/39534012941 .

## Actual permission blockers

Both hosted attempts authenticated the supplied PAT successfully as `MuXue1230-owo`.
The first failed reading destination Actions Secrets:
https://github.com/Nexa-MC/PCL-N/actions/runs/39533365152 .
The second continued independent stages but was also denied Actions administration and
Git writing to the destination:
https://github.com/Nexa-MC/PCL-N/actions/runs/39533617259 .
Concrete responses were HTTP 403 for `GET /repos/Nexa-MC/NexaCL/actions/secrets` and
`GET /repos/Nexa-MC/NexaCL/actions/permissions`; the runner's Git push was rejected.
No destination Secrets were changed. These are the PAT's actual server-enforced permissions,
not the cloud integration substituting its authentication.

The user has been asked to grant the existing fine-grained PAT access to both destinations,
with Secrets, Contents, Actions, Workflows and Administration read/write, plus Administration
on the old source for final archival. No new plaintext credential needs to be posted in chat.
After rights are granted, start a fresh run by updating the temporary workflow's nonce and
pushing that workflow change, verify its public artifact binding, then deliver a newly
RSA-OAEP-encrypted envelope. Never reuse an earlier runner's public key.

## Pending; do not report complete

Remaining historical heads/tags, default-branch/feedback configuration, release Secrets,
signed Beta 1 publication and verified mirror, and old-source archival remain pending.
The Action pauses destination Actions while importing historical tags, preserves existing
Secrets and main history, and restores Actions on failure. It uses the existing explicit
manual-notes tagged dispatch for first publication, so the source's own GITHUB_TOKEN creates
the public release without recursively triggering another release build. The original
signing identity and signature admission checks are retained. The old repository stays
unarchived until all release/update verification succeeds.
