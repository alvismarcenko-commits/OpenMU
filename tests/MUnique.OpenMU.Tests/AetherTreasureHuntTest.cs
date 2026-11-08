// <copyright file="AetherTreasureHuntTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Items;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using Moq;

/// <summary>Exercises the Ascension event and native item-pickup path together.</summary>
[TestFixture]
[NonParallelizable]
public class AetherTreasureHuntTest
{
    private string? _previousStaging;
    private string? _previousGuardianHome;

    /// <summary>Use isolated event settings for this fixture only.</summary>
    [SetUp]
    public void SetUp()
    {
        this._previousStaging = Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING");
        this._previousGuardianHome = Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME");
        Environment.SetEnvironmentVariable("AETHER_LOCAL_STAGING", "true");
        Environment.SetEnvironmentVariable("AETHER_GUARDIAN_HOME", null);
    }

    /// <summary>Restore the environment after the event.</summary>
    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("AETHER_LOCAL_STAGING", this._previousStaging);
        Environment.SetEnvironmentVariable("AETHER_GUARDIAN_HOME", this._previousGuardianHome);
    }

    /// <summary>A manual hunt drops one collectible jewel, even with competing pickup requests.</summary>
    [Test]
    public async Task ManualHuntPaysOneNearbyPlayerAsync()
    {
        using var fixture = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var context = (GameContext)fixture.GameContext;
        await using var first = new ConnectedPlayer(context);
        await using var second = new ConnectedPlayer(context);
        await PlayerTestHelper.EnterWorldAsync(first, PlayerTestHelper.CreateCharacter(context)).ConfigureAwait(false);
        await PlayerTestHelper.EnterWorldAsync(second, PlayerTestHelper.CreateCharacter(context)).ConfigureAwait(false);
        var point = new Point(149, 123);
        var map = first.CurrentMap!;
        var jewel = new ItemDefinition { Group = 12, Number = 15, Width = 1, Height = 1, Durability = 1 };
        context.Configuration.Items.Add(jewel);
        var hunt = new AetherTreasureHuntPlugIn
        {
            Configuration = new PeriodicTaskConfiguration { PreStartMessageDelay = TimeSpan.Zero, TaskDuration = TimeSpan.FromMinutes(1), Timetable = [] },
        };

        Assert.That(await hunt.OperateAsync(context, true).ConfigureAwait(false), Is.EqualTo("requested"));
        await hunt.ExecuteTaskAsync(context).ConfigureAwait(false);
        await hunt.ExecuteTaskAsync(context).ConfigureAwait(false);
        var drop = map.GetDropsInRange(point, 0).OfType<DroppedItem>().Single();
        Assert.That(drop.Item.Definition, Is.SameAs(jewel));
        var pickup = new PickupItemAction();
        first.Position = new Point(149, 112);
        await pickup.PickupItemAsync(first, drop.Id).ConfigureAwait(false);
        Assert.That(map.GetDrop(drop.Id), Is.SameAs(drop));

        first.Position = point;
        second.Position = point;
        await Task.WhenAll(pickup.PickupItemAsync(first, drop.Id).AsTask(),
            pickup.PickupItemAsync(second, drop.Id).AsTask()).ConfigureAwait(false);
        var winners = new[] { first, second }.Where(player => player.Inventory!.Items.Any(item => item.Definition == jewel)).ToList();
        Assert.That(winners, Has.Count.EqualTo(1));
        Assert.That(winners[0].Inventory!.Items.Count(item => item.Definition == jewel), Is.EqualTo(1));
        Assert.That(map.GetDrop(drop.Id), Is.Null);
        Mock.Get(winners[0].ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!).Verify(
            view => view.ShowMessageAsync("Treasure Hunt: your Jewel of Chaos was saved.", MessageType.BlueNormal), Times.Once);
        await hunt.ShowStatusAsync(first).ConfigureAwait(false);
        Mock.Get(first.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!).Verify(
            view => view.ShowMessageAsync("The treasure has already been picked up.", MessageType.BlueNormal), Times.Once);
        Assert.That(await hunt.OperateAsync(context, false).ConfigureAwait(false), Is.EqualTo("stopped"));
    }

    private sealed class ConnectedPlayer(IGameContext context) : Player(context), IHasIpAddress
    {
        public string IpAddress => "127.0.0.1";

        protected override ICustomPlugInContainer<IViewPlugIn> CreateViewPlugInContainer()
            => new MockViewPlugInContainer();
    }
}
