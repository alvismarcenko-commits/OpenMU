// <copyright file="NativeDeliveryRecoveryPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AetherMarketplaceDelivery;
using Microsoft.Extensions.Logging;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.PlugIns;
using Npgsql;

/// <summary>Resumes paid custody deliveries through the GameServer periodic-task extension point.</summary>
[PlugIn]
[Display(Name = nameof(MarketplaceResources.RecoveryName), Description = nameof(MarketplaceResources.RecoveryDescription), ResourceType = typeof(MarketplaceResources))]
[Guid("4028C0B8-2A81-4FC1-B8D0-947C52C90E29")]
public sealed class NativeDeliveryRecoveryPlugIn : IPeriodicTaskPlugIn, IDisabledByDefault,
    ISupportCustomConfiguration<NativeDeliveryPlugInConfiguration>, ISupportDefaultCustomConfiguration
{
    private readonly ConditionalWeakTable<GameContext, SemaphoreSlim> _gates = new();

    /// <inheritdoc />
    public NativeDeliveryPlugInConfiguration? Configuration { get; set; }

    /// <inheritdoc />
    public object CreateDefaultConfig() => new NativeDeliveryPlugInConfiguration();

    /// <inheritdoc />
    public void ForceStart()
    {
        // Each periodic invocation polls durable pending work for online buyers.
    }

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        if (gameContext is not IGameServerContext server || this.Configuration is not { } configuration
            || !configuration.Realms.TryGetValue(server.Id, out var realm)
            || !configuration.ConnectionVariables.TryGetValue(realm, out var variable)
            || Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } connection)
        {
            return;
        }

        var gate = this._gates.GetValue(gameContext, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var characters = (await gameContext.GetPlayersAsync().ConfigureAwait(false))
                .Where(p => p.IsConnected && p.PlayerState.CurrentState == PlayerState.EnteredWorld)
                .Select(p => p.SelectedCharacter?.Id).OfType<Guid>().ToArray();
            if (characters.Length == 0)
            {
                return;
            }

            await using var source = NpgsqlDataSource.Create(connection);

            // Resume interrupted seller custody acquisition before exposing a listing.
            await using (var acquiring = source.CreateCommand("""
                SELECT l.listing_id,l.item_instance_id,l.seller_character_id,l.price
                FROM aether_marketplace.player_listing l
                JOIN aether_marketplace.native_item_custody c ON c.listing_id=l.listing_id
                WHERE l.realm=@realm AND l.status='DRAFT' AND c.status='ACQUIRING'
                  AND l.seller_character_id=ANY(@characters) LIMIT 100
                """))
            {
                acquiring.Parameters.AddWithValue("realm", realm);
                acquiring.Parameters.AddWithValue("characters", characters);
                var listings = new List<(Guid Listing, Guid Item, Guid Seller, long Price)>();
                await using (var reader = await acquiring.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        listings.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetInt64(3)));
                    }
                }

                var players = await gameContext.GetPlayersAsync().ConfigureAwait(false);
                foreach (var listing in listings)
                {
                    var sellers = players.Where(p => p.SelectedCharacter?.Id == listing.Seller).Take(2).ToArray();
                    if (sellers.Length == 1)
                    {
                        await new GameServerNativeCustody(source, realm).ListAsync(sellers[0], listing.Listing, listing.Item, listing.Price).ConfigureAwait(false);
                    }
                }
            }

            var service = new GameServerCustodyDelivery(source, gameContext, realm);
            var coordinator = new SafeDeliveryCoordinator(service, service);
            await using var command = source.CreateCommand("""
                SELECT p.purchase_id,p.buyer_character_id,c.item_instance_id
                FROM aether_marketplace.purchase_reservation p
                JOIN aether_marketplace.native_item_custody c ON c.listing_id=p.listing_id
                WHERE p.realm=@realm AND p.status='DELIVERY_PENDING' AND p.currency='AetherCoins'
                  AND p.buyer_character_id=ANY(@characters)
                ORDER BY p.created_at LIMIT 100
                """);
            command.Parameters.AddWithValue("realm", realm);
            command.Parameters.AddWithValue("characters", characters);
            var deliveries = new List<(Guid Purchase, Guid Buyer, Guid Item)>();
            await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    deliveries.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
                }
            }

            foreach (var delivery in deliveries)
            {
                await coordinator.DeliverAsync(delivery.Purchase, delivery.Buyer, delivery.Item.ToString("D")).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            gameContext.LoggerFactory.CreateLogger<NativeDeliveryRecoveryPlugIn>().LogError(ex, "Native Marketplace recovery failed for realm {Realm}.", realm);
        }
        finally
        {
            gate.Release();
        }
    }
}
