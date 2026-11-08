// <copyright file="BuildPresetChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Data.Common;
using System.Runtime.InteropServices;
using MUnique.OpenMU.AttributeSystem;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>Save and apply an Ascension base stat build using the native unspent point budget.</summary>
[Guid("cd458fb9-e97f-4cb9-a4be-e615c83e11a5")]
[PlugIn]
[ChatCommandHelp(Command)]
public sealed class BuildPresetChatCommandPlugIn : IChatCommandPlugIn
{
    private const string Command = "/build";
    private static readonly AttributeDefinition[] Attributes = [Stats.BaseStrength, Stats.BaseAgility, Stats.BaseVitality, Stats.BaseEnergy, Stats.BaseLeadership];

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true"
            || player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration?.PrestigeRealm != "ascension"
            || player.PersistenceContext is not IBuildPresetContext persistence)
        {
            return;
        }

        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (args is not [Command, "save" or "apply", "1" or "2" or "3"])
        {
            await player.ShowLocalizedBlueMessageAsync("BuildPresetHelp").ConfigureAwait(false);
            return;
        }

        var slot = args[2][0] - '0';
        try
        {
            await player.RunPersistenceExclusiveAsync(() => ExecuteAsync(player, persistence, args[1] == "save", slot)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            player.Logger.LogWarning(ex, "Unable to access the selected character's build preset.");
            await player.ShowLocalizedBlueMessageAsync("BuildPresetUnavailable").ConfigureAwait(false);
        }
    }

    private static async ValueTask ExecuteAsync(Player player, IBuildPresetContext persistence, bool save, int slot)
    {
        if (player.SelectedCharacter is not { CharacterClass: { } characterClass } character
            || player.Attributes is not { } attributes
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld
            || !player.IsAlive || !player.IsAtSafezone() || player.DuelRoom is not null
            || player.OpenedNpc is not null || player.TradingPartner is not null
            || player.TemporaryStorage is { ItemStorage.Items.Count: > 0 })
        {
            await player.ShowLocalizedBlueMessageAsync("BuildPresetUnavailable").ConfigureAwait(false);
            return;
        }

        var current = new int[Attributes.Length];
        var maximum = new int[Attributes.Length];
        for (var index = 0; index < Attributes.Length; index++)
        {
            var attribute = Attributes[index];
            if (characterClass.GetStatAttribute(attribute) is not { IncreasableByPlayer: true })
            {
                continue;
            }

            var value = attributes[attribute];
            // Keep preset restores inside the signed range validated by legacy stat controls.
            maximum[index] = (int)Math.Min(short.MaxValue, attribute.MaximumValue ?? short.MaxValue);
            if (!float.IsFinite(value) || value < 0 || value != MathF.Truncate(value) || value > maximum[index])
            {
                await player.ShowLocalizedBlueMessageAsync("BuildPresetUnavailable").ConfigureAwait(false);
                return;
            }

            current[index] = (int)value;
        }

        if (save)
        {
            await persistence.SaveBuildPresetAsync(character, slot, characterClass.Number, current).ConfigureAwait(false);
            await player.ShowLocalizedBlueMessageAsync("BuildPresetSaved", slot).ConfigureAwait(false);
            return;
        }

        var preset = await persistence.ReadBuildPresetAsync(character, slot).ConfigureAwait(false);
        if (preset is not { } build || build.ClassNumber != characterClass.Number
            || CalculateCost(build.Targets, current, maximum) is not { } cost || cost > character.LevelUpPoints)
        {
            await player.ShowLocalizedBlueMessageAsync("BuildPresetDoesNotFit").ConfigureAwait(false);
            return;
        }

        if (cost > 0)
        {
            var previousPoints = character.LevelUpPoints;
            for (var index = 0; index < Attributes.Length; index++)
            {
                if (build.Targets[index] > current[index])
                {
                    attributes[Attributes[index]] = build.Targets[index];
                }
            }

            character.LevelUpPoints -= cost;
            try
            {
                await persistence.SaveCharacterExchangeAsync(character).ConfigureAwait(false);
            }
            catch
            {
                // The atomic context is poisoned on failure, including an uncertain commit.
                for (var index = 0; index < Attributes.Length; index++)
                {
                    if (build.Targets[index] > current[index])
                    {
                        attributes[Attributes[index]] = current[index];
                    }
                }

                character.LevelUpPoints = previousPoints;
                await player.DisconnectAsync().ConfigureAwait(false);
                throw;
            }

            await player.ResetComboProgressAsync().ConfigureAwait(false);
            await player.InvokeViewPlugInAsync<IUpdateCharacterBaseStatsPlugIn>(p => p.UpdateCharacterBaseStatsAsync()).ConfigureAwait(false);
        }

        await player.ShowLocalizedBlueMessageAsync("BuildPresetApplied", slot).ConfigureAwait(false);
    }

    internal static int? CalculateCost(int[] targets, int[] current, int[] maximum)
    {
        if (targets.Length != current.Length)
        {
            return null;
        }

        var cost = 0;
        for (var index = 0; index < current.Length; index++)
        {
            if (targets[index] < current[index] || targets[index] > maximum[index])
            {
                return null;
            }

            cost += targets[index] - current[index];
        }

        return cost;
    }
}
