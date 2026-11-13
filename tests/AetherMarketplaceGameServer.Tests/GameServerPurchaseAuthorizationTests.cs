// <copyright file="GameServerPurchaseAuthorizationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace AetherMarketplaceGameServer.Tests;

using AetherMarketplaceDelivery;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Tests;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

[TestFixture]
public sealed class GameServerPurchaseAuthorizationTests
{
    [Test]
    public async Task ConnectedMatchingPlayerIsAcceptedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        Assert.That(Check(player, identity), Is.True);
    }

    [Test]
    public async Task DifferentAccountIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        player.Account = new BasicModel.Account { Id = Guid.NewGuid() };
        Assert.That(Check(player, identity), Is.False);
    }

    [Test]
    public async Task DifferentCharacterIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        player.SelectedCharacter!.Id = Guid.NewGuid();
        Assert.That(Check(player, identity), Is.False);
    }

    [Test]
    public async Task DifferentRealmIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        Assert.That(Check(player, identity, "Evolution"), Is.False);
    }

    [Test]
    public async Task DisconnectedPlayerIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.Disconnected).ConfigureAwait(false);
        Assert.That(Check(player, identity), Is.False);
    }

    [Test]
    public async Task TemplateAccountIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        player.Account!.IsTemplate = true;
        Assert.That(Check(player, identity), Is.False);
    }

    [Test]
    public async Task EmptyPurchaseIsRejectedAsync()
    {
        var (player, identity) = await CreateAsync().ConfigureAwait(false);
        Assert.That(Check(player, identity, purchaseId: Guid.Empty), Is.False);
    }

    [Test]
    public void MissingPlayerIsRejected()
    {
        Assert.That(
            GameServerPurchaseAuthorization.IsAuthorized(
                null, Guid.NewGuid(), Guid.NewGuid(), null, "Ascension"),
            Is.False);
    }

    private static bool Check(
        Player player,
        NativeAccountIdentity identity,
        string realm = "Ascension",
        Guid? purchaseId = null)
        => GameServerPurchaseAuthorization.IsAuthorized(
            player,
            purchaseId ?? Guid.NewGuid(),
            identity.CharacterId,
            identity,
            realm);

    private static async Task<(Player Player, NativeAccountIdentity Identity)> CreateAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var accountId = Guid.NewGuid();
        var characterId = Guid.NewGuid();
        player.Account = new BasicModel.Account { Id = accountId };
        player.SelectedCharacter!.Id = characterId;

        return (
            player,
            new NativeAccountIdentity(1001, accountId, characterId, "Ascension"));
    }
}