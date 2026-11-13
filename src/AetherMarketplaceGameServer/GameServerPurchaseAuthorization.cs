// <copyright file="GameServerPurchaseAuthorization.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Checks a purchase against the authenticated GameServer player.</summary>
public static class GameServerPurchaseAuthorization
{
    /// <summary>Validates a trusted reservation against the current player session.</summary>
    /// <param name="player">The GameServer-owned player.</param>
    /// <param name="purchaseId">The durable purchase identifier.</param>
    /// <param name="buyerCharacterId">Buyer from the trusted reservation.</param>
    /// <param name="identity">The independently verified account binding.</param>
    /// <param name="configuredRealm">The server-configured realm.</param>
    /// <returns>Whether the current session matches the purchase.</returns>
    public static bool IsAuthorized(
        Player? player,
        Guid purchaseId,
        Guid buyerCharacterId,
        NativeAccountIdentity? identity,
        string configuredRealm)
    {
        if (player is null
            || !player.IsConnected
            || player.IsTemplatePlayer
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || player.Account is not IIdentifiable account
            || player.SelectedCharacter is not { } character)
        {
            return false;
        }

        return MarketplacePurchaseAuthorization.IsAuthorized(
            purchaseId,
            buyerCharacterId,
            identity,
            account.Id,
            character.Id,
            configuredRealm);
    }
}