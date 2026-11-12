// <copyright file="NativeDeliveryContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents a trusted native delivery context.
/// </summary>
/// <param name="Identity">Verified identity binding.</param>
/// <param name="LiveAccountId">OpenMU account identifier.</param>
/// <param name="LiveCharacterId">Live character identifier.</param>
/// <param name="LiveRealm">Live server realm.</param>
/// <param name="DeliverReservedAsync">Trusted delivery operation.</param>
public sealed record NativeDeliveryContext(
    NativeAccountIdentity Identity,
    Guid LiveAccountId,
    Guid LiveCharacterId,
    string LiveRealm,
    Func<Guid, CancellationToken, Task<NativeInventoryDeliveryResult>>
        DeliverReservedAsync);