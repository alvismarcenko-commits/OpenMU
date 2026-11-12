// <copyright file="PlayerListing.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Stored player Marketplace listing.
/// </summary>
/// <param name="ListingId">Listing identifier.</param>
/// <param name="SellerCharacterId">Seller character.</param>
/// <param name="Realm">Server realm.</param>
/// <param name="ItemInstanceId">Unique item identifier.</param>
/// <param name="Currency">Listing currency.</param>
/// <param name="Price">Listing price.</param>
/// <param name="Status">Listing status.</param>
public sealed record PlayerListing(
    Guid ListingId,
    Guid SellerCharacterId,
    string Realm,
    string ItemInstanceId,
    PlayerMarketCurrency Currency,
    long Price,
    PlayerListingStatus Status);