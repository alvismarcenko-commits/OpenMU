# Origin x1 Reset / Grand Reset activation

This is an explicit operator activation for the isolated V5.10.31 x1 fixture.
It does not change global OpenMU defaults or activate production servers.

The published commands are `/reset`, `/grandreset`, and `/resetinfo`. The Reset
chat handler can be active while `ResetFeaturePlugIn` is inactive; in that case
`/reset` correctly reports that the system is not enabled. Grand Reset also
requires its separate command plugin, compatible feature settings, the Prestige
SQL contract, the class counter definition, and approved native ownership.

Stop the target candidate and take a PostgreSQL backup before applying:

```powershell
py -3 apply.py --container <isolated-postgres-container> --database <nonce-owned-staging-db> --user <operator-role>
```

The helper sends guard + migrations + activation in one `psql --single-transaction`
operation. It refuses missing ownership markers, multiple game configurations,
non-x1 rates, missing registered plugin IDs, incompatible Reset settings, legacy
GR stat bonuses, or conflicting Prestige ownership. Restart the same verified
GameServer after the commit to reload its configuration.

The resulting Origin rule is **25 Reset = 1 Grand Reset, +100 APP**. GR resets the
level to 1 and resets to 0 through the existing native atomic persistence code.
It does not award stat bonuses or enable Prestige item delivery. Other realm
rules remain inactive. Existing normal Reset level/cost/point/logout settings
are preserved; the reviewed fixture requires level 400 and returns to level 10
after a normal Reset. Its 25th normal Reset costs 25 Zen under the existing
one-Zen-times-next-reset-count policy. GR itself does not consume inventory items.

The counter configuration matches the existing `GrandResetAttributesPlugIn`:
one persistent definition and a zero-valued class default, added only if missing.
Existing counter values are never overwritten. Enrollment reads the existing
verified `Origin` native account bindings and inserts only absent profiles with
zero APP/GR. It neither changes Marketplace tables nor infers ownership for
unbound/template characters. Future verified bindings can be enrolled by an
explicit repeat application; existing balances and history are preserved.

The three unchanged prerequisite SQL files were copied from BalticAetherMU
`5500ed81a7e5e962478e41d121436c67673d75a8`, `prestige/migrations/001–003`.
Their original first two migrations remain immutable. Migration 003 establishes
the reviewed 100 APP contract before Origin activation.

Validation tools are under `tools/origin-reset-validation`: six real PostgreSQL
tests run in a fresh clone, including refusal of an unowned DB, idempotence,
24/25 threshold behavior, and unchanged Marketplace/inventory/progress hashes.
The .NET command probe inspects the actually published GameLogic assembly,
including command keys, registration attributes, GUIDs and the default opt-out.
The SQL threshold test is a SQL contract check; it does not stand in for the
separate native gameplay test of both commands.

`configure_client.py <client-directory>` validates the existing Origin marker
and approved executable SHA256 before pinning only the connection section to
127.0.0.1:44416. The observed 45406 override points to the separate legacy x100
instance and selects the encrypted protocol in this MuMain binary. The guard
refuses the unmarked owner client and other realm copies; it preserves visual
assets and unrelated INI settings. Run it before starting the private candidate.
