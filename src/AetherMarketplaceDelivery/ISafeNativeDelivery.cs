// <copyright file="ISafeNativeDelivery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Interface for an authorized native delivery implementation.
/// </summary>
public interface ISafeNativeDelivery
{
    /// <summary>Requests native item delivery.</summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="characterId">Target character.</param>
    /// <param name="itemInstanceId">Item identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Native delivery status.</returns>
    ValueTask<SafeDeliveryStatus> DeliverAsync(
        Guid deliveryId,
        Guid characterId,
        string itemInstanceId,
        CancellationToken cancellationToken = default);
}