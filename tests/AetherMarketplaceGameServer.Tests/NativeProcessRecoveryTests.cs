// <copyright file="NativeProcessRecoveryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer.Tests;

using System.Text.Json;
using AetherMarketplaceDelivery;
using AetherMarketplaceDelivery.PostgreSql;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.PlugIns;
using Npgsql;
using Configuration = MUnique.OpenMU.DataModel.Configuration;
using Entities = MUnique.OpenMU.DataModel.Entities;

public sealed partial class NativeCustodyDeliveryTests
{
    [Test]
    [Explicit("Only the isolated process-termination runner may invoke this worker.")]
    public Task ProcessCrashWorkerAsync() => this.OriginalCustodyEntityAndReceiptSurviveFreshContextAsync("process-crash");

    [Test]
    [Explicit("Only the isolated process-termination runner may invoke this recovery.")]
    public async Task ProcessCrashRecoveryAsync()
    {
        Assert.That(Environment.GetEnvironmentVariable("AETHER_V30_PROCESS_PROBE"), Is.EqualTo("1"));
        var info = JsonSerializer.Deserialize<CrashInfo>(await File.ReadAllTextAsync(ProbePath("state.json")).ConfigureAwait(false))!;
        var provider = new PersistenceContextProvider(NullLoggerFactory.Instance, null);
        using var configuration = provider.CreateNewContext();
        var definition = await configuration.GetByIdAsync<Configuration.GameServerDefinition>(info.Definition).ConfigureAwait(false);
        await using var server = new MUnique.OpenMU.GameServer.GameServer(definition!, Moq.Mock.Of<IGuildServer>(), Moq.Mock.Of<IEventPublisher>(),
            Moq.Mock.Of<ILoginServer>(), provider, Moq.Mock.Of<IFriendServer>(), NullLoggerFactory.Instance,
            new PlugInManager(null, NullLoggerFactory.Instance, null, null), new ConfigurationChangeMediator());
        await server.StartAsync().ConfigureAwait(false);
        Assert.That(server.ServerState.ToString(), Is.EqualTo("Started"));
        var context = (GameContext)server.Context;
        var buyer = await LoginAsync(info.BuyerAccount).ConfigureAwait(false);
        var seller = await LoginAsync(info.SellerAccount).ConfigureAwait(false);
        var custody = new GameServerNativeCustody(this._database, "Origin");
        await using var status = this._database.CreateCommand("SELECT status FROM aether_marketplace.player_listing WHERE listing_id=@listing");
        status.Parameters.AddWithValue("listing", info.Listing);
        if (await status.ExecuteScalarAsync().ConfigureAwait(false) is null or "DRAFT")
        {
            Assert.That(await custody.ListAsync(seller, info.Listing, info.Item, 1000).ConfigureAwait(false), Is.True);
        }

        await ReserveAsync(this._database, info.Purchase, info.Listing, buyer).ConfigureAwait(false);
        Assert.That(await new PostgreSqlAuthorizedAcSettlement(this._database, "Origin", true).SettleAsync(buyer, info.Purchase).ConfigureAwait(false), Is.AnyOf("SETTLED", "ALREADY_SETTLED"));
        var delivery = new GameServerCustodyDelivery(this._database, context, "Origin");
        var coordinator = new SafeDeliveryCoordinator(delivery, delivery);
        Assert.That(await coordinator.DeliverAsync(info.Purchase, buyer.SelectedCharacter!.Id, info.Item.ToString("D")).ConfigureAwait(false), Is.AnyOf(SafeDeliveryStatus.Delivered, SafeDeliveryStatus.AlreadyDelivered));
        Assert.That(await coordinator.DeliverAsync(info.Purchase, buyer.SelectedCharacter.Id, info.Item.ToString("D")).ConfigureAwait(false), Is.EqualTo(SafeDeliveryStatus.AlreadyDelivered));
        using var fresh = provider.CreateNewContext();
        var recovered = await fresh.GetByIdAsync<Entities.Character>(buyer.SelectedCharacter.Id).ConfigureAwait(false);
        var item = recovered!.Inventory!.Items.Single(i => ((IIdentifiable)i).Id == info.Item);
        Assert.Multiple(() =>
        {
            Assert.That(item.Level, Is.EqualTo(13));
            Assert.That(item.Durability, Is.EqualTo(37.25));
            Assert.That(item.SocketCount, Is.EqualTo(5));
            Assert.That(item.ItemOptions.Count, Is.EqualTo(5));
            Assert.That(item.ItemSetGroups.Single().AncientSetDiscriminator, Is.EqualTo(2));
        });
        await using var verify = this._database.CreateCommand("""
            SELECT (SELECT count(*) FROM data."Item" WHERE "Id"=@item),
              (SELECT status FROM aether_marketplace.purchase_reservation WHERE purchase_id=@purchase),
              (SELECT count(*) FROM aether_marketplace.ac_wallet_ledger WHERE reference LIKE 'ac-marketplace:'||@purchase::text||':%'),
              (SELECT sum(amount) FROM aether_marketplace.castle_treasury_ledger WHERE purchase_id=@purchase)
            """);
        verify.Parameters.AddWithValue("item", info.Item);
        verify.Parameters.AddWithValue("purchase", info.Purchase);
        await using var reader = await verify.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.True);
        Assert.That(reader.GetInt64(0), Is.EqualTo(1));
        Assert.That(reader.GetString(1), Is.EqualTo("COMPLETED"));
        Assert.That(reader.GetInt64(2), Is.EqualTo(3));
        Assert.That(reader.GetInt64(3), Is.EqualTo(50));

