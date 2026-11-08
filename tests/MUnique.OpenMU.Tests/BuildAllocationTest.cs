// <copyright file="BuildAllocationTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlayerActions.Character;

/// <summary>Tests that stat allocation shares the Reset/GR persistence boundary.</summary>
[TestFixture]
public class BuildAllocationTest
{
    /// <summary>Two allocations cannot spend the same points.</summary>
    [Test]
    public async Task AllocationsShareTheNativePointBudgetAsync()
    {
        using var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var character = player.SelectedCharacter!;
        character.LevelUpPoints = 5;
        var stats = player.Attributes!;
        var strength = stats[Stats.BaseStrength];
        var agility = stats[Stats.BaseAgility];

        var action = new IncreaseStatsAction();
        await Task.WhenAll(action.IncreaseStatsAsync(player, Stats.BaseStrength, 5).AsTask(),
            action.IncreaseStatsAsync(player, Stats.BaseAgility, 5).AsTask()).ConfigureAwait(false);

        Assert.That(character.LevelUpPoints, Is.Zero);
        Assert.That(stats[Stats.BaseStrength] - strength + stats[Stats.BaseAgility] - agility, Is.EqualTo(5));
    }
}
