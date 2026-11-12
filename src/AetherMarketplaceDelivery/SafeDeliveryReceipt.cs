// <copyright file="SafeDeliveryReceipt.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Identifies a delivery and its recorded status.
/// </summary>
/// <param name="DeliveryId">Delivery identifier.</param>
/// <param name="CharacterId">Target character identifier.</param>
/// <param name="ItemInstanceId">Item identifier.</param>
/// <param name="Status">Delivery status.</param>
public sealed record SafeDeliveryReceipt(
    Guid DeliveryId,
    Guid CharacterId,
    string ItemInstanceId,
    SafeDeliveryStatus Status);