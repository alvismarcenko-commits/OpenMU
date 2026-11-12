// <copyright file="NativeIntegrationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer.Tests;

using System.Threading;
using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Interfaces;
using IIdentifiable = MUnique.OpenMU.Persistence.IIdentifiable;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Tests;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

[TestFixture]
public sealed class NativeIntegrationTests
{
    [Test]
    public async Task NativeInventoryUsesReservedIdentityAndSpecificationOnceAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        var gateway = Gateway(player, source);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => gateway.DeliverAsync(reservation.Request))).ConfigureAwait(false);
        Assert.That(results.Count(r => r.Status == NativeInventoryDeliveryStatus.Delivered), Is.EqualTo(1));
        var item = player.Inventory!.Items.Single();
        Assert.That(((IIdentifiable)item).Id.ToString("D"), Is.EqualTo(reservation.Request.ItemInstanceId));
        Assert.That(item.Level, Is.EqualTo(reservation.Specification.Level));
        var restartedGateway = Gateway(player, source);
        Assert.That((await restartedGateway.DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.Not.EqualTo(NativeInventoryDeliveryStatus.Delivered));
        Assert.That(player.Inventory.Items.Count(), Is.EqualTo(1));
    }

    [TestCase("account")]
    [TestCase("live-account")]
    [TestCase("binding")]
    [TestCase("character")]
    [TestCase("realm")]
    [TestCase("item")]
    [TestCase("delivery")]
    [TestCase("offline")]
    [TestCase("template")]
    [TestCase("missing")]
    [TestCase("duplicate-player")]
    public async Task UntrustedTargetCannotMutateInventoryAsync(string mismatch)
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        var request = reservation.Request;
        switch (mismatch)
        {
            case "account": request = request with { AccountId = request.AccountId + 1 }; break;
            case "live-account": player.Account = new BasicModel.Account { Id = Guid.NewGuid() }; break;
            case "binding": source.Reservation = reservation with { Identity = reservation.Identity with { OpenMuAccountId = Guid.NewGuid() } }; break;
            case "character": request = request with { CharacterId = Guid.NewGuid() }; break;
            case "realm": request = request with { Realm = "other" }; break;
            case "item": request = request with { ItemInstanceId = Guid.NewGuid().ToString("D") }; break;
            case "delivery": request = request with { DeliveryId = Guid.NewGuid() }; break;
            case "offline": await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false); break;
            case "template": player.Account!.IsTemplate = true; break;
            case "missing": source.Reservation = null; break;
            case "duplicate-player": await player.GameContext.AddPlayerAsync(player).ConfigureAwait(false); break;
        }

        Assert.That((await Gateway(player, source).DeliverAsync(request).ConfigureAwait(false)).Status, Is.EqualTo(NativeInventoryDeliveryStatus.InvalidTarget));
        Assert.That(player.Inventory!.Items, Is.Empty);
        Assert.That(source.Starts, Is.Zero);
    }

    [Test]
    public async Task ItemThatCannotFitDoesNotMutateInventoryAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        player.GameContext.Configuration.Items.Single().Width = 9;
        Assert.That((await Gateway(player, source).DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.EqualTo(NativeInventoryDeliveryStatus.InventoryFull));
        Assert.That(player.Inventory!.Items, Is.Empty);
        Assert.That(source.Starts, Is.EqualTo(1));
        Assert.That((await Gateway(player, source).DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.Not.EqualTo(NativeInventoryDeliveryStatus.Delivered));
    }

    [Test]
    public async Task CancellationBeforeExecutionDoesNotConsumeReservationAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        var resolver = new GameServerNativeDeliveryContextResolver(player.GameContext, "test", source);
        var context = await resolver.ResolveAsync(reservation.Request).ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await context!.DeliverReservedAsync(Guid.Parse(reservation.Request.ItemInstanceId), cancellation.Token).ConfigureAwait(false));
        Assert.That(source.Starts, Is.Zero);
        Assert.That(player.Inventory!.Items, Is.Empty);
    }

    [Test]
    public async Task AccountChangeAfterResolutionIsRejectedInsidePersistenceLockAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        var resolver = new GameServerNativeDeliveryContextResolver(player.GameContext, "test", source);
        var context = await resolver.ResolveAsync(reservation.Request).ConfigureAwait(false);
        Assert.That(context, Is.Not.Null);
        player.Account = new BasicModel.Account { Id = Guid.NewGuid() };
        var result = await context!.DeliverReservedAsync(Guid.Parse(reservation.Request.ItemInstanceId), CancellationToken.None).ConfigureAwait(false);
        Assert.That(result.Status, Is.EqualTo(NativeInventoryDeliveryStatus.InvalidTarget));
        Assert.That(source.Starts, Is.Zero);
        Assert.That(player.Inventory!.Items, Is.Empty);
    }

    [Test]
    public async Task SaveExceptionRequiresReconciliationAndNeverRetriesAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        ((InMemoryContext)player.PersistenceContext).SavedChanges += (_, _) => throw new InvalidOperationException("Lost save acknowledgement");
        var gateway = Gateway(player, source);
        Assert.That((await gateway.DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
        Assert.That((await Gateway(player, source).DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.Not.EqualTo(NativeInventoryDeliveryStatus.Delivered));
        Assert.That(player.Inventory!.Items.Count(), Is.EqualTo(1));
        Assert.That(source.Starts, Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidSpecificationDoesNotCreateAnItemAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        source.Reservation = reservation with { Specification = new ItemSpecification(15, 999) };
        Assert.That((await Gateway(player, source).DeliverAsync(reservation.Request).ConfigureAwait(false)).Status, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
        Assert.That(player.Inventory!.Items, Is.Empty);
    }

    [Test]
    public async Task SnapshotChangeBeforeClaimFailsClosedAsync()
    {
        var (player, reservation, source) = await CreateAsync().ConfigureAwait(false);
        var resolver = new GameServerNativeDeliveryContextResolver(player.GameContext, "test", source);
        var context = await resolver.ResolveAsync(reservation.Request).ConfigureAwait(false);
        source.Reservation = reservation with { Specification = new ItemSpecification(0, 1, 2) };
        var result = await context!.DeliverReservedAsync(Guid.Parse(reservation.Request.ItemInstanceId), CancellationToken.None).ConfigureAwait(false);
        Assert.That(result.Status, Is.EqualTo(NativeInventoryDeliveryStatus.ReconciliationRequired));
        Assert.That(player.Inventory!.Items, Is.Empty);
    }

    private static NativeDeliveryGateway Gateway(Player player, IReservedNativeItemSource source)
        => new(new GameServerNativeDeliveryContextResolver(player.GameContext, "test", source));

    private static async Task<(Player Player, ReservedNativeItem Reservation, TestReservations Source)> CreateAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.Account = new BasicModel.Account { Id = Guid.NewGuid() };
        player.SelectedCharacter!.Id = Guid.NewGuid();
        player.GameContext.Configuration.Items.Add(new BasicModel.ItemDefinition
        {
            Group = 0, Number = 1, Width = 1, Height = 1, MaximumItemLevel = 15, Durability = 100,
        });
        await player.GameContext.AddPlayerAsync(player).ConfigureAwait(false);
        var request = new DeliveryRequest(Guid.NewGuid(), player.SelectedCharacter.Id, 7, "test", Guid.NewGuid().ToString("D"));
        var reservation = new ReservedNativeItem(request, new NativeAccountIdentity(7, ((IIdentifiable)player.Account).Id, request.CharacterId, "test"), new ItemSpecification(0, 1, 3));
        return (player, reservation, new TestReservations { Reservation = reservation });
    }

    private sealed class TestReservations : IReservedNativeItemSource
    {
        private int _started;

        public ReservedNativeItem? Reservation { get; set; }

        public int Starts => this._started;

        public ValueTask<ReservedNativeItem?> ReadAsync(DeliveryRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(this._started == 0 ? this.Reservation : null);

        public ValueTask<bool> TryBeginAsync(ReservedNativeItem reservation, CancellationToken cancellationToken)
            => ValueTask.FromResult(this.Reservation == reservation && Interlocked.CompareExchange(ref this._started, 1, 0) == 0);
    }
}
