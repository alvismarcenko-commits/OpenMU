
 // <copyright file="GrandResetCalculatorTest.cs" company="MUnique">
 // Licensed under the MIT License. See LICENSE file in the project root for full license information.
 // </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic.Resets;

/// <summary>
/// Tests for the BalticAetherMU Grand Reset calculator.
/// </summary>
[TestFixture]
public class GrandResetCalculatorTest
{
    /// <summary>
    /// Verifies that fewer than 100 resets are insufficient.
    /// </summary>
    [Test]
    public void NinetyNineResetsAreNotEnough()
    {
        Assert.That(GrandResetCalculator.CanGrandReset(99), Is.False);
    }

    /// <summary>
    /// Verifies that exactly 100 resets are sufficient.
    /// </summary>
    [Test]
    public void OneHundredResetsAreEnough()
    {
        Assert.That(GrandResetCalculator.CanGrandReset(100), Is.True);
    }

    /// <summary>
    /// Verifies that more than 100 resets are sufficient.
    /// </summary>
    [Test]
    public void MoreThanOneHundredResetsAreEnough()
    {
        Assert.That(GrandResetCalculator.CanGrandReset(101), Is.True);
    }

    /// <summary>
    /// Verifies the bonus for the first Grand Reset.
    /// </summary>
    [Test]
    public void FirstGrandResetGrantsOneHundredPoints()
    {
        Assert.That(GrandResetCalculator.CalculateBonusPoints(1), Is.EqualTo(100));
    }

    /// <summary>
    /// Verifies the bonus after ten Grand Resets.
    /// </summary>
    [Test]
    public void TenGrandResetsGrantOneThousandPoints()
    {
        Assert.That(GrandResetCalculator.CalculateBonusPoints(10), Is.EqualTo(1000));
    }

    /// <summary>
    /// Verifies that the bonus is capped at 2000 points.
    /// </summary>
    [Test]
    public void GrandResetBonusDoesNotExceedMaximum()
    {
        Assert.That(GrandResetCalculator.CalculateBonusPoints(20), Is.EqualTo(2000));
        Assert.That(GrandResetCalculator.CalculateBonusPoints(25), Is.EqualTo(2000));
        Assert.That(GrandResetCalculator.CalculateBonusPoints(100), Is.EqualTo(2000));
    }

    /// <summary>
    /// Verifies that zero or negative counts do not grant points.
    /// </summary>
    [Test]
    public void InvalidGrandResetCountsGrantNoPoints()
    {
        Assert.That(GrandResetCalculator.CalculateBonusPoints(0), Is.Zero);
        Assert.That(GrandResetCalculator.CalculateBonusPoints(-1), Is.Zero);
    }
}

