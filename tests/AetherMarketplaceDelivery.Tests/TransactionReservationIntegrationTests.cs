// <copyright file="TransactionReservationIntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using NUnit.Framework;

[TestFixture]
[NonParallelizable]
public sealed class TransactionReservationIntegrationTests
{
    private NpgsqlDataSource dataSource = null!;

    [OneTimeSetUp]
    public void SetUp()
    {
        if (Environment.GetEnvironmentVariable("AETHER_V415_TRANSACTION_TEST") != "1")
        {
            Assert.Ignore("Explicit v4.15 disposable test opt-in required.");
        }

        var password = Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PASSWORD")
            ?? throw new InvalidOperationException("Test password missing.");

        var database = Environment.GetEnvironmentVariable("AETHER_V415_TEST_DB_NAME")
            ?? throw new InvalidOperationException("Test database name missing.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PORT"), out var port) ? port : 55439,
            Database = database,
            Username = "aether_test",
            Password = password,
        };

        this.dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (this.dataSource is not null)
        {
            await this.dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task FirstPurchaseAndRetryAreIdempotentAsync()
    {
        var listing = await this.CreateListingAsync().ConfigureAwait(false);
        var request = new PlayerPurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid());
        var coordinator = new PostgreSqlTransactionReservationCoordinator(this.dataSource);

        Assert.That(await coordinator.ReservePurchaseAsync(request, CancellationToken.None),
            Is.EqualTo(PurchaseReservationResult.Reserved));

        Assert.That(await coordinator.ReservePurchaseAsync(request, CancellationToken.None),
            Is.EqualTo(PurchaseReservationResult.AlreadyReserved));
    }

    [Test]
    public async Task DifferentBuyerCannotReusePurchaseIdAsync()
    {
        var listing = await this.CreateListingAsync().ConfigureAwait(false);
        var coordinator = new PostgreSqlTransactionReservationCoordinator(this.dataSource);
        var request = new PlayerPurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid());

        Assert.That(await coordinator.ReservePurchaseAsync(request, CancellationToken.None),
            Is.EqualTo(PurchaseReservationResult.Reserved));

        Assert.That(await coordinator.ReservePurchaseAsync(
            request with { BuyerCharacterId = Guid.NewGuid() }, CancellationToken.None),
            Is.EqualTo(PurchaseReservationResult.PurchaseIdConflict));
    }

    [Test]
    public async Task ConcurrentBuyersCannotReserveSameListingAsync()
    {
        var listing = await this.CreateListingAsync().ConfigureAwait(false);
        var coordinator = new PostgreSqlTransactionReservationCoordinator(this.dataSource);

        var tasks = Enumerable.Range(0, 2)
            .Select(_ => coordinator.ReservePurchaseAsync(
                new PlayerPurchaseRequest(Guid.NewGuid(), listing, Guid.NewGuid()),
                CancellationToken.None).AsTask());

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.That(results.Count(x => x == PurchaseReservationResult.Reserved), Is.EqualTo(1));
        Assert.That(results.Count(x => x == PurchaseReservationResult.ListingNotActive), Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentListingsRejectReusedPurchaseIdAsync()
    {
        var first = await this.CreateListingAsync().ConfigureAwait(false);
        var second = await this.CreateListingAsync().ConfigureAwait(false);
        var purchaseId = Guid.NewGuid();
        var coordinator = new PostgreSqlTransactionReservationCoordinator(this.dataSource);

        var tasks = new[]
        {
            coordinator.ReservePurchaseAsync(
                new PlayerPurchaseRequest(purchaseId, first, Guid.NewGuid()),
                CancellationToken.None).AsTask(),
            coordinator.ReservePurchaseAsync(
                new PlayerPurchaseRequest(purchaseId, second, Guid.NewGuid()),
                CancellationToken.None).AsTask(),
        };

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        Assert.That(results.Count(x => x == PurchaseReservationResult.Reserved), Is.EqualTo(1));
        Assert.That(results.Count(x => x == PurchaseReservationResult.PurchaseIdConflict), Is.EqualTo(1));
    }

    private async Task<Guid> CreateListingAsync()
    {
        var listingId = Guid.NewGuid();

        await using var command = this.dataSource.CreateCommand("""
            INSERT INTO aether_marketplace.player_listing
                (listing_id, seller_character_id, realm, item_instance_id, currency, price, status)
            VALUES
                (@listing, @seller, 'Ascension', @item, 'AetherCoins', 1000, 'ACTIVE');
            """);

        command.Parameters.AddWithValue("listing", listingId);
        command.Parameters.AddWithValue("seller", Guid.NewGuid());
        command.Parameters.AddWithValue("item", Guid.NewGuid());

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return listingId;
    }
}