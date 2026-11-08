// <copyright file="AetherHelpChatCommandPlugIn.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>Public informational command for the Ascension staging game.</summary>
[Guid("c4de76a9-1f48-4d25-833a-fd867054c8df")]
[PlugIn]
[ChatCommandHelp(Command)]
public sealed class AetherHelpChatCommandPlugIn : IChatCommandPlugIn, IPlayerStateChangedPlugIn
{
    private const string Command = "/aether";
    private readonly ConditionalWeakTable<Player, StrongBox<long>> _cooldown = new();
    private readonly ConditionalWeakTable<Player, object> _welcomed = new();

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true" || player.SelectedCharacter is null)
        {
            return;
        }

        var last = this._cooldown.GetValue(player, _ => new StrongBox<long>(Environment.TickCount64 - 2000));
        lock (last)
        {
            var now = Environment.TickCount64;
            if (now - last.Value < 2000)
            {
                return;
            }

            last.Value = now;
        }

        var action = command.Trim().ToLowerInvariant() switch
        {
            Command or Command + " help" => "help",
            Command + " rules" => "rules",
            Command + " events" => "events",
            _ => "invalid",
        };
        if (player.GameContext is IGameServerContext server)
        {
            await server.EventPublisher.GameEventAsync(new AetherObservationEvent(server.Id, DateTime.UtcNow,
                "gm_bot", action)).ConfigureAwait(false);
        }

        if (action != "events")
        {
            await ShowConfiguredMessageAsync(player, action == "rules" ? "rules" : "help",
                action == "rules" ? "AetherBotRules" : "AetherBotHelp").ConfigureAwait(false);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var plugins = player.GameContext.PlugInManager.GetActivePlugInsOf<IPeriodicTaskPlugIn>();
        var zone = player.GameContext.ServerTimeZone;
        var golden = plugins.OfType<GoldenInvasionPlugIn>().FirstOrDefault()?.GetNextStartUtc(nowUtc, zone);
        var treasure = plugins.OfType<AetherTreasureHuntPlugIn>().FirstOrDefault()?.GetNextStartUtc(nowUtc, zone);
        await player.ShowLocalizedBlueMessageAsync("AetherBotEvents",
            golden?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            treasure?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask PlayerStateChangedAsync(Player player, State previousState, State currentState)
    {
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true"
            && previousState == PlayerState.CharacterSelection && currentState == PlayerState.EnteredWorld
            && player is IHasIpAddress && this._welcomed.TryAdd(player, new object()))
        {
            return ShowConfiguredMessageAsync(player, "welcome", "AetherBotWelcome");
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ShowConfiguredMessageAsync(Player player, string key, string resource)
        => AetherLiveSettings.Read().GmMessages?.GetValueOrDefault(key) is { Length: > 0 and <= 80 } message
            ? player.ShowBlueMessageAsync(message)
            : player.ShowLocalizedBlueMessageAsync(resource);
}
