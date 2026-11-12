// <copyright file="PlayerListingStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>Lifecycle of a player Marketplace listing.</summary>
public enum PlayerListingStatus
{
    /// <summary>Listing is being prepared.</summary>
    Draft,

    /// <summary>Item reservation is pending.</summary>
    Reserved,

    /// <summary>Listing is available for purchase.</summary>
    Active,

    /// <summary>Listing was sold.</summary>
    Sold,

    /// <summary>Listing was cancelled.</summary>
    Cancelled,

    /// <summary>Listing needs reconciliation.</summary>
    ReconciliationRequired,
}