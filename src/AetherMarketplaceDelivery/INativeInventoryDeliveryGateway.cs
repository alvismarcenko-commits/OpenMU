// <copyright file="INativeInventoryDeliveryGateway.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

/// <summary>
/// Defines a native OpenMU inventory delivery gateway.
/// </summary>
public interface INativeInventoryDeliveryGateway
{
    /// <summary>
    /// Attempts a delivery to an authoritative character inventory.
    /// </summary>
    /// <param name="request">Immutable delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The authoritative delivery outcome.</returns>
    Task<NativeInventoryDeliveryResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default);
}