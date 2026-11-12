// <copyright file="NativeInventoryDeliveryResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

/// <summary>
/// Represents the outcome of a native OpenMU inventory delivery.
/// </summary>
public enum NativeInventoryDeliveryStatus
{
    /// <summary>
    /// Inventory delivery has been durably confirmed.
    /// </summary>
    Delivered,

    /// <summary>
    /// The character inventory has insufficient space.
    /// </summary>
    InventoryFull,

    /// <summary>
    /// The target character or account could not be verified.
    /// </summary>
    InvalidTarget,

    /// <summary>
    /// Delivery may have happened, but persistence is uncertain.
    /// </summary>
    ReconciliationRequired,
}

/// <summary>
/// Provides a typed result for native inventory delivery.
/// </summary>
/// <param name="Status">Delivery status.</param>
/// <param name="ItemInstanceId">Persistent item identifier, when available.</param>
public sealed record NativeInventoryDeliveryResult(
    NativeInventoryDeliveryStatus Status,
    string? ItemInstanceId)
{
    /// <summary>
    /// Gets a value indicating whether delivery is durably confirmed.
    /// </summary>
    public bool IsConfirmed =>
        this.Status == NativeInventoryDeliveryStatus.Delivered
        && !string.IsNullOrWhiteSpace(this.ItemInstanceId);
}