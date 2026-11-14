// <copyright file="GameServerNativeReturns.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Threading;
using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Marketplace;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Returns cancelled listings through the same original-entity persistence path.</summary>
public sealed class GameServerNativeReturns
{
    private readonly NpgsqlDataSource _source;
    private readonly IGameContext _context;
    private readonly string _realm;

    /// <summary>Initializes a new instance of the <see cref="GameServerNativeReturns"/> class.</summary>
    /// <param name="source">The trusted store.</param>
    /// <param name="context">The owning player registry.</param>
    /// <param name="realm">The configured realm.</param>
    public GameServerNativeReturns(NpgsqlDataSource source, IGameContext context, string realm)
    {
        this._source = source;
        this._context = context;
        this._realm = realm;
    }

    /// <summary>Resumes a pending return without manufacturing a completion acknowledgement.</summary>
    /// <param name="listingId">The cancelled listing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The confirmed return state.</returns>
    public async ValueTask<SafeDeliveryStatus> ResumeAsync(Guid listingId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this._source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var fence = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@id,30))", connection, fence);
        claim.Parameters.AddWithValue("id", listingId.ToString("D"));
        if (await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return SafeDeliveryStatus.ReconciliationRequired;
        }

        await using var lookup = this._source.CreateCommand("""
            SELECT r.item_id,r.seller_character_id,r.seller_account_id,r.escrow_storage_id,r.status,s.store_id
            FROM aether_marketplace.native_listing_return r CROSS JOIN aether_marketplace.native_delivery_store s
            WHERE r.listing_id=@id AND r.realm=@realm
            """);
        lookup.Parameters.AddWithValue("id", listingId);
        lookup.Parameters.AddWithValue("realm", this._realm);
        Guid item;
        Guid character;
        Guid account;
        Guid escrow;
        Guid store;
        await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return SafeDeliveryStatus.Rejected;
            }

            if (reader.GetString(4) == "COMPLETED")
            {
                return SafeDeliveryStatus.AlreadyDelivered;
            }

            item = reader.GetGuid(0);
            character = reader.GetGuid(1);
            account = reader.GetGuid(2);
            escrow = reader.GetGuid(3);
            store = reader.GetGuid(5);
        }

        var players = (await this._context.GetPlayersAsync().ConfigureAwait(false)).Where(p => p.SelectedCharacter?.Id == character).Take(2).ToArray();
        if (players.Length != 1)
        {
            return SafeDeliveryStatus.ReconciliationRequired;
        }

        var player = players[0];
        return await player.RunPersistenceExclusiveAsync(
            async () =>
            {
                if (!player.IsConnected || player.Account is not IIdentifiable identity || identity.Id != account
                    || !await player.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(store, cancellationToken).ConfigureAwait(false))
                {
                    return SafeDeliveryStatus.Rejected;
                }

                var result = await MarketplaceInventoryDelivery.TransferExistingItemAsync(player, character, escrow, item, account, cancellationToken).ConfigureAwait(false);
                if (result.Status == MarketplaceNativeStatus.SaveUncertain)
                {
                    await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                    await player.DisposeAsync().ConfigureAwait(false);
                }

                await using var verify = this._source.CreateCommand("SELECT status FROM aether_marketplace.native_listing_return WHERE listing_id=@id");
                verify.Parameters.AddWithValue("id", listingId);
                return await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string == "COMPLETED"
                    ? SafeDeliveryStatus.Delivered : SafeDeliveryStatus.ReconciliationRequired;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
