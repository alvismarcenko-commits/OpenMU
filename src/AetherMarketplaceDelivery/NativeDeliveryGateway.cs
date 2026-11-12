// <copyright file="NativeDeliveryGateway.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Provides a fail-closed native Marketplace delivery adapter.
/// </summary>
public sealed class NativeDeliveryGateway : INativeInventoryDeliveryGateway
{
    private readonly INativeDeliveryContextResolver resolver;

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="NativeDeliveryGateway"/> class.
    /// </summary>
    /// <param name="resolver">Trusted server-side resolver.</param>
    public NativeDeliveryGateway(INativeDeliveryContextResolver resolver)
    {
        this.resolver = resolver
            ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public async Task<NativeInventoryDeliveryResult> DeliverAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.DeliveryId == Guid.Empty
            || request.CharacterId == Guid.Empty
            || request.AccountId <= 0
            || string.IsNullOrWhiteSpace(request.Realm)
            || !Guid.TryParse(request.ItemInstanceId, out var itemId)
            || itemId == Guid.Empty)
        {
            return InvalidTarget();
        }

        // This resolver must run only on the trusted game server.
        // It must not accept identity or item specifications
        // directly from an unauthenticated web request.
        var context = await this.resolver.ResolveAsync(
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
            return InvalidTarget();
        }

        // Once native execution begins, an exception can mean
        // that the item was saved but confirmation was lost.
        NativeInventoryDeliveryResult? result;

        try
        {
            result = await context.DeliverReservedAsync(
                itemId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Reconciliation();
        }

        if (result is null)
        {
            return Reconciliation();
        }

        if (result.Status == NativeInventoryDeliveryStatus.Delivered)
        {
            return NativeDeliveryIdentityGuard.IsConfirmed(
                request, result)
                ? result
                : Reconciliation();
        }

        if (result.Status == NativeInventoryDeliveryStatus.InventoryFull)
        {
            return result;
        }

        return Reconciliation();
    }

    private static NativeInventoryDeliveryResult InvalidTarget()
    {
        return new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.InvalidTarget,
            null);
    }

    private static NativeInventoryDeliveryResult Reconciliation()
    {
        return new NativeInventoryDeliveryResult(
            NativeInventoryDeliveryStatus.ReconciliationRequired,
            null);
    }
}