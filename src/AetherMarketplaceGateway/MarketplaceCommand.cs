// <copyright file="MarketplaceCommand.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGateway;

/// <summary>Trusted website-backend request; the browser cannot choose WebsiteAccountId.</summary>
public sealed class MarketplaceCommand
{
    /// <summary>Gets or sets the owner resolved by the existing website session authority.</summary>
    public long WebsiteAccountId { get; set; }

    /// <summary>Gets or sets the explicitly configured realm key.</summary>
    public string Realm { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected native character.</summary>
    public Guid CharacterId { get; set; }

    /// <summary>Gets or sets the durable operation key.</summary>
    public Guid OperationId { get; set; }

    /// <summary>Gets or sets the target listing.</summary>
    public Guid ListingId { get; set; }

    /// <summary>Gets or sets the original native item.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the approved currency name.</summary>
    public string? Currency { get; set; }

    /// <summary>Gets or sets the integer price as lossless decimal text.</summary>
    public string? Price { get; set; }

    /// <summary>Gets or sets the listing search text.</summary>
    public string? Search { get; set; }
}
