// <copyright file="IReservedNativeItemSource.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using System.Threading;
using AetherMarketplaceDelivery;

/// <summary>Provides immutable reservations and a durable, atomic one-shot native execution claim.</summary>
public interface IReservedNativeItemSource
{
    /// <summary>Reads a trusted reservation without authorizing execution.</summary>
    /// <param name="request">The requested delivery.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reserved snapshot, or null.</returns>
    ValueTask<ReservedNativeItem?> ReadAsync(DeliveryRequest request, CancellationToken cancellationToken);

    /// <summary>Claims a matching snapshot permanently before any native mutation. Never resets a claim on failure.</summary>
    /// <param name="reservation">The exact snapshot previously read.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether this is the sole authorized native attempt.</returns>
    ValueTask<bool> TryBeginAsync(ReservedNativeItem reservation, CancellationToken cancellationToken);
}
