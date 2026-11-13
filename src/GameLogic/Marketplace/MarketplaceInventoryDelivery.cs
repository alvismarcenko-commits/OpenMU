namespace MUnique.OpenMU.GameLogic.Marketplace;

using System;
using System.Threading;
using System.Threading.Tasks;
using MUnique.OpenMU.GameLogic.Administration;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Result of a native Marketplace inventory operation.
/// </summary>
public enum MarketplaceNativeStatus
{
    Delivered,
    InventoryFull,
    InvalidItem,
    InvalidCharacter,
    SaveUncertain,
}

/// <summary>
/// Inserts a server-approved item into a live player's inventory.
/// Does not authorize purchases, debit currency or provide
/// cross-database exactly-once delivery guarantees.
/// </summary>
/// <summary>
/// Native Marketplace delivery result.
/// </summary>
public sealed record MarketplaceDeliveryResult(
    MarketplaceNativeStatus Status,
    string? ItemInstanceId);
public static class MarketplaceInventoryDelivery
{
    /// <summary>Moves an existing escrow entity without rebuilding its metadata.</summary>
    /// <param name="player">The authenticated buyer.</param>
    /// <param name="expectedCharacterId">The authorized buyer character.</param>
    /// <param name="escrowStorageId">The trusted custody storage.</param>
    /// <param name="itemId">The original persistent item identity.</param>
    /// <param name="expectedAccountId">The independently authorized native account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The native save outcome, which still requires independent durable verification.</returns>
    /// <remarks>The caller must hold a durable worker fence and install the custody ownership guard
    /// in the native persistence database. An uncertain save requires a fresh player context before retry.</remarks>
    public static async ValueTask<MarketplaceDeliveryResult> TransferExistingItemAsync(
        Player player,
        Guid expectedCharacterId,
        Guid escrowStorageId,
        Guid itemId,
        Guid expectedAccountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        return await player.RunPersistenceExclusiveAsync(
            async () =>
        {
            if (!player.IsConnected || player.IsTemplatePlayer
                || player.PlayerState.CurrentState != PlayerState.EnteredWorld
                || player.SelectedCharacter?.Id != expectedCharacterId
                || player.Account is not MUnique.OpenMU.Persistence.IIdentifiable account || account.Id != expectedAccountId
                || expectedAccountId == Guid.Empty || player.Inventory is not { } inventory
                || itemId == Guid.Empty || escrowStorageId == Guid.Empty)
            {
                return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidCharacter, null);
            }

            var escrow = await player.PersistenceContext.LoadCustodyStorageAsync(escrowStorageId, cancellationToken).ConfigureAwait(false);

            // Loading storage alone does not hydrate its item collection in a fresh
            // player context. Load the persistent aggregate before checking custody.
            await player.PersistenceContext.GetByIdAsync<Item>(itemId, cancellationToken).ConfigureAwait(false);
            var item = escrow?.Items.SingleOrDefault(i => ((MUnique.OpenMU.Persistence.IIdentifiable)i).Id == itemId);
            if (escrow is null || item?.Definition is null)
            {
                return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidItem, null);
            }

            if (inventory.CheckInvSpace(item) is not { } slot)
            {
                return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InventoryFull, null);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!player.IsConnected || player.SelectedCharacter?.Id != expectedCharacterId
                || player.Account is not MUnique.OpenMU.Persistence.IIdentifiable currentAccount || currentAccount.Id != expectedAccountId)
            {
                return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidCharacter, null);
            }

            // Preserve the same tracked entity, option links, sets and durability.
            // Never create, clone, detach or delete an item to implement this move.
            try
            {
                escrow.Items.Remove(item);
                if (!await inventory.AddItemAsync(slot, item).ConfigureAwait(false)
                    || !await player.SaveProgressAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.SaveUncertain, null);
                }
            }
            catch
            {
                return new MarketplaceDeliveryResult(MarketplaceNativeStatus.SaveUncertain, null);
            }

            try
            {
                await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(p => p.ItemAppearAsync(item)).ConfigureAwait(false);
            }
            catch
            {
                // Ownership is already committed; notification failure cannot undo it.
            }

            return new MarketplaceDeliveryResult(MarketplaceNativeStatus.Delivered, itemId.ToString("D"));
        },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Delivers a validated item to a live character.
    /// The caller must have authenticated the account,
    /// realm and immutable Marketplace item specification.
    /// </summary>
    public static async ValueTask<MarketplaceNativeStatus> DeliverAsync(
        Player player,
        Guid expectedCharacterId,
        ItemSpecification specification,
        CancellationToken cancellationToken = default)
    {
        var result = await DeliverWithIdentityAsync(
            player,
            expectedCharacterId,
            specification,
            cancellationToken).ConfigureAwait(false);

        return result.Status;
    }

    /// <summary>
    /// Delivers an item and returns its persistent identity.
    /// </summary>
    public static ValueTask<MarketplaceDeliveryResult> DeliverWithIdentityAsync(
        Player player,
        Guid expectedCharacterId,
        ItemSpecification specification,
        CancellationToken cancellationToken = default)
    {
        return DeliverCoreAsync(
            player,
            expectedCharacterId,
            specification,
            null,
            cancellationToken);
    }

    /// <summary>
    /// Delivers a trusted, reserved Marketplace item identity.
    /// The caller must verify the account, realm and item specification.
    /// </summary>
    /// <param name="player">Authenticated live player.</param>
    /// <param name="expectedCharacterId">Verified character identifier.</param>
    /// <param name="specification">Trusted item specification.</param>
    /// <param name="reservedItemId">Previously reserved persistent item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The native inventory delivery result.</returns>
    public static ValueTask<MarketplaceDeliveryResult> DeliverReservedItemAsync(
        Player player,
        Guid expectedCharacterId,
        ItemSpecification specification,
        Guid reservedItemId,
        CancellationToken cancellationToken = default)
    {
        if (reservedItemId == Guid.Empty)
        {
            return ValueTask.FromResult(
                new MarketplaceDeliveryResult(
                    MarketplaceNativeStatus.InvalidItem,
                    null));
        }

        return DeliverCoreAsync(
            player,
            expectedCharacterId,
            specification,
            reservedItemId,
            cancellationToken);
    }

    private static async ValueTask<MarketplaceDeliveryResult> DeliverCoreAsync(
        Player player,
        Guid expectedCharacterId,
        ItemSpecification specification,
        Guid? reservedItemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(specification);

        return await player.RunPersistenceExclusiveAsync(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!player.IsConnected
                    || player.PlayerState.CurrentState != PlayerState.EnteredWorld
                    || player.SelectedCharacter is not { } character
                    || character.Id != expectedCharacterId
                    || player.Account is null
                    || player.IsTemplatePlayer)
                {
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidCharacter, null);
                }

                if (player.Inventory is not { } inventory)
                {
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidCharacter, null);
                }

                var definition = player.GameContext.Configuration.Items
                    .SingleOrDefault(d =>
                        d.Group == specification.Group
                        && d.Number == specification.Number);

                if (definition is null
                    || !GameMasterItemFactory.IsValid(
                        definition, specification))
                {
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InvalidItem, null);
                }

                var temporary = GameMasterItemFactory.CreateItem(
                    definition, specification);

                if (inventory.CheckInvSpace(temporary) is not { } slot)
                {
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InventoryFull, null);
                }

                var item = reservedItemId is { } expectedItemId
                    ? (MUnique.OpenMU.DataModel.Entities.Item)
                        player.PersistenceContext.CreateNewWithId(
                            typeof(MUnique.OpenMU.DataModel.Entities.Item),
                            expectedItemId)
                    : temporary.MakePersistent(player.PersistenceContext);

                if (reservedItemId is not null)
                {
                    item.AssignValues(temporary);
                }

                bool inserted;

                try
                {
                    inserted = await inventory.AddItemAsync(
                        slot, item).ConfigureAwait(false);
                }
                catch
                {
                    // The persistence state may have changed.
                    // Do not blindly retry the same delivery.
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.SaveUncertain, null);
                }

                if (!inserted)
                {
                    player.PersistenceContext.Detach(item);
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.InventoryFull, null);
                }

                try
                {
                    var saved = await player.SaveProgressAsync(
                        cancellationToken).ConfigureAwait(false);

                    if (!saved)
                    {
                        return new MarketplaceDeliveryResult(MarketplaceNativeStatus.SaveUncertain, null);
                    }
                }
                catch
                {
                    // The item may have reached persistent storage.
                    // Reconciliation is required before retrying.
                    return new MarketplaceDeliveryResult(MarketplaceNativeStatus.SaveUncertain, null);
                }

                try
                {
                    await player.InvokeViewPlugInAsync<IItemAppearPlugIn>(
                        p => p.ItemAppearAsync(item))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // The item was saved successfully.
                    // Client notification failure must not
                    // trigger another item delivery.
                }

                var persistentItemId =
                    ((MUnique.OpenMU.Persistence.IIdentifiable)item).Id;

                if (persistentItemId == Guid.Empty
                    || (reservedItemId is { } expectedId
                        && persistentItemId != expectedId))
                {
                    return new MarketplaceDeliveryResult(
                        MarketplaceNativeStatus.SaveUncertain,
                        null);
                }

                return new MarketplaceDeliveryResult(
                    MarketplaceNativeStatus.Delivered,
                    persistentItemId.ToString("D"));
            },
            cancellationToken).ConfigureAwait(false);
    }
}
