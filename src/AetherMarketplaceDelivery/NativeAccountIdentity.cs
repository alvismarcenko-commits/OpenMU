// <copyright file="NativeAccountIdentity.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery;

using System;

/// <summary>
/// Represents a verified Marketplace-to-OpenMU identity binding.
/// </summary>
/// <param name="MarketplaceAccountId">Marketplace account identifier.</param>
/// <param name="OpenMuAccountId">OpenMU account identifier.</param>
/// <param name="CharacterId">Character identifier.</param>
/// <param name="Realm">Realm identifier.</param>
public sealed record NativeAccountIdentity(
    long MarketplaceAccountId,
    Guid OpenMuAccountId,
    Guid CharacterId,
    string Realm);