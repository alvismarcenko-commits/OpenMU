// <copyright file="DeliveryIdentityGuard.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.PostgreSql;

/// <summary>
/// Validates the immutable identity of Marketplace delivery requests.
/// </summary>
public static class DeliveryIdentityGuard
{
    /// <summary>
    /// Determines whether two requests describe the same delivery.
    /// </summary>
    /// <param name="expected">The original delivery request.</param>
    /// <param name="actual">The incoming delivery request.</param>
    /// <returns>True when all identity fields match.</returns>
    public static bool Matches(
        DeliveryRequest expected,
        DeliveryRequest actual)
    {
        return expected.DeliveryId == actual.DeliveryId
            && expected.CharacterId == actual.CharacterId
            && expected.AccountId == actual.AccountId
            && string.Equals(
                expected.Realm,
                actual.Realm,
                StringComparison.Ordinal)
            && string.Equals(
                expected.ItemInstanceId,
                actual.ItemInstanceId,
                StringComparison.Ordinal);
    }

    /// <summary>
    /// Rejects an incoming request when its identity differs.
    /// </summary>
    /// <param name="expected">The original delivery request.</param>
    /// <param name="actual">The incoming delivery request.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the delivery identities conflict.
    /// </exception>
    public static void RequireMatch(
        DeliveryRequest expected,
        DeliveryRequest actual)
    {
        if (!Matches(expected, actual))
        {
            throw new InvalidOperationException(
                "Delivery identity conflict. Manual reconciliation required.");
        }
    }
}