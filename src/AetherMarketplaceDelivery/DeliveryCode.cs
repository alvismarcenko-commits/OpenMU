// <copyright file="DeliveryCode.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Describes a Marketplace delivery outcome.
/// </summary>
public enum DeliveryCode
{
    /// <summary>
    /// The inventory is ready to receive the item.
    /// </summary>
    Ready,

    /// <summary>
    /// The delivery gateway confirmed the operation.
    /// </summary>
    Delivered,

    /// <summary>
    /// The destination inventory has insufficient space.
    /// </summary>
    InventoryFull,

    /// <summary>
    /// The character does not belong to the account.
    /// </summary>
    CharacterNotOwned,

    /// <summary>
    /// The destination character belongs to another realm.
    /// </summary>
    WrongRealm,

    /// <summary>
    /// A delivery with this identifier is being processed.
    /// </summary>
    DeliveryPending,

    /// <summary>
    /// The delivery identifier is associated with different data.
    /// </summary>
    DeliveryConflict,

    /// <summary>
    /// The delivery operation could not be completed.
    /// </summary>
    DeliveryFailed,
}
