// <copyright file="ResetInfoChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Data.Common;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command plugin which shows reset costs and rewards.
/// </summary>
[Guid("79F2C2C2-2E4C-4F4B-8A74-4227D1209D27")]
[PlugIn]
[Display(Name = nameof(PlugInResources.ResetInfoChatCommandPlugIn_Name), Description = nameof(PlugInResources.ResetInfoChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[ChatCommandHelp(Command)]
public class ResetInfoChatCommandPlugIn : IChatCommandPlugIn
{
    private const string Command = "/resetinfo";

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var configuration = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        if (configuration is null)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.ResetSystemInactive)).ConfigureAwait(false);
            return;
        }

        if (player.Attributes is null || player.SelectedCharacter is null)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.NotEnteredTheGame)).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Trim(), Command + " app refresh", StringComparison.OrdinalIgnoreCase)
            && Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true")
        {
            await AetherCommandCatalog.SendAsync(player).ConfigureAwait(false);
            return;
        }

        if (string.Equals(command.Trim(), Command + " app", StringComparison.OrdinalIgnoreCase))
        {
            await ShowAppAsync(player, configuration).ConfigureAwait(false);
            return;
        }

        if (configuration is { PrestigeEnabled: true, GrandResetEnabled: true })
        {
            await player.ShowLocalizedBlueMessageAsync("PrestigeGrandResetProgress", configuration.PrestigeRealm,
                player.Attributes[Stats.Resets], configuration.PrestigeResetsRequired).ConfigureAwait(false);
        }
        else
        {
            await player.ShowLocalizedBlueMessageAsync("PrestigeGrandResetDisabled").ConfigureAwait(false);
        }

        var progression = ResetProgressionCalculator.Calculate(
            (int)player.Attributes[Stats.Resets],
            (int)player.Attributes[Stats.PointsPerReset],
            configuration);
        if (progression.RequiredItemAmount > 0 && configuration.RequiredResetItem is { Name: { } itemName })
        {
            await player.ShowLocalizedBlueMessageAsync(
                    nameof(PlayerMessage.NextResetInfoCompactWithItem),
                    progression.NextResetCount,
                    configuration.RequiredLevel,
                    progression.RequiredZen,
                    itemName,
                    progression.RequiredItemAmount,
                    progression.PointsForReset,
                    progression.TotalPointsAfterReset)
                .ConfigureAwait(false);
            return;
        }

        await player.ShowLocalizedBlueMessageAsync(
                nameof(PlayerMessage.NextResetInfoCompactNoItem),
                progression.NextResetCount,
                configuration.RequiredLevel,
                progression.RequiredZen,
                progression.PointsForReset,
                progression.TotalPointsAfterReset)
            .ConfigureAwait(false);
    }

    internal static async ValueTask<(string GrandResets, string Balance)?> ReadAppAsync(Player player, ResetConfiguration configuration)
    {
        if (configuration is { PrestigeEnabled: true, GrandResetEnabled: true }
            && player.SelectedCharacter is { } character
            && player.PersistenceContext is IPrestigeCharacterContext persistence)
        {
            try
            {
                return await player.RunPersistenceExclusiveAsync(
                    () => persistence.ReadPrestigeAsync(character, configuration.PrestigeRealm)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            {
                player.Logger.LogWarning(ex, "Unable to load the selected character's Prestige status.");
            }
        }

        return null;
    }

    private static async ValueTask ShowAppAsync(Player player, ResetConfiguration configuration)
    {
        var status = await ReadAppAsync(player, configuration).ConfigureAwait(false);
        if (status is { } value)
        {
            await player.ShowLocalizedBlueMessageAsync("PrestigeCharacterBalance", configuration.PrestigeRealm,
                value.GrandResets, value.Balance).ConfigureAwait(false);
            return;
        }

        await player.ShowLocalizedBlueMessageAsync("PrestigeBalanceUnavailable").ConfigureAwait(false);
    }
}
