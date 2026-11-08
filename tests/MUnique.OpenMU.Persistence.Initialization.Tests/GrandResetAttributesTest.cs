// <copyright file="GrandResetAttributesTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.Initialization.Updates;

/// <summary>
/// Verifies new databases, existing databases, and idempotent counter configuration updates.
/// </summary>
[TestFixture]
public class GrandResetAttributesTest
{
    /// <summary>
    /// Verifies that initialized classes already have exactly one Grand Reset definition.
    /// </summary>
    [Test]
    public async Task FreshDatabaseAndRepeatedUpdateAsync()
    {
        var (context, configuration) = await TestGameConfiguration.CreateSeason6Async().ConfigureAwait(false);
        using var scope = context;
        Assert.That(configuration.CharacterClasses, Is.Not.Empty);
        foreach (var characterClass in configuration.CharacterClasses)
        {
            Assert.That(characterClass.StatAttributes.Count(a => a.Attribute == Stats.GrandResets), Is.EqualTo(1));
        }

        foreach (var characterClass in configuration.CharacterClasses)
        {
            foreach (var attribute in characterClass.StatAttributes.Where(a => a.Attribute == Stats.GrandResets).ToList())
            {
                characterClass.StatAttributes.Remove(attribute);
            }
        }

        var update = new GrandResetAttributesPlugIn();
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        await update.ApplyUpdateAsync(context, configuration).ConfigureAwait(false);
        foreach (var characterClass in configuration.CharacterClasses)
        {
            var attribute = characterClass.StatAttributes.Single(a => a.Attribute == Stats.GrandResets);
            Assert.That(attribute.BaseValue, Is.Zero);
            Assert.That(attribute.IncreasableByPlayer, Is.False);
        }
    }
}
