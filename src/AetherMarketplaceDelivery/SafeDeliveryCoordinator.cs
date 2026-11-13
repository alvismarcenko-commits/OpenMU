// <copyright file="SafeDeliveryCoordinator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Coordinates delivery attempts using a durable journal contract.
/// </summary>
/// <param name="journal">Delivery journal.</param>
/// <param name="native">Native delivery adapter.</param>
public sealed class SafeDeliveryCoordinator(
    ISafeDeliveryJournal journal,
    ISafeNativeDelivery native)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Attempts to deliver an item once.</summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="characterId">Target character.</param>
    /// <param name="itemInstanceId">Item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Delivery processing status.</returns>
    public async ValueTask<SafeDeliveryStatus> DeliverAsync(
        Guid deliveryId,
        Guid characterId,
        string itemInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (deliveryId == Guid.Empty
            || characterId == Guid.Empty
            || string.IsNullOrWhiteSpace(itemInstanceId))
        {
            return SafeDeliveryStatus.Rejected;
        }

        var gate = this._locks.GetOrAdd(
            deliveryId, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var existing = await journal.FindAsync(
                deliveryId, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                if (existing.CharacterId != characterId
                    || existing.ItemInstanceId != itemInstanceId)
                {
                    return SafeDeliveryStatus.Rejected;
                }

                if (existing.Status is SafeDeliveryStatus.Delivered or SafeDeliveryStatus.AlreadyDelivered)
                {
                    return SafeDeliveryStatus.AlreadyDelivered;
                }

                if (native is not IRecoverableSafeNativeDelivery)
                {
                    return SafeDeliveryStatus.ReconciliationRequired;
                }
            }
            else if (!await journal.TryReserveAsync(
                deliveryId,
                characterId,
                itemInstanceId,
                cancellationToken).ConfigureAwait(false))
            {
                return SafeDeliveryStatus.ReconciliationRequired;
            }

            SafeDeliveryStatus result;

            try
            {
                result = await native.DeliverAsync(
                    deliveryId,
                    characterId,
                    itemInstanceId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                result = SafeDeliveryStatus.ReconciliationRequired;
            }

            if (result != SafeDeliveryStatus.Delivered)
            {
                result = SafeDeliveryStatus.ReconciliationRequired;
            }

            await journal.MarkAsync(
                new SafeDeliveryReceipt(
                    deliveryId,
                    characterId,
                    itemInstanceId,
                    result),
                cancellationToken).ConfigureAwait(false);

            return result;
        }
        finally
        {
            gate.Release();
        }
    }
}
