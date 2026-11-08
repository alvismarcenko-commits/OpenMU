// <copyright file="GrandResetCharacterActionTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Moq;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Regression tests for the GR action and the atomic APP boundary; persistence here is a test double.
/// </summary>
[TestFixture]
public class GrandResetCharacterActionTest
{
    /// <summary>
    /// Verifies reset boundaries, zero stat bonus, inventory and legacy-character quarantine through the real action.
    /// </summary>
    /// <param name="resets">Available resets.</param>
    /// <param name="grandResets">Existing Grand Resets.</param>
    /// <param name="expectedResets">Remaining resets.</param>
    /// <param name="expectedGrandResets">Resulting Grand Resets.</param>
    /// <param name="expectedPoints">Resulting assignable points.</param>
    [TestCase(99, 0, 99, 0, 42)]
    [TestCase(100, 0, 0, 0, 0)]
    [TestCase(101, 0, 0, 0, 0)]
    [TestCase(250, 0, 0, 0, 0)]
    [TestCase(100, 9999, 100, 9999, 42)]
    [TestCase(100, 19, 100, 19, 42)]
    [TestCase(100, 20, 100, 20, 42)]
    public async Task ExchangeAsync(int resets, int grandResets, int expectedResets, int expectedGrandResets, int expectedPoints)
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = resets;
        player.Attributes[Stats.GrandResets] = grandResets;
        player.SelectedCharacter!.LevelUpPoints = 42;
        var inventory = player.SelectedCharacter.Inventory;
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new ItemDefinition { Group = 14, Number = 13, Width = 1, Height = 1 };
        item.ItemSlot = 20;
        item.Level = 7;
        item.Durability = 23;
        inventory!.Items.Add(item);
        var equipped = player.PersistenceContext.CreateNew<Item>();
        equipped.Definition = new ItemDefinition { Group = 0, Number = 0, Width = 1, Height = 3 };
        equipped.ItemSlot = 0;
        equipped.Level = 9;
        equipped.Durability = 42;
        inventory.Items.Add(equipped);
        var originalItems = inventory.Items.ToArray();

