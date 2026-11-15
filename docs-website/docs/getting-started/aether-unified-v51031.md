# AetherMU unified integration candidate V5.10.31

The V5.10.30 acceptance fixture verified Marketplace delivery but did not include
the previously developed Aether client theme or the separate Aether gameplay
branch. This candidate combines those server histories without replacing the
Marketplace custody, settlement, gateway, or original-item delivery code.

## Source provenance

- Marketplace: `794e279c75621361f7f7ab1be9ee3f4f6bddf1e0` (OpenMU PR #4).
- Aether gameplay: `b0cd93874726a2562852c9a61813e3f063a900e0` (OpenMU PR #1).
- World corrections: `a6d23b6cc883f6dda91380c2184612f3f1483532` (OpenMU PR #5).
- Existing client/theme/Launcher: BalticAetherMU
  `5500ed81a7e5e962478e41d121436c67673d75a8` (PR #41).
- Preserved Marketplace portal: BalticAetherMU
  `9204c626a9cb33311a42a1e36589416804208d70` (PR #42).

These are draft development histories. Their presence does not activate features
or prove a complete live release. The resource merge retains both Grand Reset
and classic world update labels. The shared staging welcome now uses the game
brand instead of incorrectly announcing Ascension x100 in every configuration.

## Isolated Windows verification

Build Startup with .NET 10 SDK, Release, `-p:ci=true -p:DebugType=None`. Run from
its published directory with an explicitly configured disposable PostgreSQL
database and `-adminpanel:disabled -resolveIP:127.0.0.1`. Set
`AETHER_LOCAL_STAGING=true` to bind native listeners to loopback. Verify actual
listener addresses before connecting a client. Do not use `-reinit` against an
existing instance or overwrite the owner's client.

The reviewed local fixture cloned the existing V30 disposable database and
changed only its endpoint configuration. Its x1 rates were retained. Native
ports were 46001–46006; ConnectServer ports 44415/44416; private HTTP 50431.
The client uses port 44416 directly. MuMain's ConnectServer port heuristic
requires an unencrypted port within 44288–44543; using an arbitrary high port
can select the wrong protocol.

The client is assembled from the operator's existing copy using the previous
BalticAetherMU assembler and existing approved artwork. Its executable SHA-256
is `94191e71652c2e8809b8c042eed9a79ae52a1e5258b30ff553db53687188aec3`;
the actual Windows file version is 1.3.34.0. The older assembler's v1.28.0 label
is historical metadata, not a different executable. Preserve custom assets.

## Activation boundaries

The local x1 fixture is not a four-realm deployment. Separate realm data,
configuration, account ownership, native writer coordination, and Marketplace
store identity must be reviewed before adding routes. Do not relabel three
default channels as four realms.

The imported Prestige implementation uses the 100 APP transaction contract and
explicit staging/ownership guards. The historical `deploy/aethermu-europe`
stat-bonus descriptions are not the current APP policy. Do not import those
legacy profiles as a substitute for the reviewed Prestige migrations and
per-realm plugin configuration. No existing APP balances were migrated.

The optional world configuration updater is included in source; it is not
silently applied to an existing database. Devias invalid object coordinates and
missing client/audio resources still require authenticated replacement inputs.

Launcher distribution remains disabled without verified manifests, download
locations, and signing public keys. Guardian needs its verified host/source
scope and consumer configuration. Website native provisioning, VIP grants,
premium access, Control Center administration, and native anti-cheat remain
separate acceptance gates. No production deployment or main/master merge is
part of this candidate.
