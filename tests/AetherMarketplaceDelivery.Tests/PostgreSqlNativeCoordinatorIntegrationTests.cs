// <copyright file="PostgreSqlNativeCoordinatorIntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.Tests;

using AetherMarketplaceDelivery.PostgreSql;
using Npgsql;
using NUnit.Framework;

[TestFixture]
[NonParallelizable]
public sealed class PostgreSqlNativeCoordinatorIntegrationTests
{
    private static async Task<NpgsqlDataSource> CreateDataSourceAsync()
    {
        var password = Environment.GetEnvironmentVariable(
            "AETHER_MARKETPLACE_TEST_DB_PASSWORD");

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Test database password missing.");
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = int.TryParse(Environment.GetEnvironmentVariable(
                "AETHER_MARKETPLACE_TEST_DB_PORT"), out var testPort) ? testPort : 55432,
            Database = "aether_marketplace_test",
            Username = "aether_test",
            Password = password,
            Pooling = false,
            Timeout = 5,
        };

        var source = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        return source;
    }

    private static async Task CleanupAsync(
        NpgsqlDataSource source, Guid id)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM aether_marketplace.delivery_journal WHERE delivery_id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task ConfirmedDeliveryIsDurableAndNotRepeated()
    {
        await using var source = await CreateDataSourceAsync();
        var request = NewRequest();
        var native = new FakeNative(NativeInventoryDeliveryStatus.Delivered);
        var journal = new PostgreSqlDeliveryJournal(source);
        var coordinator = new PostgreSqlNativeDeliveryCoordinator(journal, native);

        try
        {
            var first = await coordinator.DeliverAsync(request);
            var second = await new PostgreSqlNativeDeliveryCoordinator(
                new PostgreSqlDeliveryJournal(source), native).DeliverAsync(request);

            Assert.That(first, Is.EqualTo(NativeInventoryDeliveryStatus.Delivered));
            Assert.That(second, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
            Assert.That(native.Calls, Is.EqualTo(1));

            var state = await ReadStateAsync(source, request.DeliveryId);
            Assert.That(state, Is.EqualTo("DELIVERED"));
        }
        finally
        {
            await CleanupAsync(source, request.DeliveryId);
        }
    }

    [Test]
    public async Task UncertainDeliveryIsNotRetried()
    {
        await using var source = await CreateDataSourceAsync();
        var request = NewRequest();
        var native = new FakeNative(NativeInventoryDeliveryStatus.ReconciliationRequired);
        var coordinator = new PostgreSqlNativeDeliveryCoordinator(
            new PostgreSqlDeliveryJournal(source), native);

        try
        {
            var first = await coordinator.DeliverAsync(request);
            var second = await coordinator.DeliverAsync(request);

            Assert.That(first, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
            Assert.That(second, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(await ReadStateAsync(source, request.DeliveryId),
                Is.EqualTo("RECONCILIATION_REQUIRED"));
        }
        finally
        {
            await CleanupAsync(source, request.DeliveryId);
        }
    }

    [Test]
    public async Task InventoryFullDoesNotDeliverItem()
    {
        await using var source = await CreateDataSourceAsync();
        var request = NewRequest();
        var native = new FakeNative(NativeInventoryDeliveryStatus.InventoryFull);
        var coordinator = new PostgreSqlNativeDeliveryCoordinator(
            new PostgreSqlDeliveryJournal(source), native);

        try
        {
            var result = await coordinator.DeliverAsync(request);

            Assert.That(result, Is.EqualTo(NativeInventoryDeliveryStatus.InventoryFull));
            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(await ReadStateAsync(source, request.DeliveryId),
                Is.EqualTo("INVENTORY_FULL"));
        }
        finally
        {
            await CleanupAsync(source, request.DeliveryId);
        }
    }

    [Test]
    public async Task ConcurrentRequestsOnlyInvokeNativeOnce()
    {
        await using var source = await CreateDataSourceAsync();
        var request = NewRequest();
        var native = new FakeNative(NativeInventoryDeliveryStatus.Delivered);

        try
        {
            var tasks = Enumerable.Range(0, 10).Select(_ =>
                new PostgreSqlNativeDeliveryCoordinator(
                    new PostgreSqlDeliveryJournal(source), native)
                    .DeliverAsync(request));

            await Task.WhenAll(tasks);

            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(await ReadStateAsync(source, request.DeliveryId),
                Is.EqualTo("DELIVERED"));
        }
        finally
        {
            await CleanupAsync(source, request.DeliveryId);
        }
    }

    [Test]
    public async Task ConflictingDeliveryIdentityIsRejected()
    {
        await using var source = await CreateDataSourceAsync();
        var request = NewRequest();
        var native = new FakeNative(NativeInventoryDeliveryStatus.Delivered);
        var coordinator = new PostgreSqlNativeDeliveryCoordinator(
            new PostgreSqlDeliveryJournal(source), native);

        try
        {
            await coordinator.DeliverAsync(request);

            var conflicting = request with { CharacterId = Guid.NewGuid() };

            Assert.ThrowsAsync<InvalidOperationException>(
                async () => await coordinator.DeliverAsync(conflicting));

            Assert.That(native.Calls, Is.EqualTo(1));
            Assert.That(await ReadStateAsync(source, request.DeliveryId),
                Is.EqualTo("DELIVERED"));
        }
        finally
        {
            await CleanupAsync(source, request.DeliveryId);
        }
    }
    private static DeliveryRequest NewRequest() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 1001, "Ascension",
            Guid.NewGuid().ToString("D"));

    private static async Task<string?> ReadStateAsync(
        NpgsqlDataSource source, Guid id)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT state FROM aether_marketplace.delivery_journal WHERE delivery_id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class FakeNative(
        NativeInventoryDeliveryStatus status) : INativeInventoryDeliveryGateway
    {
        public int Calls { get; private set; }

        public Task<NativeInventoryDeliveryResult> DeliverAsync(
            DeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;
            var itemId = status == NativeInventoryDeliveryStatus.Delivered
                ? request.ItemInstanceId : null;
            return Task.FromResult(new NativeInventoryDeliveryResult(status, itemId));
        }
    }
}
