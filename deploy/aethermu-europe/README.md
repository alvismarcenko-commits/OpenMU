# AetherMU Europe profiles

These are explicit admin import fragments, not startup appsettings. No profile is activated by this change.

| Profile | ExperienceRate | MasterExperienceRate | Grand Reset |
| --- | --- | --- | --- |
| x1 | 1 | 1 | disabled |
| x10 | 10 | 10 | disabled pending balance approval |
| x50 | 50 | 50 | disabled pending balance approval |
| x100 | 100 | 100 | enabled when explicitly imported |

Use one separate GameConfiguration/database per realm. In the administration panel set the GameConfiguration fields from `xN.game.json`. Enable ResetFeaturePlugIn (`6A9D585D-79D7-4674-B6EA-7E87392FA501`) and import `xN.reset.json` as its custom configuration. Only on x100, explicitly enable GrandResetChatCommandPlugIn (`F94D795C-F67B-48AD-8112-EA45DA39D10A`). Leave the command disabled on x1. Defaults and pre-existing configurations opt out of GR.

For an existing Season Six database, apply optional **Grand Reset persistence** (`33CF2935-96EE-40AD-B11C-0B7440F60419`) in Configuration Updates, then restart the servers so cached class definitions are refreshed. On next character login OpenMU's existing AddMissingStatAttributes creates the persistent zero-valued counter. New databases already have it. No schema migration is required: counters use existing StatAttribute rows and the existing character LevelUpPoints column.

`/grandreset` requires at least 100 resets and sets the normal reset count to zero, resets assignable base attributes and experience, and sets the independent Grand Reset level (1). The available point pool becomes min(GR * 100, 2000). Assigning those points does not remove the entitlement: the next normal stat-reset reconstructs it once. GRs above 20 may continue but give no additional bonus. Inventory, equipment, money, skills, master levels, and master points are untouched. The reset configuration must retain ResetStats and ReplacePointsPerReset; incompatible configurations reject GR.

The shipped 500 points/reset value is a fallback; existing per-class PointsPerReset (e.g. 550) overrides it. No new item/zen costs are imposed by these profiles; configure costs and progression tiers separately before production.

Reset and Grand Reset use the same reentrant per-player persistence lock as packet processing and autosave. The eligibility check, debit, reward and explicit save are inside it; successful acknowledgement is sent only afterwards. A second request with the consumed 100-reset balance cannot award a second GR. Save failure restores in-memory values and propagates the error. Client notification failure after commit does not undo a saved exchange.

Operational limits: this relies on OpenMU's normal single live session per character and the persistence provider's transactional save. It is not a distributed request-id ledger. A database commit whose acknowledgement is lost requires reconnect/reload before retry; ambiguous commits and two independently loaded concurrent sessions are not proven safe by the in-memory tests. Validate these failure modes against the production database before rollout. Validate Season 6 client stat/level refresh in-game as well.

### Aether Ascension x100 Grand Reset policy

At least 100 normal resets permit one Grand Reset. All normal resets become zero,
including any excess above 100. Level becomes 1 (`LevelAfterGrandReset`, independent
of `LevelAfterReset`); assignable base stats return to class defaults. Inventory
and equipped items stay in their existing slots. The new assignable pool is
100 points per lifetime Grand Reset, capped at 2000. The counter continues beyond
20 Grand Resets; the cap applies only to points. A later normal reset reconstructs
this entitlement. There is no seasonal wipe or scheduled progress deletion.

Database restart durability and ambiguous save acknowledgements still require an
isolated PostgreSQL integration test before deployment. In-memory rollback does
not establish exactly-once persistence after a lost database acknowledgement.
