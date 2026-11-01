// <copyright file="GrandResetCharacterAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.Character;

/// <summary>
/// Handles BalticAetherMU Grand Reset operations.
/// </summary>
public class GrandResetCharacterAction
{
    private readonly Player _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrandResetCharacterAction"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public GrandResetCharacterAction(Player player)
    {
        this._player = player;
    }

    /// <summary>
    /// Checks whether the character meets the Grand Reset requirements.
    /// </summary>
    public bool CanGrandReset()
    {
        if (this._player.Attributes is null ||
            this._player.SelectedCharacter is null)
        {
            return false;
        }

        return GrandResetCalculator.CanGrandReset(
            (int)this._player.Attributes[Stats.Resets]);
    }

    /// <summary>
    /// Performs a Grand Reset for an eligible character.
    /// </summary>
    public async ValueTask GrandResetCharacterAsync()
    {
        if (this._player.PlayerState.CurrentState != PlayerState.EnteredWorld ||
            this._player.Attributes is null ||
            this._player.SelectedCharacter?.CharacterClass is null)
        {
            return;
        }

        if (!this.CanGrandReset())
        {
            await this._player.ShowBlueMessageAsync(
                "Grand Reset requires 100 normal resets.").ConfigureAwait(false);
            return;
        }

        var attributes = this._player.Attributes;
        var character = this._player.SelectedCharacter;

        var currentGrandResets = (int)attributes[Stats.GrandResets];
        var nextGrandResets = checked(currentGrandResets + 1);
        var permanentBonus = GrandResetCalculator.CalculateBonusPoints(nextGrandResets);

        foreach (var stat in character.CharacterClass.StatAttributes
                     .Where(stat => stat.IncreasableByPlayer))
        {
            attributes[stat.Attribute] = stat.BaseValue;
        }

        attributes[Stats.GrandResets] = nextGrandResets;
        attributes[Stats.Resets] = 0;
        attributes[Stats.Level] = 10;

        character.Experience = 0;
        character.LevelUpPoints = permanentBonus;

        await this._player.InvokeViewPlugInAsync<IUpdateCharacterBaseStatsPlugIn>(
            plugIn => plugIn.UpdateCharacterBaseStatsAsync()).ConfigureAwait(false);

        await this._player.ShowBlueMessageAsync(
            $"Grand Reset completed! Total: {nextGrandResets}. Permanent bonus: {permanentBonus} points.")
            .ConfigureAwait(false);
    }
}
