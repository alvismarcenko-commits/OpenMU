# AetherMU Marketplace v4.14 GameServer adapter

This opt-in integration uses `GameServer.Context.GetPlayersAsync()` to locate a
unique registered character, verifies the independently stored Marketplace/OpenMU
account binding and configured realm, and calls the existing
`MarketplaceInventoryDelivery.DeliverReservedItemAsync`. Identity is checked again
inside the player's persistence lock, including after the reservation claim.
No player object or item specification is accepted from a delivery request.

## Composition inside the trusted server process

Reference `AetherMarketplaceGameServer.csproj` from the owning host when enabling
the feature. Given the actual `gameServer`, a server-configured `realm`, and a
server-owned `NpgsqlDataSource` named `dataSource`:

```csharp
var reservations = new PostgreSqlReservedNativeItemSource(dataSource);
var resolver = new GameServerNativeDeliveryContextResolver(
    gameServer.Context, realm, reservations);
var gateway = new NativeDeliveryGateway(resolver);
var coordinator = new PostgreSqlNativeDeliveryCoordinator(
    new PostgreSqlDeliveryJournal(dataSource), gateway);
```

This change does not activate a production host or expose a web endpoint.
`native-item-reservation.sql` is a deployment artifact, never run automatically.
The purchase/escrow service must create a `ReservedNativeItem` JSON snapshot after
verifying purchase ownership, account linkage, realm and exact purchased options.
Create the journal and reservation in the same purchase transaction. The snapshot
and item UUID must remain immutable. Do not derive the snapshot from client input
at delivery time. Reservation creation, payment settlement and account-link
enrollment remain responsibilities of that trusted purchase service.

Grant the game role SELECT and column UPDATE on `native_started_at` only;
reserve INSERT for the trusted purchase service. Neither web nor game roles may
modify snapshots, delete reservations or reset claims. Missing schema, snapshot,
identity or claim prevents native delivery. Realm is a configured realm key,
not a value inferred from player names or numeric server IDs.

## Failure and recovery

The existing journal claim must be PROCESSING. A second atomic PostgreSQL claim
compares the complete snapshot and journal identity before native execution.
`native_started_at` is permanent and unique item UUIDs prevent a second reservation
for the same item. New connections and adapter instances cannot retry a claim.
The native save returns the exact reserved persistent item UUID before the journal
can confirm DELIVERED.

Save errors, cancellation after claim, process loss, invalid configuration and
lost acknowledgements require reconciliation; a claim is never automatically
released. An inventory-full attempt also consumes the native claim. An operator
must verify both journal and OpenMU persistence before deciding recovery. A
confirmed or possibly committed item must never be deleted as a blind rollback.
This is conservative at-most-once execution, not an atomic transaction across the
Marketplace journal and OpenMU database. A PROCESSING journal after a crash is
not automatically resumed.

## Isolated verification

Run `tests/AetherMarketplaceGameServer.Tests/Run-IsolatedTests.ps1` with Docker and
the .NET 10 SDK. It builds both affected test projects with `ci=true`, preventing
unrelated code regeneration, starts its own PostgreSQL 17 container at loopback
port 55439, uses a generated password and removes only that container in `finally`.
It never reads production connection settings. An occupied test port causes a
failure rather than reuse of another server.

The PostgreSQL tests require `AETHER_V414_DISPOSABLE_DB=1`; ordinary test runs skip
them. The full isolated run verifies native inventory, bad identities, account
change after resolution, parallel delivery, changed snapshots, cancellation,
inventory capacity, uncertain saves, durable reservation claims and actual OpenMU
Entity Framework item persistence loaded through a fresh provider/context.
It also runs the existing 45 Marketplace regression cases against this container.
The existing regression fixture accepts `AETHER_MARKETPLACE_TEST_DB_PORT` so the
isolated run does not connect to the pre-existing service on port 55432.

No main/master changes, merges, deployment, production database changes or
production server startup are part of this integration.

