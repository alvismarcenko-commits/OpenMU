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
