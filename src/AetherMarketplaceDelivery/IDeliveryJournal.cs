// <copyright file="IDeliveryJournal.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Defines a delivery journal contract.
/// </summary>
public interface IDeliveryJournal
{
    /// <summary>
    /// Finds the request registered for a delivery identifier.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Registered request or null.</returns>
    Task<DeliveryRequest?> GetRequestAsync(
        Guid deliveryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds a completed delivery result.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Completed result or null.</returns>
    Task<DeliveryResult?> GetCompletedAsync(
        Guid deliveryId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to reserve a delivery.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when reserved.</returns>
    Task<bool> TryReserveAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a delivery outcome.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="result">Delivery result.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CompleteAsync(
        DeliveryRequest request,
        DeliveryResult result,
        CancellationToken cancellationToken);
}
