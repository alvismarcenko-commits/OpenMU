// <copyright file="DeliveryJournalEntry.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.PostgreSql;

/// <summary>
/// Describes a persisted Marketplace delivery.
/// </summary>
/// <param name="DeliveryId">Delivery identifier.</param>
/// <param name="CharacterId">Character identifier.</param>
/// <param name="AccountId">Account identifier.</param>
/// <param name="Realm">Realm identifier.</param>
/// <param name="ItemInstanceId">Item identifier.</param>
/// <param name="State">Durable state.</param>
/// <param name="AttemptCount">Processing attempt count.</param>
public sealed record DeliveryJournalEntry(
    Guid DeliveryId,
    Guid CharacterId,
    long AccountId,
    string Realm,
    string ItemInstanceId,
    JournalState State,
    int AttemptCount);
