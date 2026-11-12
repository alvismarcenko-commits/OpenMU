// <copyright file="PostgreSqlReservationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer.Tests;

using System.Text.Json;
using System.Threading;
using AetherMarketplaceDelivery;
using AetherMarketplaceDelivery.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Tests;
using Npgsql;
using NpgsqlTypes;
using Entities = MUnique.OpenMU.DataModel.Entities;
using Configuration = MUnique.OpenMU.DataModel.Configuration;

[TestFixture]
[NonParallelizable]
public sealed class PostgreSqlReservationTests
{
    private NpgsqlDataSource _database = null!;

    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        // Explicit opt-in is required; never load the server's connection configuration.
        if (Environment.GetEnvironmentVariable("AETHER_V414_DISPOSABLE_DB") != "1")
        {
            Assert.Ignore("Requires the disposable v4.14 PostgreSQL container.");
        }

        var password = Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PASSWORD")
            ?? throw new InvalidOperationException("Disposable database password missing.");
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = int.TryParse(Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PORT"), out var port) ? port : 55439, Database = Environment.GetEnvironmentVariable("AETHER_V414_TEST_DB_NAME") ?? "aether_marketplace_test", Username = "aether_test", Password = password, Pooling = false,
        }.ConnectionString;
        this._database = NpgsqlDataSource.Create(connectionString);
        ConnectionConfigurator.Initialize(new TestConnectionSettings(connectionString.Replace("Username=", "User Id=", StringComparison.OrdinalIgnoreCase)));
        await using (var model = new EntityDataContext())
        {
            await model.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        await using var setup = this._database.CreateCommand("""
            CREATE SCHEMA aether_marketplace;
            CREATE TABLE aether_marketplace.delivery_journal (
                delivery_id uuid PRIMARY KEY, character_id uuid NOT NULL,
                account_id bigint NOT NULL, realm text NOT NULL,
                item_instance_id text NOT NULL UNIQUE, state text NOT NULL DEFAULT 'PENDING',
                attempt_count integer NOT NULL DEFAULT 0,
                created_at timestamptz NOT NULL DEFAULT now(), updated_at timestamptz NOT NULL DEFAULT now(),
                delivered_at timestamptz NULL
            );
            """ + await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, "native-item-reservation.sql")).ConfigureAwait(false));
        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (this._database is not null)
        {
            await this._database.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Test]
    public async Task NativeItemSurvivesFreshOpenMuPersistenceContextAsync()
    {
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var configurationContext = provider.CreateNewContext();
        var configuration = configurationContext.CreateNew<Configuration.GameConfiguration>();
        configuration.RecoveryInterval = int.MaxValue;
        var map = configurationContext.CreateNew<Configuration.GameMapDefinition>();
        map.Number = 0;
        map.TerrainData = new byte[ushort.MaxValue + 3];
        configuration.Maps.Add(map);
        var characterClass = configurationContext.CreateNew<Configuration.CharacterClass>();
        characterClass.HomeMap = map;
        configuration.CharacterClasses.Add(characterClass);
        var definition = configurationContext.CreateNew<Configuration.Items.ItemDefinition>();
        definition.Group = 0;
        definition.Number = 1;
        definition.Width = 1;
        definition.Height = 1;
        definition.MaximumItemLevel = 15;
        definition.Durability = 100;
        configuration.Items.Add(definition);
        Assert.That(await configurationContext.SaveChangesAsync().ConfigureAwait(false), Is.True);

        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var context = new GameContext(configuration, provider, initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator());
        initializer.PlugInManager = context.PlugInManager;
        initializer.PathFinderPool = context.PathFinderPool;
        var player = new PersistentTestPlayer(context);
        var account = player.PersistenceContext.CreateNew<Entities.Account>();
        account.LoginName = "v414-test";
        account.PasswordHash = "not-a-login-credential";
        var character = player.PersistenceContext.CreateNew<Entities.Character>();
        character.Name = "V414Hero";
        character.CharacterClass = characterClass;
        character.CurrentMap = map;
        account.Characters.Add(character);
        player.Account = account;
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(character).ConfigureAwait(false);
        await context.AddPlayerAsync(player).ConfigureAwait(false);
        var request = new DeliveryRequest(Guid.NewGuid(), character.Id, 23, "test", Guid.NewGuid().ToString("D"));
        var reservation = new ReservedNativeItem(request,
            new NativeAccountIdentity(23, ((MUnique.OpenMU.Persistence.IIdentifiable)account).Id, character.Id, "test"), new ItemSpecification(0, 1, 3));
        await this.InsertAsync(reservation).ConfigureAwait(false);
        var gateway = new NativeDeliveryGateway(new GameServerNativeDeliveryContextResolver(context, "test", new PostgreSqlReservedNativeItemSource(this._database)));
        var coordinator = new PostgreSqlNativeDeliveryCoordinator(new PostgreSqlDeliveryJournal(this._database), gateway);
        Assert.That(await coordinator.DeliverAsync(request).ConfigureAwait(false), Is.EqualTo(NativeInventoryDeliveryStatus.Delivered));
        player.PersistenceContext.Dispose();

        // A fresh provider and context avoid both the EF change tracker and the configuration cache.
        var reopenedProvider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var reopened = reopenedProvider.CreateNewContext();
        var restored = await reopened.GetByIdAsync<Entities.Character>(character.Id).ConfigureAwait(false);
        Assert.That(restored, Is.Not.Null);
        var item = restored!.Inventory!.Items.Single();
        Assert.That(((MUnique.OpenMU.Persistence.IIdentifiable)item).Id, Is.EqualTo(Guid.Parse(request.ItemInstanceId)));
        Assert.That(item.Level, Is.EqualTo(3));
        Assert.That(await coordinator.DeliverAsync(request).ConfigureAwait(false), Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
        Assert.That(restored.Inventory.Items.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task ConcurrentClaimsAndNewConnectionsPermitOnlyOneAttemptAsync()
    {
        var reservation = NewReservation();
        await this.InsertAsync(reservation).ConfigureAwait(false);
        var journal = new PostgreSqlDeliveryJournal(this._database);
        Assert.That(await journal.ClaimAsync(reservation.Request.DeliveryId).ConfigureAwait(false), Is.True);
        var sources = Enumerable.Range(0, 12).Select(_ => new PostgreSqlReservedNativeItemSource(this._database)).ToArray();
        var results = await Task.WhenAll(sources.Select(s => s.TryBeginAsync(reservation, CancellationToken.None).AsTask())).ConfigureAwait(false);
        Assert.That(results.Count(r => r), Is.EqualTo(1));
        var reopened = new PostgreSqlReservedNativeItemSource(this._database);
        Assert.That(await reopened.ReadAsync(reservation.Request, CancellationToken.None).ConfigureAwait(false), Is.Null);
        Assert.That(await reopened.TryBeginAsync(reservation, CancellationToken.None).ConfigureAwait(false), Is.False);
    }

    [Test]
    public async Task SnapshotRoundTripAndChangedSpecificationCannotClaimAsync()
    {
        var reservation = NewReservation();
        await this.InsertAsync(reservation).ConfigureAwait(false);
        var source = new PostgreSqlReservedNativeItemSource(this._database);
        Assert.That(await source.ReadAsync(reservation.Request, CancellationToken.None).ConfigureAwait(false), Is.EqualTo(reservation));
        Assert.That(await source.TryBeginAsync(reservation, CancellationToken.None).ConfigureAwait(false), Is.False, "Pending journal must not authorize native execution.");
        Assert.That(await new PostgreSqlDeliveryJournal(this._database).ClaimAsync(reservation.Request.DeliveryId).ConfigureAwait(false), Is.True);
        Assert.That(await source.TryBeginAsync(reservation with { Specification = new ItemSpecification(0, 1, 15) }, CancellationToken.None).ConfigureAwait(false), Is.False);
        Assert.That(await source.TryBeginAsync(reservation, CancellationToken.None).ConfigureAwait(false), Is.True);
    }

    private static ReservedNativeItem NewReservation()
    {
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), 17, "test", Guid.NewGuid().ToString("D"));
        return new(request, new NativeAccountIdentity(17, Guid.NewGuid(), request.CharacterId, "test"), new ItemSpecification(0, 1, 3));
    }

    private async Task InsertAsync(ReservedNativeItem reservation)
    {
        await new PostgreSqlDeliveryJournal(this._database).RegisterAsync(reservation.Request).ConfigureAwait(false);
        await using var insert = this._database.CreateCommand("""
            INSERT INTO aether_marketplace.native_item_reservation(delivery_id, item_instance_id, snapshot)
            VALUES (@id, @item, @snapshot)
            """);
        insert.Parameters.AddWithValue("id", reservation.Request.DeliveryId);
        insert.Parameters.AddWithValue("item", Guid.Parse(reservation.Request.ItemInstanceId));
        insert.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(reservation));
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private sealed class PersistentTestPlayer(IGameContext context) : Player(context)
    {
        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer() => new MockViewPlugInContainer();
    }

    private sealed class TestConnectionSettings(string connectionString) : IDatabaseConnectionSettingProvider
    {
        public Task? Initialization => null;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ConnectionSetting GetConnectionSetting<TContextType>()
            where TContextType : DbContext => this.GetConnectionSetting(typeof(TContextType));

        public ConnectionSetting GetConnectionSetting(Type contextType) => new() { ConnectionString = connectionString, DatabaseEngine = DatabaseEngine.Npgsql };
    }
}
