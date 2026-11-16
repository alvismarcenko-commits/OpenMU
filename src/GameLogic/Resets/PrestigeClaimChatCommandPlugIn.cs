// Licensed under MIT. AetherMU staging delivery candidate, disabled by default.
namespace MUnique.OpenMU.GameLogic.Resets;
using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.PlugIns;

/// <summary>Claims one owned Website reservation using the player's native transaction.</summary>
[Guid("1B4DC39A-0460-4C83-B323-E938FF0B9746")]
[PlugIn]
[ChatCommandHelp("/prestigeclaim")]
public class PrestigeClaimChatCommandPlugIn : IChatCommandPlugIn, IDisabledByDefault
{
    /// <inheritdoc />
    public string Key => "/prestigeclaim";
    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;
    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var config = player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out var purchase) || config?.PrestigeDeliveryEnabled != true || !config.PrestigeEnabled
            || player.PlayerState.CurrentState != PlayerState.EnteredWorld || player.SelectedCharacter is null
            || player.OpenedNpc is not null || player.TradingPartner is not null || player.TemporaryStorage is not null
            || player.PersistenceContext is not IPrestigeCharacterContext persistence)
        {
            await player.ShowBlueMessageAsync("Prestige delivery is unavailable or purchase UUID is invalid.").ConfigureAwait(false);
            return;
        }
        await player.RunPersistenceExclusiveAsync(async () =>
        {
            try
            {
                var status = await persistence.DeliverPrestigeRewardAsync(player.SelectedCharacter!, config.PrestigeRealm, purchase, (group, number, token) =>
                {
                    var definition = player.GameContext.Configuration.Items.Single(d => d.Group == group && d.Number == number);
                    if (definition.Width != 1 || definition.Height != 1 || definition.Durability != 1) { throw new InvalidOperationException("Unverified item definition."); }
                    // Check space before creating a tracked entity; no in-memory placeholder item.
                    var probe = new Item { Definition = definition };
                    var slot = player.Inventory?.CheckInvSpace(probe) ?? throw new InvalidOperationException("Inventory full.");
                    var item = player.PersistenceContext.CreateNew<Item>();
                    item.Definition = definition; item.ItemSlot = slot; item.Level = 0; item.Durability = 1;
                    player.SelectedCharacter!.Inventory!.Items.Add(item);
                    return ValueTask.FromResult(item);
                }).ConfigureAwait(false);
                await player.ShowBlueMessageAsync($"Prestige delivery: {status}. Reconnect to refresh inventory.").ConfigureAwait(false);
            }
            finally
            {
                // Reload after success and after uncertain/failed commit. Poisoned context cannot save stale state.
                await player.DisconnectAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }
}
