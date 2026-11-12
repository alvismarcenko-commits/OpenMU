// <copyright file="PlayerMarketplaceValidation.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Validates player Marketplace listing requests.
/// </summary>
public static class PlayerMarketplaceValidation
{
    /// <summary>Validates a listing request.</summary>
    /// <param name="listing">Listing request.</param>
    /// <returns>True if the request is structurally valid.</returns>
    public static bool IsValid(PlayerListingRequest listing)
    {
        return listing.ListingId != Guid.Empty
            && listing.SellerCharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(listing.Realm)
            && !string.IsNullOrWhiteSpace(listing.ItemInstanceId)
            && Enum.IsDefined(listing.Currency)
            && listing.Price > 0;
    }
}