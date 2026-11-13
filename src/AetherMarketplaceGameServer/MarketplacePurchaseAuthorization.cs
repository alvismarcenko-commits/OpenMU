// <copyright file="MarketplacePurchaseAuthorization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using AetherMarketplaceDelivery;

/// <summary>
/// Validates trusted GameServer purchase identity without executing payment.
/// </summary>
public static class MarketplacePurchaseAuthorization
{
    /// <summary>
    /// Checks the verified account binding against the live GameServer identity.
    /// </summary>
    /// <param name="purchaseId">Durable purchase identifier.</param>
    /// <param name="buyerCharacterId">Buyer from the trusted purchase reservation.</param>
    /// <param name="identity">Trusted account binding.</param>
    /// <param name="liveAccountId">Authenticated OpenMU account.</param>
    /// <param name="liveCharacterId">Authenticated active character.</param>
    /// <param name="configuredRealm">Server-configured realm.</param>
    /// <returns>True only when all identities match.</returns>
    public static bool IsAuthorized(
        Guid purchaseId,
        Guid buyerCharacterId,
        NativeAccountIdentity? identity,
        Guid liveAccountId,
        Guid liveCharacterId,
        string configuredRealm)
    {
        return purchaseId != Guid.Empty
            && buyerCharacterId != Guid.Empty
            && identity is not null
            && identity.MarketplaceAccountId > 0
            && identity.OpenMuAccountId != Guid.Empty
            && identity.CharacterId != Guid.Empty
            && liveAccountId != Guid.Empty
            && liveCharacterId != Guid.Empty
            && !string.IsNullOrWhiteSpace(configuredRealm)
            && identity.OpenMuAccountId == liveAccountId
            && identity.CharacterId == buyerCharacterId
            && buyerCharacterId == liveCharacterId
            && string.Equals(
                identity.Realm,
                configuredRealm,
                StringComparison.Ordinal);
    }
}