        await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);

        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(expectedResets));
        Assert.That(player.Attributes[Stats.GrandResets], Is.EqualTo(expectedGrandResets));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(expectedPoints));
        Assert.That(player.SelectedCharacter.Attributes.Single(a => a.Definition == Stats.GrandResets).Value, Is.EqualTo(expectedGrandResets));
        Assert.That(player.SelectedCharacter.Inventory, Is.SameAs(inventory));
        Assert.That(inventory.Items, Is.EquivalentTo(originalItems));
        Assert.That(item.ItemSlot, Is.EqualTo(20));
        Assert.That(item.Level, Is.EqualTo(7));
        Assert.That(item.Durability, Is.EqualTo(23));
        Assert.That(equipped.ItemSlot, Is.Zero);
        Assert.That(equipped.Level, Is.EqualTo(9));
        Assert.That(equipped.Durability, Is.EqualTo(42));
        if (resets >= 100 && grandResets == 0)
        {
            Assert.That(player.Attributes[Stats.Level], Is.EqualTo(1));
            Assert.That(player.SelectedCharacter.Experience, Is.Zero);
            foreach (var stat in player.SelectedCharacter.CharacterClass!.StatAttributes.Where(s => s.IncreasableByPlayer))
            {
                Assert.That(player.Attributes[stat.Attribute], Is.EqualTo(stat.BaseValue));
            }
        }
    }

    /// <summary>
    /// Verifies the opt-out profile does not mutate player progress.
    /// </summary>
    [Test]
    public async Task DisabledProfileRejectsExchangeAsync()
    {
        var player = await CreatePlayerAsync(false).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 100;
        var action = new GrandResetCharacterAction(player);
        Assert.That(action.CanGrandReset(), Is.False);
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
    }

    /// <summary>
    /// Separate action instances must share the player lock and consume a block only once.
    /// </summary>
    [Test]
    public async Task ConcurrentAndRepeatedRequestsConsumeOnceAsync()
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 250;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, 16).Select(async _ =>
        {
            await start.Task.ConfigureAwait(false);
            await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
        }).ToArray();
        start.SetResult();
        await Task.WhenAll(requests).ConfigureAwait(false);
        await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.Zero);
        Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.Zero);
        var context = (AtomicTestContext)player.PersistenceContext;
        Assert.That(context.Exchanges, Is.EqualTo(1));
        Assert.That(context.App, Is.EqualTo(new System.Numerics.BigInteger(100)));
    }

    /// <summary>
    /// A normal reset awards only its own configured points, without a permanent GR bonus.
    /// </summary>
    [Test]
    public async Task NormalResetNeverAddsGrandResetBonusAsync()
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 100;
        await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
        player.SelectedCharacter!.LevelUpPoints = 0;
        player.Attributes[Stats.BaseStrength] += 100;
        player.Attributes[Stats.Level] = 400;
        await new ResetCharacterAction(player).ResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(500));
        Assert.That(player.Attributes[Stats.Level], Is.EqualTo(10));
    }

    /// <summary>
    /// A failed save restores the counter, level, base attributes and assignable points.
    /// </summary>
    [Test]
    public async Task SaveFailureRestoresProgressAsync()
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 100;
        player.Attributes[Stats.GrandResets] = 0;
        player.Attributes[Stats.Level] = 400;
        player.Attributes[Stats.BaseStrength] = 123;
        player.SelectedCharacter!.LevelUpPoints = 42;
        player.SelectedCharacter.Experience = 99;
        var inventory = player.SelectedCharacter.Inventory!;
        var equipped = player.PersistenceContext.CreateNew<Item>();
        equipped.Definition = new ItemDefinition { Group = 0, Number = 0, Width = 1, Height = 3 };
        equipped.ItemSlot = 0;
        equipped.Level = 9;
        equipped.Durability = 42;
        inventory.Items.Add(equipped);
        var originalItems = inventory.Items.ToArray();
        var context = (MUnique.OpenMU.Persistence.InMemory.InMemoryContext)player.PersistenceContext;
        context.SavedChanges += (_, _) => throw new InvalidOperationException("Simulated save failure");
        var attributes = player.Attributes!;
        var character = player.SelectedCharacter!;
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false));
        Assert.That(attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(attributes[Stats.GrandResets], Is.Zero);
        Assert.That(attributes[Stats.Level], Is.EqualTo(400));
        Assert.That(attributes[Stats.BaseStrength], Is.EqualTo(123));
        Assert.That(character.LevelUpPoints, Is.EqualTo(42));
        Assert.That(character.Experience, Is.EqualTo(99));
        Assert.That(character.Inventory, Is.SameAs(inventory));
        Assert.That(inventory.Items, Is.EquivalentTo(originalItems));
        Assert.That(equipped.ItemSlot, Is.Zero);
        Assert.That(equipped.Level, Is.EqualTo(9));
        Assert.That(equipped.Durability, Is.EqualTo(42));
    }

    /// <summary>
    /// Repeated cycles increase exact APP/GR counters without a stat bonus or gameplay cap.
    /// </summary>
    [Test]
    public async Task RepeatedCyclesPreserveProgressAsync()
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        for (var cycle = 1; cycle <= 25; cycle++)
        {
            player.Attributes![Stats.Resets] = 100;
            player.Attributes[Stats.Level] = 400;
            player.Attributes[Stats.BaseStrength] += player.SelectedCharacter!.LevelUpPoints;
            player.SelectedCharacter.LevelUpPoints = 0;
            await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
            Assert.That(player.Attributes[Stats.Resets], Is.Zero);
            Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
            Assert.That(((AtomicTestContext)player.PersistenceContext).GrandResets, Is.EqualTo(new System.Numerics.BigInteger(cycle)));
            Assert.That(((AtomicTestContext)player.PersistenceContext).App, Is.EqualTo(new System.Numerics.BigInteger(cycle * 100)));
            Assert.That(player.SelectedCharacter.LevelUpPoints, Is.Zero);
        }

        player.Attributes![Stats.Level] = 400;
        await new ResetCharacterAction(player).ResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(1));
        Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
        Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.EqualTo(500));
    }

    /// <summary>
    /// Missing persisted setting defaults to one while the normal reset level remains independent.
    /// </summary>
    [Test]
    public void MissingGrandResetLevelDefaultsToOne()
    {
        var configuration = System.Text.Json.JsonSerializer.Deserialize<ResetConfiguration>("{\"LevelAfterReset\":10}")!;
        Assert.That(configuration.LevelAfterGrandReset, Is.EqualTo(1));
        Assert.That(configuration.LevelAfterReset, Is.EqualTo(10));
    }

    /// <summary>
    /// Persisted explicit values remain explicit and require an operator migration.
    /// </summary>
    [TestCase(1, 1)]
    [TestCase(10, 10)]
    public void ExplicitPersistedGrandResetLevelIsNotSilentlyMigrated(int persistedLevel, int expectedLevel)
    {
        var json = "{\"LevelAfterGrandReset\":" + persistedLevel + ",\"LevelAfterReset\":10}";
        var configuration = System.Text.Json.JsonSerializer.Deserialize<ResetConfiguration>(json)!;
        Assert.That(configuration.LevelAfterGrandReset, Is.EqualTo(expectedLevel));
        Assert.That(configuration.LevelAfterReset, Is.EqualTo(10));
    }

    /// <summary>
    /// A stale persisted level must reject the exchange before consuming progress.
    /// </summary>
    [TestCase(0)]
    [TestCase(10)]
    public async Task IncompatibleGrandResetLevelRejectsWithoutMutationAsync(int level)
    {
        var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!.LevelAfterGrandReset = level;
        player.Attributes![Stats.Resets] = 100;
        player.Attributes[Stats.GrandResets] = 3;
        player.SelectedCharacter!.LevelUpPoints = 42;
        var beforeLevel = player.Attributes[Stats.Level];
        var beforeExperience = player.SelectedCharacter.Experience;
        var action = new GrandResetCharacterAction(player);
        Assert.That(action.CanGrandReset(), Is.False);
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(player.Attributes[Stats.GrandResets], Is.EqualTo(3));
        Assert.That(player.Attributes[Stats.Level], Is.EqualTo(beforeLevel));
        Assert.That(player.SelectedCharacter.Experience, Is.EqualTo(beforeExperience));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(42));
    }

    /// <summary>
    /// Float storage must never consume resets when the next counter cannot be represented exactly.
    /// </summary>
    /// <param name="count">An invalid or no longer incrementable stored count.</param>
    [TestCase(16777216f)]
    [TestCase(1.5f)]
    public async Task InvalidCounterDoesNotConsumeResetsAsync(float count)
    {
        using var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 100;
        player.Attributes[Stats.GrandResets] = count;
        await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(player.Attributes[Stats.GrandResets], Is.EqualTo(count));
    }

    /// <summary>
    /// The real action delegates to the atomic persistence capability; failures force a reload.
    /// </summary>
    /// <param name="fail">Whether to inject an exchange failure.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AtomicPersistenceBoundaryIsUsedAsync(bool fail)
    {
        var context = new AtomicTestContext { Fail = fail };
        var provider = new Moq.Mock<MUnique.OpenMU.Persistence.IPersistenceContextProvider>();
        provider.Setup(p => p.CreateNewPlayerContext(Moq.It.IsAny<MUnique.OpenMU.DataModel.Configuration.GameConfiguration>())).Returns(context);
        using var player = await CreatePlayerAsync(true, provider.Object).ConfigureAwait(false);
        player.Attributes![Stats.Resets] = 100;
        var attributes = player.Attributes;
        if (fail)
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false));
            Assert.That(player.IsConnected, Is.False);
            Assert.That(attributes[Stats.Resets], Is.EqualTo(100));
        }
        else
        {
            await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
            Assert.That(player.Attributes[Stats.GrandResets], Is.Zero);
            Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.Zero);
        }

        Assert.That(context.Exchanges, Is.EqualTo(1));
    }

    [TestCase("origin", 25)]
    [TestCase("legacy", 50)]
    [TestCase("evolution", 75)]
    [TestCase("ascension", 100)]
    public async Task RealmThresholdAndHundredAppAsync(string realm, int threshold)
    {
        using var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        var config = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!;
        config.PrestigeRealm = realm;
        config.PrestigeResetsRequired = threshold;
        var action = new GrandResetCharacterAction(player);
        var context = (AtomicTestContext)player.PersistenceContext;
        var messages = Mock.Get(player.ViewPlugIns.GetPlugIn<IShowMessagePlugIn>()!);
        player.Attributes![Stats.Resets] = threshold - 1;
        await new ResetInfoChatCommandPlugIn().HandleCommandAsync(player, "/resetinfo").ConfigureAwait(false);
        messages.Verify(view => view.ShowMessageAsync($"Grand Reset ({realm}): {threshold - 1}/{threshold} resets. Reward: 100 APP.", MessageType.BlueNormal), Times.Once);
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(context.Exchanges, Is.Zero);
        messages.Verify(view => view.ShowMessageAsync(It.Is<string>(message => message.StartsWith("APP balance", StringComparison.Ordinal)), MessageType.BlueNormal), Times.Never);
        player.Attributes[Stats.Resets] = threshold;
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        messages.Verify(view => view.ShowMessageAsync("APP balance after Grand Reset: 100", MessageType.BlueNormal), Times.Once);
        Assert.Multiple(() =>
        {
            Assert.That(context.Exchanges, Is.EqualTo(1));
            Assert.That(context.App, Is.EqualTo(new System.Numerics.BigInteger(100)));
            Assert.That(player.Attributes[Stats.Level], Is.EqualTo(1));
            Assert.That(player.Attributes[Stats.Resets], Is.Zero);
            Assert.That(player.SelectedCharacter!.LevelUpPoints, Is.Zero);
        });
    }

    [Test]
    public async Task DisabledPrestigeCannotFallBackToStatBonusAsync()
    {
        using var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!.PrestigeEnabled = false;
        player.Attributes![Stats.Resets] = 100;
        player.SelectedCharacter!.LevelUpPoints = 42;
        await new GrandResetCharacterAction(player).GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(player.Attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(player.SelectedCharacter.LevelUpPoints, Is.EqualTo(42));
        Assert.That(((AtomicTestContext)player.PersistenceContext).Exchanges, Is.Zero);
    }

    [Test]
    public async Task EmptyTemporaryStorageAllowsExchangeButItemsBlockItAsync()
    {
        using var player = await CreatePlayerAsync(true).ConfigureAwait(false);
        var attributes = player.Attributes ?? throw new InvalidOperationException("Expected entered character attributes.");
        attributes[Stats.Resets] = 100;
        var action = new GrandResetCharacterAction(player);
        var storage = player.TemporaryStorage ?? throw new InvalidOperationException("OpenMU must create temporary storage on entering the world.");
        Assert.That(storage.ItemStorage.Items, Is.Empty);
        Assert.That(action.CanGrandReset(), Is.True, "An empty storage is safe.");
        var item = player.PersistenceContext.CreateNew<Item>();
        item.Definition = new ItemDefinition { Group = 14, Number = 13, Width = 1, Height = 1 };
        storage.ItemStorage.Items.Add(item);
        Assert.That(action.CanGrandReset(), Is.False);
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(((AtomicTestContext)player.PersistenceContext).Exchanges, Is.Zero);
        Assert.That(attributes[Stats.Resets], Is.EqualTo(100));
        Assert.That(storage.ItemStorage.Items.Single(), Is.SameAs(item));
        storage.ItemStorage.Items.Remove(item);
        await action.GrandResetCharacterAsync().ConfigureAwait(false);
        Assert.That(((AtomicTestContext)player.PersistenceContext).Exchanges, Is.EqualTo(1));
        Assert.That(((AtomicTestContext)player.PersistenceContext).App, Is.EqualTo(new System.Numerics.BigInteger(100)));
    }

    private sealed class AtomicTestContext : MUnique.OpenMU.Persistence.InMemory.PlayerInMemoryContext, MUnique.OpenMU.Persistence.IAtomicCharacterProgressContext, MUnique.OpenMU.Persistence.IPrestigeCharacterContext
    {
        public AtomicTestContext()
            : base(new MUnique.OpenMU.Persistence.InMemory.InMemoryRepositoryProvider())
        {
        }

        public bool Fail { get; init; }

        public int Exchanges { get; private set; }

        public System.Numerics.BigInteger GrandResets { get; private set; }

        public System.Numerics.BigInteger App { get; private set; }

        public ValueTask<(string GrandResets, string Balance)?> ReadPrestigeAsync(Character character, string realm, System.Threading.CancellationToken cancellationToken = default)
            => ValueTask.FromResult<(string, string)?>((this.GrandResets.ToString(System.Globalization.CultureInfo.InvariantCulture),
                this.App.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public async ValueTask<string> ExecutePrestigeExchangeAsync(Character character, string realm, string operationId, int resetsBefore, System.Threading.CancellationToken cancellationToken = default)
        {
            await this.SaveCharacterExchangeAsync(character, cancellationToken).ConfigureAwait(false);
            await this.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            this.GrandResets++;
            this.App += 100;
            return System.Text.Json.JsonSerializer.Serialize(new { balance_app = this.App.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public ValueTask<string> DeliverPrestigeRewardAsync(Character character, string realm, Guid purchaseId, Func<int, int, System.Threading.CancellationToken, ValueTask<Item>> createItem, System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException("No item delivery in this action test double.");

        public ValueTask SaveCharacterExchangeAsync(MUnique.OpenMU.DataModel.Entities.Character character, System.Threading.CancellationToken cancellationToken = default)
        {
            this.Exchanges++;
            if (this.Fail)
            {
                throw new InvalidOperationException("Injected atomic boundary failure.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async ValueTask<Player> CreatePlayerAsync(bool enabled, MUnique.OpenMU.Persistence.IPersistenceContextProvider? provider = null)
    {
        if (provider is null)
        {
            var context = new AtomicTestContext();
            var mock = new Moq.Mock<MUnique.OpenMU.Persistence.IPersistenceContextProvider>();
            mock.Setup(p => p.CreateNewPlayerContext(Moq.It.IsAny<MUnique.OpenMU.DataModel.Configuration.GameConfiguration>())).Returns(context);
            provider = mock.Object;
        }
        var player = await PlayerTestHelper.CreatePlayerAsync(provider).ConfigureAwait(false);
        player.GameContext.FeaturePlugIns.AddPlugIn(new ResetFeaturePlugIn
        {
            Configuration = new ResetConfiguration
            {
                GrandResetEnabled = enabled,
                PrestigeEnabled = enabled,
                PrestigeRealm = "ascension",
                PrestigeResetsRequired = 100,
                LevelAfterReset = 10,
                PointsPerReset = 500,
                RequiredMoney = 0,
                LogOut = false,
                MoveHome = false,
            },
        }, true);
        return player;
    }
}

