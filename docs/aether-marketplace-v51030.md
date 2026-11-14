# AetherMU Marketplace V5.10.30 candidate operations

This candidate extends V5.10.29; it is disabled by default and is not a public
deployment approval. Preserve commits `657d977` and `e710f36` and their SQL order.

## Transaction boundary and topology

Native `data` tables and `aether_marketplace` tables must be in the **same
PostgreSQL database**. Both data sources must observe the same
`native_delivery_store.store_id`. This check is mandatory on the gateway,
custody and delivery paths. Website authentication may remain in its separate
dedicated SQLite store; it is not part of native payment or item ownership.

The inspected local `bin/Release/ConnectionSettings.xml` configures native
contexts to `localhost:5432/openmu`. This is a local configuration finding,
not evidence of the deployed production topology. Environment overrides and
remote deployment settings were not queried, and production was not contacted.
Confirm the effective runtime target and database UUID before activation.

Zen uses native inventory `Money`, not a new shadow wallet. Save native actors
under their persistence locks, then settle both balances and the immutable
journal in one PostgreSQL transaction. Refresh live actor balances before
releasing their locks. EF Money concurrency metadata must be deployed with the
updated compiled models; old binaries are incompatible with live Zen settlement.
Host every native session writer for a routed realm in the coordinated host.
Independent GameServer processes need a distributed actor ownership protocol
before Zen activation; this candidate does not claim that topology is supported.

## SQL and roles

After the verified V5.10.29 schema, apply
`src/AetherMarketplaceGameServer/marketplace-zen-native-v51030-staging.sql`
under an operator migration identity, in a transaction. It extends native proof
and custody guards for Zen and cancellation without enabling the legacy
`PAYMENT_PROOF_NOT_IMPLEMENTED` entry point. Existing AC v4 accounting remains
the authority; the new protected entry delegates to it.

Use a dedicated login granted `aether_v30_settlement_runtime` for the private
bridge, never the database owner. The NOLOGIN custody owner has only the
privileges needed by its protected functions. Browser and website identities
must not receive SQL role membership. Native ORM persistence remains configured
through the existing OpenMU native writer; do not replace it with the bridge's
restricted connection. Verify permissions with the disposable tests first.

The generated EF migration `ProtectNativeZenBalances` has empty Up/Down methods:
it records a model concurrency change, not a database column change. Apply
normal OpenMU migration review and ship the matching model binaries together.

## Private host configuration

Both Startup and Dapr GameServer.Host reference and map the private bridge.
An enabled bridge requires these operator-provided environment settings:

```
AETHER_NATIVE_MARKETPLACE_ENABLED=true
AETHER_NATIVE_MARKETPLACE_TOKEN=<new-server-only-secret-at-least-32-characters>
AETHER_NATIVE_MARKETPLACE_ROUTING={"Realms":{"<actual-byte-server-id>":"Origin"},"ConnectionVariables":{"Origin":"AETHER_MARKETPLACE_ORIGIN_DB"}}
AETHER_MARKETPLACE_ORIGIN_DB=<restricted-connection-to-the-verified-native-database>
```

Use exact native Realm names Origin, Legacy, Evolution and Ascension and actual
server IDs; do not infer mappings from ordinal position. Configure all host-owned
contexts for each realm. Neither the source template nor the examples contain
usable credentials. Bind the host's API privately and keep administrator routes
unreachable from public player traffic. The bridge additionally checks the remote
socket is loopback and compares its own bearer secret in constant time.

The existing website backend needs the same enable flag and secret plus
`AETHER_NATIVE_MARKETPLACE_URL=http://127.0.0.1:<host-port>`. It resolves website
ownership through the existing revocable session authority. Operator-verified
`marketplace_account_identity` and `native_account_binding` records are required;
there is no automatic trust of browser-supplied ownership.

The recovery plugin remains disabled by default. Its ID is
`4028C0B8-2A81-4FC1-B8D0-947C52C90E29`. Before explicitly enabling it in the
existing plugin manager, supply its `Realms` and `ConnectionVariables` configuration
using the same verified routing. An HTTP bridge enable flag does not enable the
periodic recovery plugin automatically. It resumes custody acquisition, paid
delivery and original-item returns; offline/full inventories stay pending.

## Castle Treasury gate

AC commission accrual stays at 5%, per Realm. Zen accrues no commission.
Native siege state exposes the current owner and selection/registration scores;
these are not an immutable final ranked result for a siege cycle. Do not use
those scores to invent 2nd/3rd place winners.

`CastleTreasuryIntegrationAdapter` is a tested, read-only authority boundary and
allocation planner: it validates event/Realm/guild identities, calculates
50/30/20 without integer overflow, and rolls missing shares and rounding into
the next event. `ExecutionEnabled` is permanently false. It does not modify
wallets, persist payout proposals or execute payouts. Payout execution, durable
final-event ingestion and payout journals require a real authenticated final
results source and an approved guild treasury custody destination. Individual
guild-master wallets are not assumed to be guild treasuries. Keep this gate closed.

## Safe validation and recovery

`Run-NativeCustodyTests.ps1 -IncludeRegression` creates a uniquely named private
PostgreSQL 17 container, builds Release once and runs tests with `--no-build`.
It removes only its own container and restores its environment variables.
`Run-ProcessRecoveryTests.ps1 -EvidenceDirectory <new-local-evidence-folder>`
tests eight actual process termination points. It verifies process ancestry
before stopping its own testhost, which contains an actual GameServer instance.
It never targets existing GameServer processes or production containers.
The test players and guild result authorities are synthetic; those results do
not constitute Season 6 client acceptance.

Before any deployment, capture database backups, role grants, native binaries,
plugin configuration, website source and effective routing with secrets stored
separately. Reconcile pending custody and paid deliveries from the journal after
restart; retry the same operation UUID, never generate a refund from a timeout.

## Rollback

Disable website and native bridge activation and stop accepting new listings.
Disable periodic recovery only after recording pending work. Do not drop custody,
settlement, return or receipt tables. Do not move original items with ad hoc SQL.
Keep matching V5.10.30 native binaries available to reconcile Zen balances and
deliveries. Do not run V5.10.29 binaries against active Zen settlements: they do
not enforce the new native Money concurrency contract or understand Zen proof.

For a staging rollback, restore the paired pre-migration native + Marketplace
database snapshot and matching binaries together after confirming no accepted
transactions must be retained. A live rollback needs a reviewed reconciliation
plan; independently rolling back either side risks losing ownership/payment
evidence. No destructive automatic rollback script is supplied.

## Remaining release gates

Require the final test report and native Season 6 client acceptance before public
activation. The real GameServer tests run server code with synthetic authenticated
actors and isolated data, not network game-client login. Check two real clients,
inventory notifications, reconnect, option persistence and every deployed Realm.
An existing `Main.exe` alone does not prove compatibility with the reference
Season 6 Episode 3 1.04d client. Never label the candidate 100% accepted while
that stage or the production topology verification remains outstanding.
