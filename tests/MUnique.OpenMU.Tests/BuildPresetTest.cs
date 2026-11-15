// <copyright file="BuildPresetTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>Validates that preset application can spend native points only by adding legal stats.</summary>
[TestFixture]
[NonParallelizable]
public class BuildPresetTest
{
    /// <summary>Checks valid application, repeat application, and out-of-range or decreasing targets.</summary>
    [Test]
    public void PresetCostIsAdditiveAndBounded()
    {
        int[] current = [28, 20, 25, 10, 0];
        int[] maximum = [32767, 32767, 32767, 32767, 0];
        Assert.That(BuildPresetChatCommandPlugIn.CalculateCost([38, 20, 27, 10, 0], current, maximum), Is.EqualTo(12));
        Assert.That(BuildPresetChatCommandPlugIn.CalculateCost(current, current, maximum), Is.Zero);
        Assert.That(BuildPresetChatCommandPlugIn.CalculateCost([27, 21, 25, 10, 0], current, maximum), Is.Null);
        Assert.That(BuildPresetChatCommandPlugIn.CalculateCost([32768, 20, 25, 10, 0], current, maximum), Is.Null);
        Assert.That(BuildPresetChatCommandPlugIn.CalculateCost([28, 20, 25, 10, 1], current, maximum), Is.Null);
    }

    /// <summary>Applies the same build through the chat path without spending the same points twice.</summary>
    [Test]
    public async Task ApplyBuildFromChatAsync()
    {
        var previous = Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING");
        Environment.SetEnvironmentVariable("AETHER_LOCAL_STAGING", "true");
        try
        {
            var persistence = new PresetContext();
            var provider = new Mock<IPersistenceContextProvider>();
            provider.Setup(p => p.CreateNewPlayerContext(It.IsAny<GameConfiguration>())).Returns(persistence);
            using var player = await PlayerTestHelper.CreatePlayerAsync(provider.Object).ConfigureAwait(false);
            player.GameContext.FeaturePlugIns.AddPlugIn(new ResetFeaturePlugIn
            {
                Configuration = new ResetConfiguration { PrestigeRealm = "ascension" },
            }, true);
            var safeZone = player.CurrentMap!.Terrain.SafezoneMap;
            var point = player.Position;
            safeZone[point.X, point.Y] = true;
            var character = player.SelectedCharacter!;
            var attributes = player.Attributes!;
            var original = attributes[Stats.BaseStrength];
            attributes[Stats.BaseStrength] += 5;
            var command = new BuildPresetChatCommandPlugIn();
            await command.HandleCommandAsync(player, "/build save 1").ConfigureAwait(false);
            attributes[Stats.BaseStrength] = original;
            character.LevelUpPoints = 4;
            await command.HandleCommandAsync(player, "/build apply 1").ConfigureAwait(false);
            Assert.That(persistence.Exchanges, Is.Zero);
            Assert.That(attributes[Stats.BaseStrength], Is.EqualTo(original));
            character.LevelUpPoints = 5;
            await command.HandleCommandAsync(player, "/build apply 1").ConfigureAwait(false);
            await command.HandleCommandAsync(player, "/build apply 1").ConfigureAwait(false);
            Assert.That(attributes[Stats.BaseStrength], Is.EqualTo(original + 5));
            Assert.That(character.LevelUpPoints, Is.Zero);
            Assert.That(persistence.Exchanges, Is.EqualTo(1));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AETHER_LOCAL_STAGING", previous);
        }
    }

    private sealed class PresetContext() : PlayerInMemoryContext(new InMemoryRepositoryProvider()), IBuildPresetContext
    {
        private (int ClassNumber, int[] Targets)? _preset;

        public int Exchanges { get; private set; }

        public ValueTask<(int ClassNumber, int[] Targets)?> ReadBuildPresetAsync(Character character, int slot)
            => ValueTask.FromResult(this._preset);

        public ValueTask SaveBuildPresetAsync(Character character, int slot, int classNumber, int[] targets)
        {
            this._preset = (classNumber, targets);
            return ValueTask.CompletedTask;
        }

        public ValueTask SaveCharacterExchangeAsync(Character character, CancellationToken cancellationToken = default)
        {
            this.Exchanges++;
            return ValueTask.CompletedTask;
        }
    }
}
