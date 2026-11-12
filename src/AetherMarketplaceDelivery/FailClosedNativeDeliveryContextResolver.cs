// <copyright file="FailClosedNativeDeliveryContextResolver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Denies native item delivery until a trusted GameServer
/// resolver is explicitly implemented and registered.
/// </summary>
public sealed class FailClosedNativeDeliveryContextResolver
    : INativeDeliveryContextResolver
{
    /// <inheritdoc />
    public Task<NativeDeliveryContext?> ResolveAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<NativeDeliveryContext?>(null);
    }
}