// <copyright file="NativeCustodyDeliveryTests.cs" company="MUnique">
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
public sealed class NativeCustodyDeliveryTests
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
        Assert.That(new NpgsqlConnectionStringBuilder(connectionString).Database, Is.EqualTo("aether_v29_native_disposable"));
        this._database = NpgsqlDataSource.Create(connectionString);
        ConnectionConfigurator.Initialize(new TestConnectionSettings(connectionString.Replace("Username=", "User Id=", StringComparison.OrdinalIgnoreCase)));
        await using (var model = new EntityDataContext())
        {
            await model.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

    }

    [OneTimeTearDown]
    public async Task TearDownAsync()
    {
        if (this._database is not null)
        {
            await this._database.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestCase("normal")]
    [TestCase("offline")]
    [TestCase("full")]
    [TestCase("bad-proof")]
    [TestCase("wrong-account")]
    [TestCase("concurrent")]
    [TestCase("save-failure")]
    [TestCase("plugin")]
    [TestCase("restart-before")]
    [TestCase("lost-ack")]
    [TestCase("acquire-retry")]
    [TestCase("disconnect-during")]
    public async Task OriginalCustodyEntityAndReceiptSurviveFreshContextAsync(string scenario)
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
        var optionDefinition = configurationContext.CreateNew<Configuration.Items.ItemOptionDefinition>();
        configuration.ItemOptions.Add(optionDefinition);
        foreach (var name in new[] { "Excellent", "Luck", "Additional", "Harmony", "Socket" })
        {
            var type = configurationContext.CreateNew<Configuration.Items.ItemOptionType>();
            type.Name = name;
            configuration.ItemOptionTypes.Add(type);
            var option = configurationContext.CreateNew<Configuration.Items.IncreasableItemOption>();
            option.OptionType = type;
            option.Number = optionDefinition.PossibleOptions.Count + 1;
            optionDefinition.PossibleOptions.Add(option);
        }

        var set = configurationContext.CreateNew<Configuration.Items.ItemSetGroup>();
        configuration.ItemSetGroups.Add(set);
        var setItem = configurationContext.CreateNew<Configuration.Items.ItemOfItemSet>();
        setItem.AncientSetDiscriminator = 2;
        setItem.ItemDefinition = definition;
        setItem.ItemSetGroup = set;
        set.Items.Add(setItem);
        Assert.That(await configurationContext.SaveChangesAsync().ConfigureAwait(false), Is.True);

        var initializer = new MapInitializer(configuration, NullLogger<MapInitializer>.Instance, NullDropGenerator.Instance, null);
        var server = new Moq.Mock<GameContext>(configuration, provider, initializer, NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), NullDropGenerator.Instance, new ConfigurationChangeMediator()) { CallBase = true };
        server.As<IGameServerContext>().SetupGet(s => s.Id).Returns(1);
        var context = server.Object;
        initializer.PlugInManager = context.PlugInManager;
        initializer.PathFinderPool = context.PathFinderPool;
        var player = new PersistentTestPlayer(context);
        var account = player.PersistenceContext.CreateNew<Entities.Account>();
        account.LoginName = Guid.NewGuid().ToString("N")[..10];
        account.PasswordHash = "not-a-login-credential";
        var character = player.PersistenceContext.CreateNew<Entities.Character>();
        character.Name = Guid.NewGuid().ToString("N")[..10];
        character.CharacterClass = characterClass;
        character.CurrentMap = map;
        account.Characters.Add(character);
        player.Account = account;
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await player.SetSelectedCharacterAsync(character).ConfigureAwait(false);
        await context.AddPlayerAsync(player).ConfigureAwait(false);
        await using var reset = this._database.CreateCommand("DROP SCHEMA IF EXISTS aether_marketplace CASCADE");
        await reset.ExecuteNonQueryAsync().ConfigureAwait(false);
        var migrations = new[] {
            "marketplace-transaction-v415.sql", "marketplace-reserve-purchase-v415.sql",
            "marketplace-account-binding-fix3.sql", "marketplace-identity-hardening-fix3.sql",
            "marketplace-settlement-ledger-fix3.sql", "marketplace-settlement-integration-fix3.sql",
            "marketplace-ac-wallet-fix3.sql", "marketplace-ac-atomic-settlement-v3-staging.sql",
            "marketplace-payment-proof-v1-staging.sql", "marketplace-ac-lifecycle-v51028-staging.sql",
            "marketplace-native-ownership-fix3.sql", "marketplace-native-delivery-v51029-staging.sql",
            "marketplace-native-delivery-permissions-v29-staging.sql",
        };
        foreach (var migration in migrations)
        {
            await using var apply = this._database.CreateCommand(await File.ReadAllTextAsync(Path.Combine(TestContext.CurrentContext.TestDirectory, migration)).ConfigureAwait(false));
            await apply.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var sellerPlayer = new PersistentTestPlayer(context);
        var nativeSellerAccount = sellerPlayer.PersistenceContext.CreateNew<Entities.Account>();
        nativeSellerAccount.LoginName = Guid.NewGuid().ToString("N")[..10];
        nativeSellerAccount.PasswordHash = "not-a-login-credential";
        var nativeSeller = sellerPlayer.PersistenceContext.CreateNew<Entities.Character>();
        nativeSeller.Name = Guid.NewGuid().ToString("N")[..10];
        nativeSeller.CharacterClass = characterClass;
        nativeSeller.CurrentMap = map;
        nativeSellerAccount.Characters.Add(nativeSeller);
        sellerPlayer.Account = nativeSellerAccount;
        await sellerPlayer.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        await sellerPlayer.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
        await sellerPlayer.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
        await sellerPlayer.SetSelectedCharacterAsync(nativeSeller).ConfigureAwait(false);
        await context.AddPlayerAsync(sellerPlayer).ConfigureAwait(false);
        var escrow = sellerPlayer.PersistenceContext.CreateNew<Entities.ItemStorage>();
        var original = sellerPlayer.PersistenceContext.CreateNew<Entities.Item>();
        original.Definition = definition;
        original.Level = 13;
        original.Durability = 37.25;
        original.HasSkill = true;
        original.SocketCount = 5;
        original.PetExperience = 4321;
        foreach (var option in optionDefinition.PossibleOptions)
        {
            var link = sellerPlayer.PersistenceContext.CreateNew<Entities.ItemOptionLink>();
            link.ItemOption = option;
            link.Level = option.Number + 2;
            link.Index = option.Number - 1;
            original.ItemOptions.Add(link);
        }

        original.ItemSetGroups.Add(setItem);
        var optionIds = original.ItemOptions.Select(o => ((MUnique.OpenMU.Persistence.IIdentifiable)o).Id).Order().ToArray();
        Assert.That(await sellerPlayer.Inventory!.AddItemAsync(original).ConfigureAwait(false), Is.True);
        Assert.That(await sellerPlayer.SaveProgressAsync().ConfigureAwait(false), Is.True);
        Assert.That(await player.SaveProgressAsync().ConfigureAwait(false), Is.True);
        var itemId = ((MUnique.OpenMU.Persistence.IIdentifiable)original).Id;
        var escrowId = ((MUnique.OpenMU.Persistence.IIdentifiable)escrow).Id;
        var accountId = ((MUnique.OpenMU.Persistence.IIdentifiable)account).Id;
        var seller = nativeSeller.Id;
        var sellerAccount = ((MUnique.OpenMU.Persistence.IIdentifiable)nativeSellerAccount).Id;
        var listing = Guid.NewGuid();
        var purchase = Guid.NewGuid();
        await using var seed = this._database.CreateCommand("""
            INSERT INTO aether_marketplace.marketplace_account_identity VALUES ('Origin',101,@account),('Origin',102,@sellerAccount);
            INSERT INTO aether_marketplace.native_account_binding(realm,character_id,openmu_account_id,marketplace_account_id)
            VALUES ('Origin',@buyer,@account,101),('Origin',@seller,@sellerAccount,102);
            INSERT INTO aether_marketplace.ac_wallet(realm,account_id,balance)
            VALUES ('Origin',@account,2000),('Origin',@sellerAccount,0),('Origin','00000000-0000-0000-0000-000000000001',0);
            INSERT INTO aether_marketplace.native_realm_custody VALUES ('Origin',@escrow);
            """);
        seed.Parameters.AddWithValue("account", accountId);
        seed.Parameters.AddWithValue("sellerAccount", sellerAccount);
        seed.Parameters.AddWithValue("buyer", character.Id);
        seed.Parameters.AddWithValue("seller", seller);
        seed.Parameters.AddWithValue("listing", listing);
        seed.Parameters.AddWithValue("item", itemId);
        seed.Parameters.AddWithValue("escrow", escrowId);
        seed.Parameters.AddWithValue("purchase", purchase);
        await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        Assert.That(await player.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(Guid.NewGuid()).ConfigureAwait(false), Is.False, "Different stores cannot share an ownership commit");
        await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(this._database.ConnectionString)
        {
            Password = Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PASSWORD"),
            Options = "-c role=aether_v29_runtime",
        }.ConnectionString);
        await using var forged = runtime.CreateCommand("UPDATE aether_marketplace.native_custody_delivery SET status='COMPLETED'");
        var denied = Assert.ThrowsAsync<PostgresException>(async () => await forged.ExecuteNonQueryAsync().ConfigureAwait(false));
        Assert.That(denied!.SqlState, Is.EqualTo("42501"));
        if (scenario == "acquire-retry")
        {
            await using var intent = runtime.CreateCommand("SELECT aether_marketplace.prepare_native_listing_v29(@listing,@item,@seller,@account,'Origin',1000)");
            intent.Parameters.AddWithValue("listing", listing);
            intent.Parameters.AddWithValue("item", itemId);
            intent.Parameters.AddWithValue("seller", seller);
            intent.Parameters.AddWithValue("account", sellerAccount);
            Assert.That(await intent.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo(escrowId));
        }

        Assert.That(await new GameServerNativeCustody(runtime, "Origin").ListAsync(sellerPlayer, listing, itemId, 1000).ConfigureAwait(false), Is.True);
        Assert.That(sellerPlayer.Inventory.Items.Any(i => ((MUnique.OpenMU.Persistence.IIdentifiable)i).Id == itemId), Is.False);
        Assert.That(await new GameServerNativeCustody(runtime, "Origin").ListAsync(sellerPlayer, Guid.NewGuid(), itemId, 1000).ConfigureAwait(false), Is.False, "Seller cannot reuse custody item");
        await using var reservePurchase = this._database.CreateCommand("SELECT aether_marketplace.reserve_purchase(@purchase,@listing,@buyer); SELECT aether_marketplace.prepare_settlement(@purchase)");
        reservePurchase.Parameters.AddWithValue("purchase", purchase);
        reservePurchase.Parameters.AddWithValue("listing", listing);
        reservePurchase.Parameters.AddWithValue("buyer", character.Id);
        await reservePurchase.ExecuteNonQueryAsync().ConfigureAwait(false);
        var beforePayment = new GameServerCustodyDelivery(runtime, context, "Origin");
        Assert.That(await beforePayment.TryReserveAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.False, "Reservation is not payment proof");
        var settlement = new PostgreSqlAuthorizedAcSettlement(this._database, "Origin");
        Assert.That(await settlement.SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("SETTLED"));
        var native = new GameServerCustodyDelivery(runtime, context, "Origin");
        var coordinator = new SafeDeliveryCoordinator(native, native);
        Assert.That(await new GameServerCustodyDelivery(runtime, context, "Legacy").TryReserveAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.False);
        Assert.That(await native.TryReserveAsync(purchase, Guid.NewGuid(), itemId.ToString("D")).ConfigureAwait(false), Is.False);
        if (scenario == "restart-before")
        {
            Assert.That(await native.TryReserveAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.True);
            await context.RemovePlayerAsync(player).ConfigureAwait(false);
            await player.DisposeAsync().ConfigureAwait(false);
            player = new PersistentTestPlayer(context);
            var reconnectAccount = await player.PersistenceContext.GetByIdAsync<Entities.Account>(accountId).ConfigureAwait(false);
            player.Account = reconnectAccount;
            await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            await player.SetSelectedCharacterAsync(reconnectAccount!.Characters.Single()).ConfigureAwait(false);
            await context.AddPlayerAsync(player).ConfigureAwait(false);
            native = new GameServerCustodyDelivery(runtime, context, "Origin");
            coordinator = new SafeDeliveryCoordinator(native, native);
        }
        if (scenario == "offline")
        {
            await context.RemovePlayerAsync(player).ConfigureAwait(false);
            Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            await context.AddPlayerAsync(player).ConfigureAwait(false);
        }

        if (scenario == "wrong-account")
        {
            player.Account = new MUnique.OpenMU.Persistence.BasicModel.Account { Id = Guid.NewGuid() };
            Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            player.Account = account;
        }

        if (scenario == "bad-proof")
        {
            await using var corrupt = this._database.CreateCommand("UPDATE aether_marketplace.castle_treasury_ledger SET amount=51");
            await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false);
            Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            corrupt.CommandText = "UPDATE aether_marketplace.castle_treasury_ledger SET amount=50";
            await corrupt.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        if (scenario == "full")
        {
            var fillers = new List<Entities.Item>();
            while (player.Inventory!.CheckInvSpace(original) is { } slot)
            {
                var filler = player.PersistenceContext.CreateNew<Entities.Item>();
                filler.Definition = definition;
                Assert.That(await player.Inventory.AddItemAsync(slot, filler).ConfigureAwait(false), Is.True);
                fillers.Add(filler);
            }

            Assert.That(await player.SaveProgressAsync().ConfigureAwait(false), Is.True);
            Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            Assert.That(escrow.Items.Contains(original), Is.True);
            await player.Inventory.RemoveItemAsync(fillers[0]).ConfigureAwait(false);
            await player.SaveProgressAsync().ConfigureAwait(false);
        }

        if (scenario == "save-failure")
        {
            await using var inject = this._database.CreateCommand("""
                CREATE FUNCTION public.fail_native_save() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'injected native save failure'; END $$;
                CREATE TRIGGER fail_native_save AFTER UPDATE ON data."Item"
                FOR EACH ROW EXECUTE FUNCTION public.fail_native_save();
                """);
            await inject.ExecuteNonQueryAsync().ConfigureAwait(false);
            Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            Assert.That((await native.FindAsync(purchase).ConfigureAwait(false))?.Status, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired));
            await using var unpaidDelivery = this._database.CreateCommand("SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=@id");
            unpaidDelivery.Parameters.AddWithValue("id", purchase);
            Assert.That(await unpaidDelivery.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo("DELIVERY_PENDING"));
            inject.CommandText = "DROP TRIGGER fail_native_save ON data.\"Item\"; DROP FUNCTION public.fail_native_save()";
            await inject.ExecuteNonQueryAsync().ConfigureAwait(false);
            await context.RemovePlayerAsync(player).ConfigureAwait(false);
            player.PersistenceContext.Dispose();
            player = new PersistentTestPlayer(context);
            var restoredAccount = await player.PersistenceContext.GetByIdAsync<Entities.Account>(accountId).ConfigureAwait(false);
            player.Account = restoredAccount;
            await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
            await player.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            await player.SetSelectedCharacterAsync(restoredAccount!.Characters.Single()).ConfigureAwait(false);
            await context.AddPlayerAsync(player).ConfigureAwait(false);
            Assert.That(player.IsConnected, Is.True, "Reconnected session");
            Assert.That(player.PlayerState.CurrentState, Is.EqualTo(PlayerState.EnteredWorld));
            Assert.That(player.SelectedCharacter!.Id, Is.EqualTo(character.Id));
            var reloadedEscrow = await player.PersistenceContext.LoadCustodyStorageAsync(escrowId).ConfigureAwait(false);
            await player.PersistenceContext.GetByIdAsync<Entities.Item>(itemId).ConfigureAwait(false);
            await using var physical = this._database.CreateCommand("SELECT \"ItemStorageId\"::text FROM data.\"Item\" WHERE \"Id\"=@item");
            physical.Parameters.AddWithValue("item", itemId);
            TestContext.WriteLine($"Physical storage: {await physical.ExecuteScalarAsync().ConfigureAwait(false)}, expected escrow {escrowId}");
            Assert.That(reloadedEscrow!.Items.Count, Is.EqualTo(1), "Original must remain in custody after rollback");
            Assert.That(reloadedEscrow.Items.Single().Definition, Is.Not.Null, "Recovered definition");
        }

        if (scenario == "disconnect-during")
        {
            await using var delay = this._database.CreateCommand("""
                CREATE FUNCTION public.delay_native_save() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_sleep(1); RETURN NEW; END $$;
                CREATE TRIGGER delay_native_save AFTER UPDATE ON data."Item"
                FOR EACH ROW EXECUTE FUNCTION public.delay_native_save();
                """);
            await delay.ExecuteNonQueryAsync().ConfigureAwait(false);
            var delivery = coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).AsTask();
            var saving = false;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await using var observe = this._database.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep')");
                saving = await observe.ExecuteScalarAsync().ConfigureAwait(false) is true;
                if (saving) { break; }
                await Task.Delay(20).ConfigureAwait(false);
            }

            Assert.That(saving, Is.True, "Observe the native save before changing session state");
            Assert.That((await native.FindAsync(purchase).ConfigureAwait(false))?.Status, Is.EqualTo(SafeDeliveryStatus.ReconciliationRequired), "Uncommitted ownership is not completion");
            await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
            Assert.That(await delivery.ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.Delivered));
            delay.CommandText = "DROP TRIGGER delay_native_save ON data.\"Item\"; DROP FUNCTION public.delay_native_save()";
            await delay.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        else if (scenario == "lost-ack")
        {
            Assert.That(await native.TryReserveAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.True);
            // Persist without the coordinator acknowledgement, then discard the context.
            var saved = await MUnique.OpenMU.GameLogic.Marketplace.MarketplaceInventoryDelivery.TransferExistingItemAsync(player, character.Id, escrowId, itemId, accountId).ConfigureAwait(false);
            Assert.That(saved.Status, Is.EqualTo(MUnique.OpenMU.GameLogic.Marketplace.MarketplaceNativeStatus.Delivered));
            await context.RemovePlayerAsync(player).ConfigureAwait(false);
            await player.DisposeAsync().ConfigureAwait(false);
            Assert.That(await new SafeDeliveryCoordinator(new GameServerCustodyDelivery(runtime, context, "Origin"), new GameServerCustodyDelivery(runtime, context, "Origin")).DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.AlreadyDelivered));
        }
        else if (scenario == "plugin")
        {
            const string variable = "AETHER_V29_PLUGIN_TEST_CONNECTION";
            var previous = Environment.GetEnvironmentVariable(variable);
            try
            {
                Environment.SetEnvironmentVariable(variable, new NpgsqlConnectionStringBuilder(this._database.ConnectionString)
                {
                    Password = Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PASSWORD"),
                }.ConnectionString);
                var plugin = new NativeDeliveryRecoveryPlugIn
                {
                    Configuration = new NativeDeliveryPlugInConfiguration
                    {
                        Realms = new() { [1] = "Origin" },
                        ConnectionVariables = new() { ["Origin"] = variable },
                    },
                };
                Assert.That(PlugInManager.DiscoverPlugInTypes(typeof(NativeDeliveryRecoveryPlugIn).Assembly), Does.Contain(typeof(NativeDeliveryRecoveryPlugIn)));
                await plugin.ExecuteTaskAsync(context).ConfigureAwait(false);
                Assert.That((await native.FindAsync(purchase).ConfigureAwait(false))?.Status, Is.EqualTo(SafeDeliveryStatus.Delivered));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous);
            }
        }
        else if (scenario == "concurrent")
        {
            var other = new GameServerCustodyDelivery(this._database, context, "Origin");
            var outcomes = await Task.WhenAll(coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).AsTask(),
                new SafeDeliveryCoordinator(other, other).DeliverAsync(purchase, character.Id, itemId.ToString("D")).AsTask()).ConfigureAwait(false);
            Assert.That(outcomes.Count(s => s == SafeDeliveryStatus.Delivered), Is.EqualTo(1));
        }
        else
        {
        Assert.That(await coordinator.DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.Delivered));
        }
        Assert.That(await new SafeDeliveryCoordinator(new GameServerCustodyDelivery(this._database, context, "Origin"), new GameServerCustodyDelivery(this._database, context, "Origin")).DeliverAsync(purchase, character.Id, itemId.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.AlreadyDelivered));
        if (scenario is not "lost-ack" and not "disconnect-during")
        {
            Assert.That(await settlement.SettleAsync(player, purchase).ConfigureAwait(false), Is.EqualTo("ALREADY_SETTLED"));
        }
        player.PersistenceContext.Dispose();
        var reopenedProvider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var reopened = reopenedProvider.CreateNewContext();
        var restored = await reopened.GetByIdAsync<Entities.Character>(character.Id).ConfigureAwait(false);
        var restoredItem = restored!.Inventory!.Items.Single(i => ((MUnique.OpenMU.Persistence.IIdentifiable)i).Id == itemId);
        Assert.Multiple(() =>
        {
            Assert.That(((MUnique.OpenMU.Persistence.IIdentifiable)restoredItem).Id, Is.EqualTo(itemId));
            Assert.That(restoredItem.Level, Is.EqualTo(13));
            Assert.That(restoredItem.Durability, Is.EqualTo(37.25));
            Assert.That(restoredItem.HasSkill, Is.True);
            Assert.That(restoredItem.SocketCount, Is.EqualTo(5));
            Assert.That(restoredItem.PetExperience, Is.EqualTo(4321));
            Assert.That(restoredItem.ItemOptions.Select(o => ((MUnique.OpenMU.Persistence.IIdentifiable)o).Id).Order(), Is.EqualTo(optionIds));
            Assert.That(restoredItem.ItemOptions.OrderBy(o => o.Index).Select(o => (o.Level, o.Index)), Is.EqualTo(new[] { (3,0),(4,1),(5,2),(6,3),(7,4) }));
            Assert.That(restoredItem.ItemSetGroups.Single().AncientSetDiscriminator, Is.EqualTo(2));
        });
        var restoredEscrow = await reopened.GetByIdAsync<Entities.ItemStorage>(escrowId).ConfigureAwait(false);
        Assert.That(restoredEscrow!.Items, Is.Empty);
        await using var verify = this._database.CreateCommand("SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=@id");
        verify.Parameters.AddWithValue("id", purchase);
        Assert.That(await verify.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo("COMPLETED"));
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