        async Task<Player> LoginAsync(Guid accountId)
        {
            var actor = new PersistentTestPlayer(context);
            actor.Account = await actor.PersistenceContext.GetByIdAsync<Entities.Account>(accountId).ConfigureAwait(false);
            await actor.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
            await actor.PlayerState.TryAdvanceToAsync(PlayerState.Authenticated).ConfigureAwait(false);
            await actor.PlayerState.TryAdvanceToAsync(PlayerState.CharacterSelection).ConfigureAwait(false);
            await actor.SetSelectedCharacterAsync(actor.Account!.Characters.Single()).ConfigureAwait(false);
            await context.AddPlayerAsync(actor).ConfigureAwait(false);
            return actor;
        }
    }

    private static string ProbePath(string filename) => Path.Combine(Environment.GetEnvironmentVariable("AETHER_V30_PROBE_DIRECTORY") ?? throw new InvalidOperationException("Probe directory required."), filename);

    private static async Task ReserveAsync(NpgsqlDataSource source, Guid purchase, Guid listing, Player buyer)
    {
        await using var reserve = source.CreateCommand("SELECT aether_marketplace.reserve_authorized_purchase_v30(@purchase,@listing,@buyer,@account,'Origin')");
        reserve.Parameters.AddWithValue("purchase", purchase);
        reserve.Parameters.AddWithValue("listing", listing);
        reserve.Parameters.AddWithValue("buyer", buyer.SelectedCharacter!.Id);
        reserve.Parameters.AddWithValue("account", ((IIdentifiable)buyer.Account!).Id);
        Assert.That(await reserve.ExecuteScalarAsync().ConfigureAwait(false), Is.AnyOf("RESERVED", "ALREADY_RESERVED"));
    }

    private async Task ProcessCrashProbeAsync(GameContext context, Player buyer, Player seller, Entities.Item original, Guid listing, Guid purchase, Guid definition)
    {
        var stage = Environment.GetEnvironmentVariable("AETHER_V30_CRASH_STAGE") ?? throw new InvalidOperationException("Stage required.");
        var itemId = ((IIdentifiable)original).Id;
        var info = new CrashInfo(definition, ((IIdentifiable)buyer.Account!).Id, ((IIdentifiable)seller.Account!).Id, itemId, listing, purchase);
        await File.WriteAllTextAsync(ProbePath("state.json"), JsonSerializer.Serialize(info)).ConfigureAwait(false);
        await CheckpointAsync("before-custody").ConfigureAwait(false);
        Assert.That(await new GameServerNativeCustody(this._database, "Origin").ListAsync(seller, listing, itemId, 1000).ConfigureAwait(false), Is.True);
        await CheckpointAsync("after-custody").ConfigureAwait(false);
        await ReserveAsync(this._database, purchase, listing, buyer).ConfigureAwait(false);
        await CheckpointAsync("before-payment").ConfigureAwait(false);
        Assert.That(await new PostgreSqlAuthorizedAcSettlement(this._database, "Origin", true).SettleAsync(buyer, purchase).ConfigureAwait(false), Is.EqualTo("SETTLED"));
        await CheckpointAsync("after-payment").ConfigureAwait(false);
        await CheckpointAsync("before-delivery").ConfigureAwait(false);
        var native = new GameServerCustodyDelivery(this._database, context, "Origin");
        if (stage is "during-persistence" or "during-recovery")
        {
            await using var delay = this._database.CreateCommand("""
                CREATE OR REPLACE FUNCTION public.v30_crash_delay() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN PERFORM pg_sleep(90); RETURN NEW; END $$;
                CREATE TRIGGER v30_crash_delay AFTER UPDATE ON data."Item" FOR EACH ROW EXECUTE FUNCTION public.v30_crash_delay();
                """);
            await delay.ExecuteNonQueryAsync().ConfigureAwait(false);
            Task operation;
            if (stage == "during-recovery")
            {
                const string connectionVariable = "AETHER_V30_CRASH_PLUGIN";
                Environment.SetEnvironmentVariable(connectionVariable, new NpgsqlConnectionStringBuilder(this._database.ConnectionString) { Password = Environment.GetEnvironmentVariable("AETHER_MARKETPLACE_TEST_DB_PASSWORD") }.ConnectionString);
                var plugin = new NativeDeliveryRecoveryPlugIn { Configuration = new() { Realms = new() { [1] = "Origin" }, ConnectionVariables = new() { ["Origin"] = connectionVariable } } };
                operation = plugin.ExecuteTaskAsync(context).AsTask();
            }
            else
            {
                operation = new SafeDeliveryCoordinator(native, native).DeliverAsync(purchase, buyer.SelectedCharacter!.Id, itemId.ToString("D")).AsTask();
            }

            var observed = false;
            for (var attempt = 0; attempt < 200 && !operation.IsCompleted; attempt++)
            {
                await using var query = this._database.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep')");
                if (await query.ExecuteScalarAsync().ConfigureAwait(false) is true)
                {
                    observed = true;
                    break;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            Assert.That(observed, Is.True, "Observe an uncommitted native inventory transaction before terminating.");
            await CheckpointAsync(stage).ConfigureAwait(false);
            await operation.ConfigureAwait(false);
        }
        else
        {
            Assert.That(await native.TryReserveAsync(purchase, buyer.SelectedCharacter!.Id, itemId.ToString("D")).ConfigureAwait(false), Is.True);
            await using var escrow = this._database.CreateCommand("SELECT escrow_storage_id FROM aether_marketplace.native_item_custody WHERE listing_id=@listing");
            escrow.Parameters.AddWithValue("listing", listing);
            var escrowId = (Guid)(await escrow.ExecuteScalarAsync().ConfigureAwait(false))!;
            var delivered = await MUnique.OpenMU.GameLogic.Marketplace.MarketplaceInventoryDelivery.TransferExistingItemAsync(buyer, buyer.SelectedCharacter.Id, escrowId, itemId, ((IIdentifiable)buyer.Account!).Id).ConfigureAwait(false);
            Assert.That(delivered.Status.ToString(), Is.EqualTo("Delivered"));
            await CheckpointAsync("after-persistence").ConfigureAwait(false);
        }

        Assert.Fail("Unknown or unreached process checkpoint.");

        async Task CheckpointAsync(string name)
        {
            if (name != stage)
            {
                return;
            }

            await File.WriteAllTextAsync(ProbePath("ready.json"), JsonSerializer.Serialize(new { stage, pid = Environment.ProcessId })).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        }
    }

    private sealed record CrashInfo(Guid Definition, Guid BuyerAccount, Guid SellerAccount, Guid Item, Guid Listing, Guid Purchase);
}
