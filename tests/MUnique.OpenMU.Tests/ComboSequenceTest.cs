// <copyright file="ComboSequenceTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Persistence.BasicModel;
using Moq;

/// <summary>Tests the state machine called by the native targeted and area skill actions.</summary>
[TestFixture]
public class ComboSequenceTest
{
    /// <summary>The configured Combo bonus is applied by the native PvE and PvP damage paths.</summary>
    /// <param name="pvp">Whether the defender is a player.</param>
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeComboBonusAddsDamageAsync(bool pvp)
    {
        using var attacker = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        using var opponent = await PlayerTestHelper.CreatePlayerAsync(attacker.GameContext).ConfigureAwait(false);
        var attributes = attacker.Attributes!;
        attributes.AddElement(new SimpleElement(100, AggregateType.AddRaw), Stats.AttackRatePvm);
        attributes.AddElement(new SimpleElement(100, AggregateType.AddRaw), Stats.AttackRatePvp);
        attributes.AddElement(new SimpleElement(100, AggregateType.AddRaw), Stats.MinimumPhysBaseDmg);
        attributes.AddElement(new SimpleElement(100, AggregateType.AddRaw), Stats.MaximumPhysBaseDmg);
        attributes.AddElement(new SimpleElement(60, AggregateType.AddRaw), Stats.ComboBonus);
        var monsterStats = new Mock<IAttributeSystem>();
        monsterStats.SetupGet(stats => stats[Stats.DamageReceiveDecrement]).Returns(1);
        var monster = new Mock<IAttackable>();
        monster.SetupGet(target => target.Attributes).Returns(monsterStats.Object);
        IAttackable defender = pvp ? opponent : monster.Object;

        var normal = await attacker.CalculateDamageAsync(defender, null, false).ConfigureAwait(false);
        var combo = await attacker.CalculateDamageAsync(defender, null, true).ConfigureAwait(false);

        Assert.That(normal.TotalDamage, Is.GreaterThan(0));
        Assert.That(combo.TotalDamage, Is.EqualTo(normal.TotalDamage + 60));
        Assert.That(combo.Attributes.HasFlag(DamageAttributes.Combo), Is.True);
    }

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
