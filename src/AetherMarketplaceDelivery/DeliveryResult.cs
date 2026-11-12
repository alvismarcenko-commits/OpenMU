// <copyright file="DeliveryResult.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>
/// Reports the result of a delivery request.
/// </summary>
/// <param name="Code">Delivery status.</param>
/// <param name="DeliveryPerformed">Whether the gateway confirmed delivery.</param>
/// <param name="Message">Diagnostic message.</param>
public sealed record DeliveryResult(
    DeliveryCode Code,
    bool DeliveryPerformed,
    string Message);
