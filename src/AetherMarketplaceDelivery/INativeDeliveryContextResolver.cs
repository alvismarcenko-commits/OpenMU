// <copyright file="INativeDeliveryContextResolver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resolves trusted OpenMU delivery contexts.
/// </summary>
public interface INativeDeliveryContextResolver
{
    /// <summary>
    /// Resolves a verified native delivery context.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Trusted context or null.</returns>
    Task<NativeDeliveryContext?> ResolveAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default);
}