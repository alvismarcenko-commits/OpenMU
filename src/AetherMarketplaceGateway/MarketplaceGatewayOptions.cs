// <copyright file="MarketplaceGatewayOptions.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGateway;

using System.Text.Json;
using AetherMarketplaceGameServer;

/// <summary>Disabled-by-default trusted website bridge configuration.</summary>
public sealed class MarketplaceGatewayOptions
{
    /// <summary>Gets or sets a value indicating whether the private bridge is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the server-only bearer secret.</summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Gets or sets explicit native server and connection routing.</summary>
    public NativeDeliveryPlugInConfiguration Routing { get; set; } = new();

    /// <summary>Loads explicit server configuration without reading production connection settings.</summary>
    /// <returns>The opt-in bridge settings.</returns>
    public static MarketplaceGatewayOptions FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("AETHER_NATIVE_MARKETPLACE_ENABLED") != "true")
        {
            return new MarketplaceGatewayOptions();
        }

        var secret = Environment.GetEnvironmentVariable("AETHER_NATIVE_MARKETPLACE_TOKEN") ?? string.Empty;
        var routing = JsonSerializer.Deserialize<NativeDeliveryPlugInConfiguration>(Environment.GetEnvironmentVariable("AETHER_NATIVE_MARKETPLACE_ROUTING") ?? "{}")
            ?? throw new InvalidOperationException("Marketplace routing is required.");
        if (secret.Length < 32 || routing.Realms.Count == 0 || routing.ConnectionVariables.Count == 0)
        {
            throw new InvalidOperationException("Enabled Marketplace bridge requires a secret and explicit routing.");
        }

        return new MarketplaceGatewayOptions { Enabled = true, Secret = secret, Routing = routing };
    }
}
