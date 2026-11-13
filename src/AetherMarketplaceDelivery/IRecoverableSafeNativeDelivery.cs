// <copyright file="IRecoverableSafeNativeDelivery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceDelivery;

/// <summary>Marks an adapter which fences workers and reconciles durable original-item ownership before retry.</summary>
public interface IRecoverableSafeNativeDelivery : ISafeNativeDelivery
{
}
