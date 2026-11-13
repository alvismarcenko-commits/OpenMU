// <copyright file="NativeDeliveryPlugInConfiguration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

/// <summary>Explicit realm routing and secret references for opt-in recovery.</summary>
public sealed class NativeDeliveryPlugInConfiguration
{
    /// <summary>Gets or sets the server-id to realm mapping.</summary>
    public Dictionary<byte, string> Realms { get; set; } = new();

    /// <summary>Gets or sets environment variable names holding each realm's trusted connection.</summary>
    public Dictionary<string, string> ConnectionVariables { get; set; } = new();
}
