// <copyright file="PostgreSqlAuthorizedAcSettlement.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using System.Threading;
using MUnique.OpenMU.GameLogic;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Settles AC using live server identity and a single atomic database statement.</summary>
public sealed class PostgreSqlAuthorizedAcSettlement
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _realm;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlAuthorizedAcSettlement"/> class.</summary>
    /// <param name="dataSource">The trusted settlement service data source.</param>
    /// <param name="realm">The server-configured realm.</param>
    public PostgreSqlAuthorizedAcSettlement(NpgsqlDataSource dataSource, string realm)
    {
        this._dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        this._realm = realm;
    }

    /// <summary>Authorizes and settles a purchase without accepting client identity or payment proof.</summary>
    /// <param name="player">The authenticated server-owned player.</param>
    /// <param name="purchaseId">The durable purchase identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The database outcome. Transport errors require retry with the same purchase identifier.</returns>
    public async Task<string> SettleAsync(Player? player, Guid purchaseId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (player is null || purchaseId == Guid.Empty)
        {
            return "UNAUTHORIZED";
        }

        return await player.RunPersistenceExclusiveAsync(
            async () =>
            {
                if (!player.IsConnected || player.IsTemplatePlayer
                    || player.PlayerState.CurrentState != PlayerState.EnteredWorld
                    || player.Account is not IIdentifiable account
                    || player.SelectedCharacter is not { } character
                    || account.Id == Guid.Empty || character.Id == Guid.Empty)
                {
                    return "UNAUTHORIZED";
                }

                await using var command = this._dataSource.CreateCommand("""
                    SELECT aether_marketplace.settle_authorized_ac_purchase_v4(
                        @purchase, @realm, @character, @account)
                    """);
                command.Parameters.AddWithValue("purchase", purchaseId);
                command.Parameters.AddWithValue("realm", this._realm);
                command.Parameters.AddWithValue("character", character.Id);
                command.Parameters.AddWithValue("account", account.Id);
                return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                    ?? throw new InvalidOperationException("Settlement returned no outcome.");
            },
            cancellationToken).ConfigureAwait(false);
    }
}
