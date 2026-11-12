// <copyright file="DeliveryJournalSql.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceDelivery.PostgreSql;

/// <summary>
/// Defines parameterized SQL commands for delivery journal operations.
/// </summary>
public static class DeliveryJournalSql
{
    /// <summary>
    /// Registers a delivery if its identifier is unused.
    /// </summary>
    public const string Insert = """
        INSERT INTO aether_marketplace.delivery_journal
        (delivery_id, character_id, account_id, realm, item_instance_id)
        VALUES (@delivery_id, @character_id, @account_id, @realm, @item_instance_id)
        ON CONFLICT DO NOTHING
        RETURNING delivery_id;
        """;

    /// <summary>
    /// Reads a delivery by its identifier.
    /// </summary>
    public const string Read = """
        SELECT delivery_id, character_id, account_id,
               realm, item_instance_id, state, attempt_count
        FROM aether_marketplace.delivery_journal
        WHERE delivery_id = @delivery_id;
        """;

    /// <summary>
    /// Claims a pending delivery for processing.
    /// </summary>
    public const string Claim = """
        UPDATE aether_marketplace.delivery_journal
        SET state = 'PROCESSING',
            attempt_count = attempt_count + 1,
            updated_at = now()
        WHERE delivery_id = @delivery_id
          AND state = 'PENDING'
        RETURNING delivery_id;
        """;

    /// <summary>
    /// Marks a processing delivery as needing reconciliation.
    /// </summary>
    public const string RequireReconciliation = """
        UPDATE aether_marketplace.delivery_journal
        SET state = 'RECONCILIATION_REQUIRED',
            updated_at = now()
        WHERE delivery_id = @delivery_id
          AND state = 'PROCESSING'
        RETURNING delivery_id;
        """;

    /// <summary>
    /// Records confirmed delivery.
    /// </summary>
    public const string ConfirmDelivered = """
        UPDATE aether_marketplace.delivery_journal
        SET state = 'DELIVERED',
            delivered_at = now(),
            updated_at = now()
        WHERE delivery_id = @delivery_id
          AND state = 'PROCESSING'
        RETURNING delivery_id;
        """;

    /// <summary>
    /// Records insufficient inventory capacity.
    /// </summary>
    public const string InventoryFull = """
        UPDATE aether_marketplace.delivery_journal
        SET state = 'INVENTORY_FULL',
            updated_at = now()
        WHERE delivery_id = @delivery_id
          AND state = 'PROCESSING'
        RETURNING delivery_id;
        """;
}
