// <copyright file="JournalState.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.PostgreSql;

/// <summary>
/// Represents a durable delivery state.
/// </summary>
public enum JournalState
{
    /// <summary>
    /// Delivery has been registered.
    /// </summary>
    Pending,

    /// <summary>
    /// A worker may be changing inventory.
    /// </summary>
    Processing,

    /// <summary>
    /// Delivery was confirmed.
    /// </summary>
    Delivered,

    /// <summary>
    /// The inventory did not have sufficient capacity.
    /// </summary>
    InventoryFull,

    /// <summary>
    /// The actual inventory state requires verification.
    /// </summary>
    ReconciliationRequired,

    /// <summary>
    /// Delivery failed without a confirmed inventory mutation.
    /// </summary>
    Failed,
}
