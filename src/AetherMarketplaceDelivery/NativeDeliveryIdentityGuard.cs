// <copyright file="NativeDeliveryIdentityGuard.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Validates a native Marketplace delivery identity.
/// </summary>
public static class NativeDeliveryIdentityGuard
{
    /// <summary>
    /// Verifies that a delivered item matches the registered request.
    /// </summary>
    /// <param name="request">Registered request.</param>
    /// <param name="result">Native result.</param>
    /// <returns>Whether the identities match.</returns>
    public static bool IsConfirmed(
        DeliveryRequest request,
        NativeInventoryDeliveryResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (request.DeliveryId == Guid.Empty ||
            request.CharacterId == Guid.Empty ||
            request.AccountId <= 0 ||
            string.IsNullOrWhiteSpace(request.Realm) ||
            !Guid.TryParse(request.ItemInstanceId, out var expected) ||
            expected == Guid.Empty)
        {
            return false;
        }

        if (result.Status != NativeInventoryDeliveryStatus.Delivered ||
            !Guid.TryParse(result.ItemInstanceId, out var actual) ||
            actual == Guid.Empty)
        {
            return false;
        }

        return expected == actual;
    }
}