// <copyright file="NativeAccountIdentityBridge.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Validates Marketplace-to-OpenMU identity bindings.
/// </summary>
public static class NativeAccountIdentityBridge
{
    /// <summary>
    /// Validates all identities before native inventory delivery.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="identity">Verified identity.</param>
    /// <param name="liveAccountId">Live OpenMU account identifier.</param>
    /// <param name="liveCharacterId">Live character identifier.</param>
    /// <param name="liveRealm">Live realm.</param>
    /// <returns>True when every identity matches.</returns>
    public static bool Matches(
        DeliveryRequest request,
        NativeAccountIdentity identity,
        Guid liveAccountId,
        Guid liveCharacterId,
        string liveRealm)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);

        if (request.DeliveryId == Guid.Empty
            || request.AccountId <= 0
            || request.CharacterId == Guid.Empty
            || identity.MarketplaceAccountId <= 0
            || identity.OpenMuAccountId == Guid.Empty
            || identity.CharacterId == Guid.Empty
            || liveAccountId == Guid.Empty
            || liveCharacterId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Realm)
            || string.IsNullOrWhiteSpace(identity.Realm)
            || string.IsNullOrWhiteSpace(liveRealm))
        {
            return false;
        }

        return request.AccountId == identity.MarketplaceAccountId
            && request.CharacterId == identity.CharacterId
            && request.CharacterId == liveCharacterId
            && identity.OpenMuAccountId == liveAccountId
            && string.Equals(request.Realm, identity.Realm, StringComparison.Ordinal)
            && string.Equals(identity.Realm, liveRealm, StringComparison.Ordinal);
    }
}