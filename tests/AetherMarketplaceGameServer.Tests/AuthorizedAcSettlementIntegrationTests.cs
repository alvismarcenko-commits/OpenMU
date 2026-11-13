// <copyright file="AuthorizedAcSettlementIntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer.Tests;

using MUnique.OpenMU.Tests;
using Npgsql;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>Exercises the live-player settlement adapter against disposable PostgreSQL.</summary>
[TestFixture]
public sealed class AuthorizedAcSettlementIntegrationTests
{
    /// <summary>Checks identity rejection, settlement and replay through the real adapter.</summary>
    [Test]
    public async Task LiveIdentitySettlementAndReplayAsync()
    {
        var connection = Environment.GetEnvironmentVariable("AETHER_V51028_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            Assert.Ignore("Run Run-AcLifecycleTests.ps1 for a fresh isolated database.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connection);
        Assert.That(builder.Database, Is.EqualTo("aether_v51028_disposable"));
        Assert.That(builder.Host, Is.EqualTo("127.0.0.1"));
        await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var setup = source.CreateCommand("""
            SELECT p.purchase_id, p.buyer_character_id, b.openmu_account_id
            FROM aether_marketplace.purchase_reservation p
            JOIN aether_marketplace.native_account_binding b
              ON b.realm=p.realm AND b.character_id=p.buyer_character_id
            WHERE p.status='RESERVED' AND p.price=1001
            """);
        Guid purchase;
        Guid character;
        Guid account;
        await using (var reader = await setup.ExecuteReaderAsync().ConfigureAwait(false))
        {
            Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.True);
            purchase = reader.GetGuid(0);
            character = reader.GetGuid(1);
            account = reader.GetGuid(2);
        }

        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Account = new BasicModel.Account { Id = account };
        player.SelectedCharacter!.Id = character;
        var adapter = new PostgreSqlAuthorizedAcSettlement(source, "Origin");
        Assert.That(await adapter.SettleAsync(null, purchase).ConfigureAwait(false), Is.EqualTo("UNAUTHORIZED"));
        Assert.That(await new PostgreSqlAuthorizedAcSettlement(source, "Legacy").SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("UNAUTHORIZED"));
        Assert.That(await adapter.SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("INSUFFICIENT_FUNDS"));
        await using var fund = source.CreateCommand("UPDATE aether_marketplace.ac_wallet SET balance=balance+1 WHERE realm='Origin' AND account_id=@account");
        fund.Parameters.AddWithValue("account", account);
        Assert.That(await fund.ExecuteNonQueryAsync().ConfigureAwait(false), Is.EqualTo(1));
        Assert.That(await adapter.SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("SETTLED"));
        Assert.That(await adapter.SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("ALREADY_SETTLED"));

        var listing = Guid.NewGuid();
        var concurrentPurchase = Guid.NewGuid();
        await using var prepare = source.CreateCommand("""
            UPDATE aether_marketplace.ac_wallet SET balance=2000
            WHERE realm='Origin' AND account_id=@account;
            INSERT INTO aether_marketplace.player_listing
                (listing_id,seller_character_id,realm,item_instance_id,currency,price)
            SELECT @listing,seller_character_id,realm,@item,currency,1000
            FROM aether_marketplace.purchase_reservation WHERE purchase_id=@previous;
            SELECT aether_marketplace.reserve_purchase(@purchase,@listing,@buyer);
            SELECT aether_marketplace.prepare_settlement(@purchase);
            """);
        prepare.Parameters.AddWithValue("account", account);
        prepare.Parameters.AddWithValue("listing", listing);
        prepare.Parameters.AddWithValue("item", Guid.NewGuid());
        prepare.Parameters.AddWithValue("previous", purchase);
        prepare.Parameters.AddWithValue("purchase", concurrentPurchase);
        prepare.Parameters.AddWithValue("buyer", character);
        await prepare.ExecuteNonQueryAsync().ConfigureAwait(false);

        // Separate live player instances avoid serializing calls on one process-local lock.
        var players = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            var session = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
            session.Account = new BasicModel.Account { Id = account };
            session.SelectedCharacter!.Id = character;
            return session;
        })).ConfigureAwait(false);
        var outcomes = await Task.WhenAll(players.Select(session => adapter.SettleAsync(session, concurrentPurchase))).ConfigureAwait(false);
        Assert.That(outcomes.Count(result => result == "SETTLED"), Is.EqualTo(1));
        Assert.That(outcomes.Count(result => result == "ALREADY_SETTLED"), Is.EqualTo(9));
        await using var verify = source.CreateCommand("""
            SELECT balance FROM aether_marketplace.ac_wallet WHERE realm='Origin' AND account_id=@account
            """);
        verify.Parameters.AddWithValue("account", account);
        Assert.That(await verify.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(1000L));

        var contestedListing = Guid.NewGuid();
        await using var createListing = source.CreateCommand("""
            INSERT INTO aether_marketplace.player_listing
                (listing_id,seller_character_id,realm,item_instance_id,currency,price)
            SELECT @listing,seller_character_id,realm,@item,currency,1000
            FROM aether_marketplace.purchase_reservation WHERE purchase_id=@previous
            """);
        createListing.Parameters.AddWithValue("listing", contestedListing);
        createListing.Parameters.AddWithValue("item", Guid.NewGuid());
        createListing.Parameters.AddWithValue("previous", purchase);
        await createListing.ExecuteNonQueryAsync().ConfigureAwait(false);
        var reservations = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var reserve = source.CreateCommand("SELECT aether_marketplace.reserve_purchase(@purchase,@listing,@buyer)");
            reserve.Parameters.AddWithValue("purchase", Guid.NewGuid());
            reserve.Parameters.AddWithValue("listing", contestedListing);
            reserve.Parameters.AddWithValue("buyer", Guid.NewGuid());
            return await reserve.ExecuteScalarAsync().ConfigureAwait(false) as string;
        })).ConfigureAwait(false);
        Assert.That(reservations.Count(result => result == "RESERVED"), Is.EqualTo(1));
        Assert.That(reservations.Count(result => result == "LISTING_NOT_ACTIVE"), Is.EqualTo(9));
    }
}
