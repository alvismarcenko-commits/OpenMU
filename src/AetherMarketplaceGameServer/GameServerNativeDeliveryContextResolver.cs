// <copyright file="GameServerNativeDeliveryContextResolver.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer;

using System.Threading;
using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Marketplace;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;

/// <summary>Resolves real registered players and executes trusted reservations through native inventory persistence.</summary>
public sealed class GameServerNativeDeliveryContextResolver : INativeDeliveryContextResolver
{
    private readonly IGameContext _context;
    private readonly string _realm;
    private readonly IReservedNativeItemSource _reservations;

    /// <summary>Initializes a new instance of the <see cref="GameServerNativeDeliveryContextResolver"/> class.</summary>
    /// <param name="context">The owning GameServer.Context player registry.</param>
    /// <param name="realm">The server-configured realm, never a request-derived value.</param>
    /// <param name="reservations">The trusted durable purchase snapshot source.</param>
    public GameServerNativeDeliveryContextResolver(IGameContext context, string realm, IReservedNativeItemSource reservations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        this._context = context ?? throw new ArgumentNullException(nameof(context));
        this._realm = realm;
        this._reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
    }

    /// <inheritdoc />
    public async Task<NativeDeliveryContext?> ResolveAsync(DeliveryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.DeliveryId == Guid.Empty || request.AccountId <= 0 || request.CharacterId == Guid.Empty
            || request.Realm != this._realm || !Guid.TryParse(request.ItemInstanceId, out var itemId) || itemId == Guid.Empty)
        {
            return null;
        }

        var reservation = await this._reservations.ReadAsync(request, cancellationToken).ConfigureAwait(false);
        if (reservation is null || reservation.Request != request || reservation.Specification is null || reservation.Identity is null)
        {
            return null;
        }

        var players = await this._context.GetPlayersAsync().ConfigureAwait(false);
        var matches = players.Where(p => p.SelectedCharacter?.Id == request.CharacterId).Take(2).ToArray();
        if (matches.Length != 1 || !this.Matches(matches[0], reservation))
        {
            return null;
        }

        var player = matches[0];
        return new NativeDeliveryContext(reservation.Identity, reservation.Identity.OpenMuAccountId, request.CharacterId, this._realm, ExecuteAsync);

        async Task<NativeInventoryDeliveryResult> ExecuteAsync(Guid reservedId, CancellationToken token)
        {
            // Reuse the player's existing persistence lock. Its async-flow reentrancy permits
            // the native delivery's inline SaveProgressAsync without taking a second lock.
            return await player.RunPersistenceExclusiveAsync(
                async () =>
                {
                    token.ThrowIfCancellationRequested();
                    var currentPlayers = await this._context.GetPlayersAsync().ConfigureAwait(false);
                    if (reservedId != itemId || !this.Matches(player, reservation)
                        || currentPlayers.Count(p => p.SelectedCharacter?.Id == request.CharacterId) != 1
                        || !currentPlayers.Contains(player))
                    {
                        return new NativeInventoryDeliveryResult(NativeInventoryDeliveryStatus.InvalidTarget, null);
                    }

                    if (!await this._reservations.TryBeginAsync(reservation, token).ConfigureAwait(false))
                    {
                        return new NativeInventoryDeliveryResult(NativeInventoryDeliveryStatus.ReconciliationRequired, null);
                    }

                    if (!this.Matches(player, reservation))
                    {
                        return new NativeInventoryDeliveryResult(NativeInventoryDeliveryStatus.ReconciliationRequired, null);
                    }

                    // The durable claim survives cancellation, process loss and save ambiguity.
                    // It must never be released automatically, even when inventory is full.
                    var result = await MarketplaceInventoryDelivery.DeliverReservedItemAsync(
                        player, request.CharacterId, reservation.Specification, reservedId, token).ConfigureAwait(false);
                    var status = result.Status switch
                    {
                        MarketplaceNativeStatus.Delivered => NativeInventoryDeliveryStatus.Delivered,
                        MarketplaceNativeStatus.InventoryFull => NativeInventoryDeliveryStatus.InventoryFull,
                        _ => NativeInventoryDeliveryStatus.ReconciliationRequired,
                    };
                    return new NativeInventoryDeliveryResult(status, result.ItemInstanceId);
                },
                token).ConfigureAwait(false);
        }
    }

    private bool Matches(Player player, ReservedNativeItem reservation)
    {
        return ReferenceEquals(player.GameContext, this._context)
            && player.IsConnected && !player.IsTemplatePlayer
            && player.PlayerState.CurrentState == PlayerState.EnteredWorld
            && player.Account is IIdentifiable account && player.SelectedCharacter is { } character
            && NativeAccountIdentityBridge.Matches(reservation.Request, reservation.Identity, account.Id, character.Id, this._realm);
    }
}
