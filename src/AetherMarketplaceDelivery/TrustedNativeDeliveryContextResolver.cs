// <copyright file="TrustedNativeDeliveryContextResolver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Resolves native delivery contexts using a trusted
/// server-owned provider.
/// </summary>
public sealed class TrustedNativeDeliveryContextResolver
    : INativeDeliveryContextResolver
{
    private readonly Func<DeliveryRequest, CancellationToken,
        Task<NativeDeliveryContext?>> trustedProvider;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="TrustedNativeDeliveryContextResolver"/> class.
    /// </summary>
    /// <param name="trustedProvider">Trusted server-side provider.</param>
    public TrustedNativeDeliveryContextResolver(
        Func<DeliveryRequest, CancellationToken,
            Task<NativeDeliveryContext?>> trustedProvider)
    {
        this.trustedProvider = trustedProvider
            ?? throw new ArgumentNullException(nameof(trustedProvider));
    }

    /// <inheritdoc />
    public async Task<NativeDeliveryContext?> ResolveAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.DeliveryId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.AccountId <= 0
            || string.IsNullOrWhiteSpace(request.Realm)
            || !Guid.TryParse(request.ItemInstanceId, out var itemId)
            || itemId == Guid.Empty)
        {
            return null;
        }

        var context = await this.trustedProvider(
            request, cancellationToken).ConfigureAwait(false);

        if (context is null
            || context.DeliverReservedAsync is null
            || !NativeAccountIdentityBridge.Matches(
                request,
                context.Identity,
                context.LiveAccountId,
                context.LiveCharacterId,
                context.LiveRealm))
        {
            return null;
        }

        return context;
    }
}