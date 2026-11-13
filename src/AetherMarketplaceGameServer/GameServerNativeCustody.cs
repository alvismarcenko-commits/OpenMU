// <copyright file="GameServerNativeCustody.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Places a seller's original native item into configured realm custody.</summary>
public sealed class GameServerNativeCustody
{
    private readonly NpgsqlDataSource _source;
    private readonly string _realm;

    /// <summary>Initializes a new instance of the <see cref="GameServerNativeCustody"/> class.</summary>
    /// <param name="source">The trusted native database.</param>
    /// <param name="realm">The server-configured realm.</param>
    public GameServerNativeCustody(NpgsqlDataSource source, string realm)
    {
        this._source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        this._realm = realm;
    }

    /// <summary>Lists an original native item; the listing becomes ACTIVE only in the successful custody save.</summary>
    /// <param name="seller">The authenticated seller session.</param>
    /// <param name="listingId">The trusted listing operation identifier.</param>
    /// <param name="itemId">The persistent item identity.</param>
    /// <param name="price">The AC listing price.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether custody was durably established.</returns>
    public async ValueTask<bool> ListAsync(Player seller, Guid listingId, Guid itemId, long price, CancellationToken cancellationToken = default)
    {
        return await seller.RunPersistenceExclusiveAsync(
            async () =>
            {
                if (!seller.IsConnected || seller.IsTemplatePlayer || seller.PlayerState.CurrentState != PlayerState.EnteredWorld
                    || seller.Account is not IIdentifiable account || seller.SelectedCharacter is not { } character
                    || seller.Inventory is not { } inventory || listingId == Guid.Empty || itemId == Guid.Empty)
                {
                    return false;
                }

                var item = inventory.Items.SingleOrDefault(i => ((IIdentifiable)i).Id == itemId);
                if (item is null)
                {
                    return false;
                }

                await using var store = this._source.CreateCommand("SELECT store_id FROM aether_marketplace.native_delivery_store WHERE singleton");
                if (await store.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid storeId
                    || !await seller.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(storeId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                await using var command = this._source.CreateCommand("SELECT aether_marketplace.prepare_native_listing_v29(@listing,@item,@seller,@account,@realm,@price)");
                command.Parameters.AddWithValue("listing", listingId);
                command.Parameters.AddWithValue("item", itemId);
                command.Parameters.AddWithValue("seller", character.Id);
                command.Parameters.AddWithValue("account", account.Id);
                command.Parameters.AddWithValue("realm", this._realm);
                command.Parameters.AddWithValue("price", price);
                var escrowId = (Guid)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Custody storage missing."));
                var escrow = await seller.PersistenceContext.LoadCustodyStorageAsync(escrowId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Native custody storage missing.");
                if (!seller.IsConnected || seller.SelectedCharacter?.Id != character.Id
                    || seller.Account is not IIdentifiable currentAccount || currentAccount.Id != account.Id)
                {
                    return false;
                }

                try
                {
                    var previousSlot = item.ItemSlot;
                    await inventory.RemoveItemAsync(item).ConfigureAwait(false);
                    item.ItemSlot = 0;
                    escrow.Items.Add(item);
                    if (!await seller.SaveProgressAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await seller.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                        await seller.DisposeAsync().ConfigureAwait(false);
                        return false;
                    }

                    try
                    {
                        await seller.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(previousSlot)).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A client notification cannot undo committed native custody.
                    }
                }
                catch
                {
                    await seller.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                    await seller.DisposeAsync().ConfigureAwait(false);
                    return false;
                }

                await using var verify = this._source.CreateCommand("""
                    SELECT EXISTS(SELECT 1 FROM aether_marketplace.native_item_custody c
                    JOIN data."Item" i ON i."Id"=c.item_instance_id AND i."ItemStorageId"=c.escrow_storage_id
                    WHERE c.listing_id=@id AND c.status='HELD')
                    """);
                verify.Parameters.AddWithValue("id", listingId);
                return await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
