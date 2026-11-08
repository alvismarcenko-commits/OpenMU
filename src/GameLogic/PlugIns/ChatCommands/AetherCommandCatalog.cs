// <copyright file="AetherCommandCatalog.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.CompilerServices;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Persistence;

/// <summary>Extends MuMain's native command window with staging-specific player actions.</summary>
public static class AetherCommandCatalog
{
    private static readonly ConditionalWeakTable<Player, StrongBox<long>> LastRequest = new();

    /// <summary>Sends only active commands available to the authenticated character.</summary>
    /// <param name="player">The current player.</param>
    /// <returns>A task representing the catalogue delivery.</returns>
    public static async ValueTask SendAsync(Player player)
    {
        var staging = Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true";
        if (staging && !AllowRequest(player))
        {
            return;
        }

        var available = player.GetAvailableChatCommandInfos().ToList();
        if (staging)
        {
            available = await AddStagingActionsAsync(player, available).ConfigureAwait(false);
        }

        await player.InvokeViewPlugInAsync<IChatCommandListViewPlugIn>(
            view => view.ShowChatCommandListAsync(available)).ConfigureAwait(false);
    }

    private static async ValueTask<List<ChatCommandInfo>> AddStagingActionsAsync(Player player, List<ChatCommandInfo> available)
    {
        var config = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        var actions = new List<ChatCommandInfo>();
        if (config is { PrestigeEnabled: true, GrandResetEnabled: true }
            && player.PersistenceContext is IPrestigeCharacterContext
            && available.FirstOrDefault(command => command.Command == "/resetinfo") is { } resetInfo)
        {
            var status = await ResetInfoChatCommandPlugIn.ReadAppAsync(player, config).ConfigureAwait(false);
            var description = status is { } value && player.Attributes is { } attributes
                ? player.GetLocalizedMessage(
                    "AetherUiPrestigeSnapshot",
                    config.PrestigeRealm,
                    attributes[Stats.Level],
                    attributes[Stats.Resets],
                    config.PrestigeResetsRequired,
                    CompactCounter(value.GrandResets),
                    CompactCounter(value.Balance))
                : player.GetLocalizedMessage("AetherUiPrestigeUnavailable");
            actions.Add(resetInfo with
            {
                Command = "/resetinfo app",
                Usage = "/resetinfo app",
                Name = player.GetLocalizedMessage("AetherUiPrestige"),
                Description = description,
                Parameters = [new(player.GetLocalizedMessage("AetherUiAction"), null, nameof(String), true, ["refresh"])],
            });
        }

        if (available.FirstOrDefault(command => command.Command == "/grandreset") is { } grandReset)
        {
            available.Remove(grandReset);
            if (config is { PrestigeEnabled: true, GrandResetEnabled: true } && player.PersistenceContext is IPrestigeCharacterContext)
            {
                actions.Add(grandReset with
                {
                    Name = player.GetLocalizedMessage("AetherUiGrandReset"),
                    Description = player.GetLocalizedMessage("AetherUiGrandResetDetails"),
                    Parameters = [new(player.GetLocalizedMessage("AetherUiAction"), null, nameof(String), true, ["confirm"])],
                });
            }
        }

        if (available.FirstOrDefault(command => command.Command == "/build") is { } build)
        {
            available.Remove(build);
            if (config?.PrestigeRealm == "ascension" && player.PersistenceContext is IBuildPresetContext)
            {
                actions.Add(BuildAction(player, build, "save", "AetherUiBuildSave"));
                actions.Add(BuildAction(player, build, "apply", "AetherUiBuildApply"));
            }
        }

        if (available.FirstOrDefault(command => command.Command == "/aether") is { } aether)
        {
            actions.Add(aether with { Command = "/aether hunt", Usage = "/aether hunt", Name = player.GetLocalizedMessage("AetherUiHunt") });
            actions.Add(aether with { Command = "/aether events", Usage = "/aether events", Name = player.GetLocalizedMessage("AetherUiEvents") });
        }

        if (available.FirstOrDefault(command => command.Command == "/language") is { Parameters: [var languageParameter] } language)
        {
            available.Remove(language);
            actions.Add(language with
            {
                Parameters = [languageParameter with { ValidValues = ["en", "lv", "lt", "et", "ru", "it", "pl", "es", "de"] }],
            });
        }

        actions.AddRange(available);
        return actions;
    }

    private static ChatCommandInfo BuildAction(Player player, ChatCommandInfo build, string action, string label)
        => build with
        {
            Command = $"/build {action}",
            Usage = $"/build {action} 1–3",
            Name = player.GetLocalizedMessage(label),
            Description = player.GetLocalizedMessage("AetherUiBuildDetails"),
            Parameters = [new(player.GetLocalizedMessage("AetherUiSlot"), null, nameof(Byte), true, ["1", "2", "3"], 1, 3)],
        };

    private static bool AllowRequest(Player player)
    {
        var request = LastRequest.GetValue(player, _ => new StrongBox<long>(Environment.TickCount64 - 2000));
        lock (request)
        {
            var now = Environment.TickCount64;
            if (now - request.Value < 2000)
            {
                return false;
            }

            request.Value = now;
            return true;
        }
    }

    private static string CompactCounter(string counter) => counter.Length <= 18 ? counter : counter[..17] + "…";
}
