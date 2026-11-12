// <copyright file="PlayerListingRequest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Request to list an owned item for sale.
/// </summary>
/// <param name="ListingId">Listing identifier.</param>
/// <param name="SellerCharacterId">Seller character.</param>
/// <param name="Realm">Server realm.</param>
/// <param name="ItemInstanceId">Unique item identifier.</param>
/// <param name="Currency">Requested currency.</param>
/// <param name="Price">Requested price.</param>
public sealed record PlayerListingRequest(
    Guid ListingId,
    Guid SellerCharacterId,
    string Realm,
    string ItemInstanceId,
    PlayerMarketCurrency Currency,
    long Price);