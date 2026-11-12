// <copyright file="DeliveryRequest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Identifies one requested item delivery.
/// </summary>
/// <param name="DeliveryId">Unique delivery identifier.</param>
/// <param name="CharacterId">Destination character identifier.</param>
/// <param name="AccountId">Owning account identifier.</param>
/// <param name="Realm">Destination realm.</param>
/// <param name="ItemInstanceId">Unique item instance identifier.</param>
public sealed record DeliveryRequest(
    Guid DeliveryId,
    Guid CharacterId,
    long AccountId,
    string Realm,
    string ItemInstanceId);
