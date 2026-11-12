// <copyright file="ISafeDeliveryJournal.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Persistent delivery journal contract.
/// </summary>
public interface ISafeDeliveryJournal
{
    /// <summary>Finds an existing delivery receipt.</summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Existing receipt or null.</returns>
    ValueTask<SafeDeliveryReceipt?> FindAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to reserve a delivery identifier.</summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="characterId">Target character.</param>
    /// <param name="itemInstanceId">Item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether reservation succeeded.</returns>
    ValueTask<bool> TryReserveAsync(
        Guid deliveryId,
        Guid characterId,
        string itemInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>Records the delivery outcome.</summary>
    /// <param name="receipt">Delivery receipt.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completion task.</returns>
    ValueTask MarkAsync(
        SafeDeliveryReceipt receipt,
        CancellationToken cancellationToken = default);
}