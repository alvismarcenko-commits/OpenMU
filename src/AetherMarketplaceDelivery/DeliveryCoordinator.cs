// <copyright file="DeliveryCoordinator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Prototype only. Not safe for real inventory writes or payments.
/// </summary>
public sealed class DeliveryCoordinator(
    ICharacterInventoryGateway inventory,
    IDeliveryJournal journal)
{
    /// <summary>
    /// Coordinates an isolated prototype Marketplace item delivery.
    /// </summary>
    /// <param name="request">The delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting delivery status.</returns>
    public async Task<DeliveryResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.DeliveryId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.AccountId <= 0
            || string.IsNullOrWhiteSpace(request.Realm)
            || string.IsNullOrWhiteSpace(request.ItemInstanceId))
        {
            return new(
                DeliveryCode.DeliveryFailed,
                false,
                "Invalid delivery request.");
        }

        var existing = await journal.GetRequestAsync(
            request.DeliveryId, cancellationToken).ConfigureAwait(false);

        if (existing is not null && existing != request)
        {
            return new(
                DeliveryCode.DeliveryConflict,
                false,
                "Delivery ID belongs to another request.");
        }

        var completed = await journal.GetCompletedAsync(
            request.DeliveryId, cancellationToken).ConfigureAwait(false);

        if (completed is not null)
        {
            return completed;
        }

        if (!await journal.TryReserveAsync(
            request, cancellationToken).ConfigureAwait(false))
        {
            return new(
                DeliveryCode.DeliveryPending,
                false,
                "Delivery already in progress.");
        }

        var validation = await inventory.ValidateAsync(
            request, cancellationToken).ConfigureAwait(false);

        if (validation != DeliveryCode.Ready)
        {
            var blocked = new DeliveryResult(
                validation,
                false,
                "Inventory validation failed.");

            await journal.CompleteAsync(
                request, blocked, cancellationToken).ConfigureAwait(false);

            return blocked;
        }

        // WARNING:
        // This is only an isolated prototype boundary.
        // Real OpenMU integration requires durable idempotency
        // and crash recovery before any item mutation.
        var delivered = await inventory.DeliverAsync(
            request, cancellationToken).ConfigureAwait(false);

        var result = delivered
            ? new DeliveryResult(
                DeliveryCode.Delivered,
                true,
                "Gateway confirmed delivery.")
            : new DeliveryResult(
                DeliveryCode.DeliveryFailed,
                false,
                "Gateway rejected delivery.");

        await journal.CompleteAsync(
            request, result, cancellationToken).ConfigureAwait(false);

        return result;
    }
}
