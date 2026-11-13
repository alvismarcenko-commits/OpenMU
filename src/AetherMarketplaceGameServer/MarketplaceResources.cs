// <copyright file="MarketplaceResources.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Resources;

/// <summary>Localized Marketplace plugin labels.</summary>
public static class MarketplaceResources
{
    private static readonly ResourceManager Resources = new("AetherMarketplaceGameServer.MarketplaceResources", typeof(MarketplaceResources).Assembly);

    /// <summary>Gets the recovery plugin name.</summary>
    public static string RecoveryName => Resources.GetString(nameof(RecoveryName)) ?? nameof(RecoveryName);

    /// <summary>Gets the recovery plugin description.</summary>
    public static string RecoveryDescription => Resources.GetString(nameof(RecoveryDescription)) ?? nameof(RecoveryDescription);
}
