# World validation

This Python 3.10+ tool reads an exported world configuration and optionally the
installed Season 6 client assets. It never connects to a database, changes input
files, repairs configuration or starts a server. The only write is the explicit
JSON report. Use a detached export, not a running database directory.

```powershell
py tools/world-validation/world_validate.py --snapshot world.json --client-data C:/AetherMU/Client/Data --output validation.json
py -m unittest discover -s tools/world-validation -v
```

The input contains `GameMapDefinition`, `MonsterDefinition`, `MonsterSpawnArea`,
`ExitGate`, `ItemDefinition` and `ShopItems` arrays. Each entity has an `Id`;
foreign key names match the OpenMU PostgreSQL model. Maps need `terrain_hex`,
the hex encoding of their complete server ATT data. Only NPC merchant-linked
items belong in `ShopItems`; do not export accounts or character inventories.

The `ClassicWorldRestorationTests.FreshWorldIntegrityAndOptionalSnapshotAsync`
test exports a freshly initialized, in-memory world when the
`WORLD_VALIDATION_EXPORT` environment variable names an output file. This is a
test fixture export, not a live database import or update.

Checks cover finite OBJ transforms, OBJ/ATT/MAP container structure, map resource
presence, canonical server terrain, respawn gate walkability, monster spawn
references/bounds/quantity and the full rectangles of merchant items. The shared
client resource mapping follows the observed S6 MuMain resolver (BC/CC/Kalima/DS/IT).
It does not establish provenance of the installed executable or WEBZEN assets.

Missing input tables and omitted terrain/client inputs are `NOT TESTED`;
out-of-grid decorative objects are `PARTIAL` because placement outside the
256x256 grid can be intentional. Their x/y extent check assumes the client scale
of 100 units per tile; it is not authority to relocate decorations. Non-finite
values remain `FAIL`. Null-map event spawn templates are valid; blocked event
spawn terrain is not automatically an error before its event stage opens.
Model/texture dependency resolution, event-state variants, path connectivity,
AI behaviour and real game sessions are not covered by this offline tool.

Exit codes: `0` for PASS, `1` for detected FAIL, `2` for PARTIAL or invalid input.
`PARTIAL` never means all checks passed. Validation is diagnostic only.

## Fix1 evidence and deployment boundary

The optional `FixClassicWorldConfigurationUpdatePlugIn` repairs only the known
Season 6 Arena gates, Hanzo collision and hash-matched legacy Karutan terrain.
It is not mandatory and preserves customized data. Fresh initializers carry the
same corrections. Merely running the validator never applies this update.
Test and review a backup/rollback plan before an operator enables it.

Karutan resources previously contained a two-byte prefix, 65536 cell bytes and
two trailing zero bytes. The old parser assumes a three-byte header, shifting
the grid and wrapping the extra byte to (0,0). Fix1 changes only the envelope to
`00 FF FF` plus the same cells. Independent installed-client decoding matched
all 65536 low-byte flags in each map. Original cell SHA256:

- Terrain81: `8cceb73897a5e321695eaaa4576ccdfa45bfebb5ab8c078223fd96f2ebede709`
- Terrain82: `fceca98e49ae386df7d7ef6d01b56a865550ac3fcba1e4e2d66636db2a3b483d`

Devias object records 528/529 (zero based), type 86, have Y bytes `FF FF FF FF`
in both the decoded `EncTerrain3.obj` and the legacy `Terrain.obj` record stream.
This establishes malformed stored input rather than a NaN invented by the
audit decoder. No authenticated original Y coordinates were available: Fix1
detects and rejects these records in validation but does not modify the map or
silently substitute/drop its objects. The visible client remains unverified.
