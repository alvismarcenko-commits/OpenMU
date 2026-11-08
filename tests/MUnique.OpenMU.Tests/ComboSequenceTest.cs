// <copyright file="ComboSequenceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.BasicModel;

/// <summary>Tests the state machine called by the native targeted and area skill actions.</summary>
[TestFixture]
public class ComboSequenceTest
{
    /// <summary>Checks that Reset and repeated final casts cannot complete the same combo twice.</summary>
    [Test]
    public async Task NativeSequenceCompletesOnlyOnceAsync()
    {
        var slash = new Skill();
        var twist = new Skill();
        var stab = new Skill();
        var definition = new SkillComboDefinition { MaximumCompletionTime = TimeSpan.FromMinutes(1) };
        definition.Steps.Add(new SkillComboStep { Skill = slash, Order = 1 });
        definition.Steps.Add(new SkillComboStep { Skill = twist, Order = 2 });
        definition.Steps.Add(new SkillComboStep { Skill = stab, Order = 3, IsFinalStep = true });
        var combo = ComboStateMachine.Create(definition);
        Assert.That(await combo.RegisterSkillAsync(twist).ConfigureAwait(false), Is.False);
        Assert.That(await combo.RegisterSkillAsync(slash).ConfigureAwait(false), Is.False);
        Assert.That(await combo.RegisterSkillAsync(twist).ConfigureAwait(false), Is.False);
        await combo.ResetAsync().ConfigureAwait(false);
        Assert.That(await combo.RegisterSkillAsync(stab).ConfigureAwait(false), Is.False);
        Assert.That(await combo.RegisterSkillAsync(slash).ConfigureAwait(false), Is.False);
        Assert.That(await combo.RegisterSkillAsync(twist).ConfigureAwait(false), Is.False);
        var completed = await Task.WhenAll(combo.RegisterSkillAsync(stab).AsTask(), combo.RegisterSkillAsync(stab).AsTask()).ConfigureAwait(false);
        Assert.That(completed.Count(value => value), Is.EqualTo(1));
    }
}
