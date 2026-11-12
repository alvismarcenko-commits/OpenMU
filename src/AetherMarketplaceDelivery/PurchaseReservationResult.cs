// <copyright file="PurchaseReservationResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

/// <summary>Result of a server-authorized purchase reservation attempt.</summary>
public enum PurchaseReservationResult
{
    /// <summary>A new reservation was created.</summary>
    Reserved,

    /// <summary>The same purchase identity was already recorded; do not repeat payment or delivery.</summary>
    AlreadyReserved,

    /// <summary>The purchase identity was reused with different parameters.</summary>
    PurchaseIdConflict,

    /// <summary>The listing does not exist.</summary>
    ListingNotFound,

    /// <summary>The listing cannot currently be purchased.</summary>
    ListingNotActive,

    /// <summary>The buyer is also the seller.</summary>
    SelfPurchaseDenied,

    /// <summary>The request is structurally invalid.</summary>
    InvalidRequest,

    /// <summary>The database returned an unrecognized result.</summary>
    ReconciliationRequired,
}