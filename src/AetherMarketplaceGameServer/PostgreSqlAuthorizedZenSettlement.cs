// <copyright file="PostgreSqlAuthorizedZenSettlement.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Threading;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using Npgsql;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Settles native Zen and refreshes live actors without overwriting persisted balances.</summary>
/// <remarks>Schedule outside a packet handler's existing player lock. Both actors are locked
/// by character identity before flushing, settling and refreshing their native Money baselines.</remarks>
public sealed class PostgreSqlAuthorizedZenSettlement
{
    private readonly NpgsqlDataSource _source;
    private readonly IGameContext _context;
    private readonly string _realm;
    private readonly IReadOnlyList<IGameContext> _realmContexts;

    /// <summary>Initializes a new instance of the <see cref="PostgreSqlAuthorizedZenSettlement"/> class.</summary>
    /// <param name="source">The trusted settlement data source.</param>
    /// <param name="context">The owning GameServer registry.</param>
    /// <param name="realm">The configured realm.</param>
    /// <param name="realmContexts">All host-owned contexts for this explicitly routed realm.</param>
    public PostgreSqlAuthorizedZenSettlement(NpgsqlDataSource source, IGameContext context, string realm, IEnumerable<IGameContext>? realmContexts = null)
    {
        this._source = source ?? throw new ArgumentNullException(nameof(source));
        this._context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        this._realm = realm;
        this._realmContexts = (realmContexts ?? [context]).Append(context).Distinct().ToArray();
    }

    /// <summary>Settles using native balances and the server-authenticated buyer.</summary>
    /// <param name="buyer">The live buyer.</param>
    /// <param name="purchaseId">The durable purchase id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The durable settlement outcome.</returns>
    public async ValueTask<string> SettleAsync(Player buyer, Guid purchaseId, CancellationToken cancellationToken = default)
    {
        if (buyer.IsPersistenceLockHeld)
        {
            return "DEFER_TO_SERVER_QUEUE";
        }

        await using var lookup = this._source.CreateCommand("SELECT seller_character_id FROM aether_marketplace.purchase_reservation WHERE purchase_id=@id AND realm=@realm AND currency='Zen'");
        lookup.Parameters.AddWithValue("id", purchaseId);
        lookup.Parameters.AddWithValue("realm", this._realm);
        if (await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid sellerId)
        {
            return "PURCHASE_NOT_FOUND";
        }

        var registered = new List<Player>();
        foreach (var context in this._realmContexts)
        {
            registered.AddRange(await context.GetPlayersAsync().ConfigureAwait(false));
        }

        var sellers = registered.Where(p => p.SelectedCharacter?.Id == sellerId).Take(2).ToArray();
        if (sellers.Length > 1 || !registered.Contains(buyer))
        {
            return "UNAUTHORIZED";
        }

        var actors = sellers.Append(buyer).Distinct().OrderBy(p => p.SelectedCharacter?.Id).ToArray();
        if (actors.Any(p => p.IsPersistenceLockHeld))
        {
            return "DEFER_TO_SERVER_QUEUE";
        }

        return await AcquireAsync(0).ConfigureAwait(false);

        async ValueTask<string> AcquireAsync(int index)
        {
            if (index < actors.Length)
            {
                return await actors[index].RunPersistenceExclusiveAsync(() => AcquireAsync(index + 1), cancellationToken).ConfigureAwait(false);
            }

            if (!ReferenceEquals(buyer.GameContext, this._context) || !buyer.IsConnected || buyer.IsTemplatePlayer
                || buyer.PlayerState.CurrentState != PlayerState.EnteredWorld || buyer.Account is not IIdentifiable account
                || buyer.SelectedCharacter is not { Inventory: { } inventory } character)
            {
                return "UNAUTHORIZED";
            }

            await using var identity = this._source.CreateCommand("SELECT store_id FROM aether_marketplace.native_delivery_store WHERE singleton");
            if (await identity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not Guid store
                || !await buyer.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(store, cancellationToken).ConfigureAwait(false))
            {
                return "STORE_MISMATCH";
            }

            foreach (var actor in actors)
            {
                if (!actor.IsConnected || actor.PlayerState.CurrentState != PlayerState.EnteredWorld
                    || !this._realmContexts.Contains(actor.GameContext)
                    || !(await actor.GameContext.GetPlayersAsync().ConfigureAwait(false)).Contains(actor)
                    || (actor != buyer && actor.SelectedCharacter?.Id != sellerId)
                    || !await actor.PersistenceContext.VerifyMarketplaceDeliveryStoreAsync(store, cancellationToken).ConfigureAwait(false))
                {
                    return "UNAUTHORIZED";
                }

                try
                {
                    if (await actor.SaveProgressAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
                catch
                {
                    // A failed flush may have lost its commit acknowledgement. Never retry this dirty actor.
                }

                await actor.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                await actor.DisposeAsync().ConfigureAwait(false);
                return "NATIVE_SAVE_FAILED";
            }

            string result;
            try
            {
                await using var settle = this._source.CreateCommand("SELECT aether_marketplace.settle_authorized_zen_purchase_v30(@id,@realm,@buyer,@account,@maximum)");
                settle.Parameters.AddWithValue("id", purchaseId);
                settle.Parameters.AddWithValue("realm", this._realm);
                settle.Parameters.AddWithValue("buyer", character.Id);
                settle.Parameters.AddWithValue("account", account.Id);
                settle.Parameters.AddWithValue("maximum", this._context.Configuration.MaximumInventoryMoney);
                result = await settle.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                    ?? throw new InvalidOperationException("Missing Zen settlement outcome.");
            }
            finally
            {
                // Even a lost commit acknowledgement requires refreshing both actors.
                // If refresh is uncertain, discard that session without saving it again.
                foreach (var actor in actors)
                {
                    try
                    {
                        if (actor.SelectedCharacter?.Inventory is not { } storage)
                        {
                            throw new InvalidOperationException("Native inventory changed during settlement.");
                        }

                        await actor.PersistenceContext.ReloadInventoryMoneyAsync(storage, CancellationToken.None).ConfigureAwait(false);
                        await actor.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                    }
                    catch
                    {
                        await actor.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
                        await actor.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }

            return result;
        }
    }
}