## V5.10.29 original-entity custody candidate

`GameServerNativeCustody.ListAsync` validates a live seller and independently stored
account/Realm binding. A durable DRAFT/ACQUIRING intent fences the original item;
the native inventory save moves that same entity into configured realm custody.
Only that committed ownership transition exposes the listing as ACTIVE/HELD.
Interrupted acquisition can be resumed with the same listing id by the recovery hook.

After V5.10.28 AC settlement, `GameServerCustodyDelivery` implements the existing
`ISafeDeliveryJournal` and recoverable `ISafeNativeDelivery` contracts. The safe
coordinator permits retry only for this explicitly recoverable adapter; legacy
adapters retain their conservative behavior. Purchase id is the delivery id.
Database advisory transaction locks fence workers. Offline/full-inventory cases
leave a PENDING receipt and the original entity in custody.

`MarketplaceInventoryDelivery.TransferExistingItemAsync` loads the original
escrowed entity through the native player context, preserves its persistent
option/set links, finds a real inventory slot and saves native progress. A native
database ownership trigger rechecks the paid purchase, accounting proof, custody,
listing, actual character/account and destination. It commits ownership,
COMPLETED receipt/purchase, DELIVERED custody and SOLD listing in the SAME native
save transaction. Application acknowledgements cannot create completion. A lost
acknowledgement is recovered from that committed receipt, without moving again.
An uncertain save disposes the affected player session/context without retrying
its dirty change tracker; a new session reconciles the durable state.

This candidate explicitly requires Marketplace state and native data in the same
PostgreSQL database. A per-store UUID is checked through both independent data
access paths before mutation. Different stores and unsupported persistence
providers fail closed. This is not a distributed transaction implementation and
must not be enabled for split-database deployment.

The new `NativeDeliveryRecoveryPlugIn` uses OpenMU's periodic-task plugin hook,
which also resumes deliveries after reconnect and after space becomes available.
It is disabled by default. Startup and Dapr GameServer hosts reference/load the
adapter assembly for normal plugin discovery. Configure explicit `Realms`
(server-id to realm key) and `ConnectionVariables` (realm key to environment
variable name). Do not infer Realm from EXP rates, character names or server ids.
Origin, Legacy, Evolution and Ascension may each use their own configured source.
No connection strings or enabled configurations are shipped.

Migration dependencies through V5.10.28 must exist first. The isolated runner
lists the precise order, then installs `marketplace-native-ownership-fix3.sql`,
`marketplace-native-delivery-v51029-staging.sql`, and the separate
`marketplace-native-delivery-permissions-v29-staging.sql`. Configure
`native_realm_custody` with a dedicated native ItemStorage before listing.
The old `transfer_reserved_native_item` gate is unchanged; this candidate does
not enable that unsafe legacy entry point or snapshot-based recreation.

The restricted `aether_v29_runtime` role receives read access and EXECUTE on
validated entry points, never direct table mutations or wallet settlement.
SECURITY DEFINER functions use a fixed search_path and a non-login owner.
The native ORM login must additionally have USAGE on aether_marketplace and
SELECT on native_delivery_store; it requires no receipt/status mutation grants.
Role installation and production activation require separate review. Existing
legacy settlement permissions are outside this staging role script.

Windows 11 development verification (.NET 10 SDK and Docker Desktop):

```powershell
.\tests\AetherMarketplaceGameServer.Tests\Run-NativeCustodyTests.ps1 -IncludeRegression
```

This starts its own PostgreSQL 17 container on a random loopback port, builds
Release, runs actual OpenMU persistence/custody tests, and uses a second disposable
database for affected native/delivery regressions. It restores environment values
and removes only its own container. `-NoBuild` is valid only for unchanged source
already built in Release. A fresh-context/recreated-service recovery test is not
proof of recovery after an OS-level GameServer kill; practical client and process
termination tests remain separate acceptance checks.
