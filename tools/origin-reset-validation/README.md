# Origin activation validation — 2026-11-15

This activation was applied only to the nonce-owned V5.10.31 PostgreSQL candidate.
The published native assemblies remain the verified `e2787167fef848da9913c346d1d67b73e3643a37`
build. No Marketplace implementation, production database, or global plugin
defaults were changed.

Completed checks:

- Published assembly reflection: `/reset`, `/grandreset`, `/resetinfo` keys,
  fixed GUIDs, registration attributes, and feature default opt-out verified.
- `test_activation.py`: 6 passed against a fresh PostgreSQL clone; includes
  unchanged Marketplace/progress/inventory fingerprints and ownership refusal.
- `test_client_endpoint.py`: 4 passed, including observed 45406 regression and
  refusal to modify an unmarked owner client or incompatible executable.
- Existing native Reset/GR action and calculator tests: 61 passed, zero skipped.
- Existing native PostgreSQL GR/Prestige tests: 29 passed, zero skipped.
  Process-restart durability was excluded from that run; do not describe it as
  a server-restart acceptance test.
- Owner executed `/reset` in the real client on the newly created ordinary QA
  character: 24 to 25 resets, level 400 to 10, 37500 points, Zen 2000 to 1975.
- Owner executed `/grandreset`: level 1, resets 0, points 0, GR count 1,
  APP 100, one ledger entry. The nonempty inventory fingerprint stayed identical
  before Reset, after Reset, after GR, and after character reentry.

Known presentation limitation: immediately after GR, this client retained the
old 25-reset value in the character panel while chat and PostgreSQL showed zero.
Switch Character and reentry refreshed the panel to level 1 / resets 0.
This activation does not claim to fix that immediate client refresh defect.
Character reentry was tested; a fresh account login after GR was not observed.

Runtime evidence, TRX files, snapshots and Latvian report are delivered outside
Git with the private local candidate. Account passwords and database credentials
are deliberately excluded from this source change.
