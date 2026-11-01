// <copyright file="GrandResetCharacterActionTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Resets;

[TestFixture]
public class GrandResetCharacterActionTest
{
    [Test]
    public async Task RequiresOneHundredNormalResetsAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var action = new GrandResetCharacterAction(player);

        player.Attributes![Stats.Resets] = 99;
        Assert.That(action.CanGrandReset(), Is.False);

        player.Attributes[Stats.Resets] = 100;
        Assert.That(action.CanGrandReset(), Is.True);
    }
}
