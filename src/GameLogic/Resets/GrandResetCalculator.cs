
// <copyright file="GrandResetCalculator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

/// <summary>
/// Calculates AetherMU Europe Grand Reset eligibility and bonus points.
/// </summary>
public static class GrandResetCalculator
{
    /// <summary>
    /// Gets the number of normal resets required for one Grand Reset.
    /// </summary>
    public const int RequiredResets = 100;

    /// <summary>
    /// Gets the bonus points granted per Grand Reset.
    /// </summary>
    public const int PointsPerGrandReset = 100;

    /// <summary>
    /// Gets the maximum permanent Grand Reset bonus.
    /// </summary>
    public const int MaximumBonusPoints = 2000;

    /// <summary>
    /// Determines whether the character can perform a Grand Reset.
    /// </summary>
    public static bool CanGrandReset(int resetCount)
    {
        return resetCount >= RequiredResets;
    }

    /// <summary>
    /// Calculates the permanent bonus for completed Grand Resets.
    /// </summary>
    public static int CalculateBonusPoints(int grandResetCount)
    {
        var cappedGrandResets = Math.Clamp(
            grandResetCount,
            0,
            MaximumBonusPoints / PointsPerGrandReset);

        return cappedGrandResets * PointsPerGrandReset;
    }
}

