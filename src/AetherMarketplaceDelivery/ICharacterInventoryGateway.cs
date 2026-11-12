// <copyright file="ICharacterInventoryGateway.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Defines the inventory gateway contract.
/// </summary>
public interface ICharacterInventoryGateway
{
    /// <summary>
    /// Validates the destination and inventory capacity.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation outcome.</returns>
    Task<DeliveryCode> ValidateAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs a prototype delivery operation.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the gateway confirms delivery.</returns>
    Task<bool> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken);
}
