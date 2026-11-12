// <copyright file="PostgreSqlReservedNativeItemSource.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using System.Text.Json;
using System.Threading;
using AetherMarketplaceDelivery;
using Npgsql;
using NpgsqlTypes;

/// <summary>Reads trusted purchase snapshots and durably prevents a second native attempt across processes.</summary>
public sealed class PostgreSqlReservedNativeItemSource : IReservedNativeItemSource
{
    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlReservedNativeItemSource"/> class.</summary>
    /// <param name="dataSource">A server-owned data source with read and claim privileges only.</param>
    public PostgreSqlReservedNativeItemSource(NpgsqlDataSource dataSource)
    {
        this._dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    /// <inheritdoc />
    public async ValueTask<ReservedNativeItem?> ReadAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        await using var command = this._dataSource.CreateCommand("""
            SELECT snapshot::text FROM aether_marketplace.native_item_reservation
            WHERE delivery_id = @id AND native_started_at IS NULL
            """);
        command.Parameters.AddWithValue("id", request.DeliveryId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string json ? JsonSerializer.Deserialize<ReservedNativeItem>(json) : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryBeginAsync(ReservedNativeItem reservation, CancellationToken cancellationToken)
    {
        // The journal claim and this independent one-shot claim are both required.
        // Snapshot equality closes changes between lookup and native execution.
        await using var command = this._dataSource.CreateCommand("""
            UPDATE aether_marketplace.native_item_reservation AS r
            SET native_started_at = now()
            FROM aether_marketplace.delivery_journal AS j
            WHERE r.delivery_id = @id AND j.delivery_id = r.delivery_id
              AND r.native_started_at IS NULL AND r.snapshot = @snapshot
              AND j.state = 'PROCESSING'
              AND j.account_id = @account AND j.character_id = @character
              AND j.realm = @realm AND j.item_instance_id = @item
            RETURNING r.delivery_id
            """);
        var request = reservation.Request;
        command.Parameters.AddWithValue("id", request.DeliveryId);
        command.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(reservation));
        command.Parameters.AddWithValue("account", request.AccountId);
        command.Parameters.AddWithValue("character", request.CharacterId);
        command.Parameters.AddWithValue("realm", request.Realm);
        command.Parameters.AddWithValue("item", request.ItemInstanceId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid;
    }
}
