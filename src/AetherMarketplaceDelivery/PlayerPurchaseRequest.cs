// <copyright file="PlayerPurchaseRequest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Request to purchase a player listing.
/// </summary>
/// <param name="PurchaseId">Purchase identifier.</param>
/// <param name="ListingId">Listing identifier.</param>
/// <param name="BuyerCharacterId">Buyer character.</param>
public sealed record PlayerPurchaseRequest(
    Guid PurchaseId,
    Guid ListingId,
    Guid BuyerCharacterId);