// <copyright file="SafeDeliveryStatus.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>Delivery processing status.</summary>
public enum SafeDeliveryStatus
{
    /// <summary>Delivery was confirmed.</summary>
    Delivered,

    /// <summary>Delivery was already confirmed.</summary>
    AlreadyDelivered,

    /// <summary>Manual or automated reconciliation is required.</summary>
    ReconciliationRequired,

    /// <summary>The request was rejected.</summary>
    Rejected,
}