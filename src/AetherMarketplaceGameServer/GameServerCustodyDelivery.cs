// <copyright file="GameServerCustodyDelivery.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Threading;
using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Marketplace;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Reconciles original native entities using database-backed receipts and worker fencing.</summary>
/// <remarks>Requires the custody guard in the same PostgreSQL database as native persistence.
/// The connection must be server-owned; neither this service nor its coordinator is a client API.</remarks>
public sealed class GameServerCustodyDelivery : ISafeDeliveryJournal, IRecoverableSafeNativeDelivery
{
    private readonly NpgsqlDataSource _source;
    private readonly IGameContext _context;
    private readonly string _realm;

    /// <summary>Initializes a new instance of the <see cref="GameServerCustodyDelivery"/> class.</summary>
    /// <param name="source">The trusted native database data source.</param>
    /// <param name="context">The owning GameServer player registry.</param>
    /// <param name="realm">The server-configured realm.</param>
    public GameServerCustodyDelivery(NpgsqlDataSource source, IGameContext context, string realm)
    {
        this._source = source ?? throw new ArgumentNullException(nameof(source));
        this._context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        this._realm = realm;
    }

    /// <inheritdoc />
    public async ValueTask<SafeDeliveryReceipt?> FindAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var command = this._source.CreateCommand("""
            SELECT buyer_character_id,item_id,status FROM aether_marketplace.native_custody_delivery
            WHERE purchase_id=@id AND realm=@realm
            """);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("realm", this._realm);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new SafeDeliveryReceipt(
                deliveryId,
                reader.GetGuid(0),
                reader.GetGuid(1).ToString("D"),
                reader.GetString(2) == "COMPLETED" ? SafeDeliveryStatus.Delivered : SafeDeliveryStatus.ReconciliationRequired)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryReserveAsync(Guid deliveryId, Guid characterId, string itemInstanceId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(itemInstanceId, out var itemId))
        {
            return false;
        }

        await using var command = this._source.CreateCommand("""
            SELECT aether_marketplace.authorize_native_delivery_v29(@id)
            WHERE EXISTS (SELECT 1 FROM aether_marketplace.purchase_reservation p
                JOIN aether_marketplace.native_item_custody c ON c.listing_id=p.listing_id
                WHERE p.purchase_id=@id AND p.realm=@realm AND p.buyer_character_id=@buyer AND c.item_instance_id=@item)
            """);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("realm", this._realm);
        command.Parameters.AddWithValue("buyer", characterId);
        command.Parameters.AddWithValue("item", itemId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var receipt = await this.FindAsync(deliveryId, cancellationToken).ConfigureAwait(false);
        return result is "AUTHORIZED" or "ALREADY_AUTHORIZED"
            && receipt?.CharacterId == characterId && receipt.ItemInstanceId == itemInstanceId;
    }

    /// <inheritdoc />
    public async ValueTask MarkAsync(SafeDeliveryReceipt receipt, CancellationToken cancellationToken = default)
    {
        // Application acknowledgements cannot manufacture durable completion.
        if (receipt.Status == SafeDeliveryStatus.Delivered
            && (await this.FindAsync(receipt.DeliveryId, cancellationToken).ConfigureAwait(false))?.Status != SafeDeliveryStatus.Delivered)
        {
            throw new InvalidOperationException("Native ownership commit has not been confirmed.");
        }
    }

    /// <inheritdoc />
    public async ValueTask<SafeDeliveryStatus> DeliverAsync(Guid deliveryId, Guid characterId, string itemInstanceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await this._source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var fence = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var claim = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@id,29))", connection, fence);
        claim.Parameters.AddWithValue("id", deliveryId.ToString("D"));
        if (await claim.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            return SafeDeliveryStatus.ReconciliationRequired;
        }

        var receipt = await this.FindAsync(deliveryId, cancellationToken).ConfigureAwait(false);
        if (receipt?.CharacterId != characterId || receipt.ItemInstanceId != itemInstanceId)
        {
            return SafeDeliveryStatus.Rejected;
        }

        if (receipt.Status == SafeDeliveryStatus.Delivered)
        {
            return SafeDeliveryStatus.Delivered;
        }

        await using var command = this._source.CreateCommand("""
            SELECT d.buyer_account_id,d.escrow_storage_id,b.marketplace_account_id,s.store_id
            FROM aether_marketplace.native_custody_delivery d
            CROSS JOIN aether_marketplace.native_delivery_store s
            JOIN aether_marketplace.native_account_binding b
              ON b.realm=d.realm AND b.character_id=d.buyer_character_id AND b.openmu_account_id=d.buyer_account_id
            WHERE d.purchase_id=@id AND d.realm=@realm AND d.status='PENDING'
              AND aether_marketplace.verify_native_settlement_v30(d.purchase_id)='ACCOUNTING_PROOF_VALID'
            """);
        command.Parameters.AddWithValue("id", deliveryId);
        command.Parameters.AddWithValue("realm", this._realm);
        Guid account;
        Guid escrow;
        long marketplaceAccount;
        Guid storeId;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return SafeDeliveryStatus.Rejected;
            }

            account = reader.GetGuid(0);
            escrow = reader.GetGuid(1);
            marketplaceAccount = reader.GetInt64(2);
            storeId = reader.GetGuid(3);
        }

        var players = (await this._context.GetPlayersAsync().ConfigureAwait(false)).Where(p => p.SelectedCharacter?.Id == characterId).Take(2).ToArray();
        if (players.Length != 1)
        {
            return SafeDeliveryStatus.ReconciliationRequired;
        }

        var player = players[0];
        return await player.RunPersistenceExclusiveAsync(
            async () =>
        {
            var request = new DeliveryRequest(deliveryId, characterId, marketplaceAccount, this._realm, itemInstanceId);
            var identity = new NativeAccountIdentity(marketplaceAccount, account, characterId, this._realm);
            var currentPlayers = await this._context.GetPlayersAsync().ConfigureAwait(false);
            if (!ReferenceEquals(player.GameContext, this._context) || player.Account is not IIdentifiable liveAccount
                || currentPlayers.Count(p => p.SelectedCharacter?.Id == characterId) != 1 || !currentPlayers.Contains(player)
                || !NativeAccountIdentityBridge.Matches(request, identity, liveAccount.Id, player.SelectedCharacter?.Id ?? Guid.Empty, this._realm)
                || !await player.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(storeId, cancellationToken).ConfigureAwait(false))
            {
                return SafeDeliveryStatus.Rejected;
            }

            var result = await MarketplaceInventoryDelivery.TransferExistingItemAsync(player, characterId, escrow, Guid.Parse(itemInstanceId), account, cancellationToken).ConfigureAwait(false);
            if (result.Status == MarketplaceNativeStatus.SaveUncertain)
            {
                // Never reuse a change tracker after an ambiguous ownership save.
                await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                await player.DisposeAsync().ConfigureAwait(false);
            }

            return (await this.FindAsync(deliveryId, cancellationToken).ConfigureAwait(false))?.Status == SafeDeliveryStatus.Delivered
                ? SafeDeliveryStatus.Delivered : SafeDeliveryStatus.ReconciliationRequired;
        },
            cancellationToken).ConfigureAwait(false);
    }
}
