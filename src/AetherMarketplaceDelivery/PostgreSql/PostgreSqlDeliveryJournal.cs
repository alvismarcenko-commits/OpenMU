// <copyright file="PostgreSqlDeliveryJournal.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.PostgreSql;

using Npgsql;

/// <summary>
/// Provides PostgreSQL-backed delivery journal operations.
/// </summary>
public sealed class PostgreSqlDeliveryJournal
{
    private readonly NpgsqlDataSource dataSource;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDeliveryJournal"/> class.
    /// </summary>
    /// <param name="dataSource">PostgreSQL data source.</param>
    public PostgreSqlDeliveryJournal(NpgsqlDataSource dataSource)
    {
        this.dataSource = dataSource;
    }

    /// <summary>
    /// Registers a new delivery request.
    /// </summary>
    /// <param name="request">Delivery request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a new row was inserted.</returns>
    public async Task<bool> RegisterAsync(
        DeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await this.dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            DeliveryJournalSql.Insert,
            connection);

        command.Parameters.AddWithValue("delivery_id", request.DeliveryId);
        command.Parameters.AddWithValue("character_id", request.CharacterId);
        command.Parameters.AddWithValue("account_id", request.AccountId);
        command.Parameters.AddWithValue("realm", request.Realm);
        command.Parameters.AddWithValue("item_instance_id", request.ItemInstanceId);

        var result = await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is not null)
        {
            return true;
        }

        await using var lookup = new NpgsqlCommand(
            """
            SELECT character_id, account_id, realm, item_instance_id
            FROM aether_marketplace.delivery_journal
            WHERE delivery_id = @delivery_id
            """,
            connection);

        lookup.Parameters.AddWithValue("delivery_id", request.DeliveryId);

        await using var reader = await lookup
            .ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var existing = new DeliveryRequest(
                request.DeliveryId,
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3));

            DeliveryIdentityGuard.RequireMatch(existing, request);
        }
        else
        {
            throw new InvalidOperationException(
                "Delivery registration conflict requires reconciliation.");
        }

        return false;
    }

    /// <summary>
    /// Atomically claims a pending delivery.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the claim succeeded.</returns>
    public async Task<bool> ClaimAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        return await this.ExecuteStateChangeAsync(
            DeliveryJournalSql.Claim,
            deliveryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks a processing delivery as requiring reconciliation.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the state was changed.</returns>
    public async Task<bool> RequireReconciliationAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        return await this.ExecuteStateChangeAsync(
            DeliveryJournalSql.RequireReconciliation,
            deliveryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records confirmed inventory delivery.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the state was changed.</returns>
    public async Task<bool> ConfirmDeliveredAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        return await this.ExecuteStateChangeAsync(
            DeliveryJournalSql.ConfirmDelivered,
            deliveryId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records insufficient inventory capacity.
    /// </summary>
    /// <param name="deliveryId">Delivery identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the state was changed.</returns>
    public async Task<bool> MarkInventoryFullAsync(
        Guid deliveryId,
        CancellationToken cancellationToken = default)
    {
        return await this.ExecuteStateChangeAsync(
            DeliveryJournalSql.InventoryFull,
            deliveryId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteStateChangeAsync(
        string sql,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await this.dataSource
            .OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("delivery_id", deliveryId);

        var result = await command
            .ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is not null;
    }
}
