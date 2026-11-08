// <copyright file="AetherHelpChatCommandPlugIn.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.PlugIns;

/// <summary>Public informational command for the Ascension staging game.</summary>
[Guid("c4de76a9-1f48-4d25-833a-fd867054c8df")]
[PlugIn]
[ChatCommandHelp(Command)]
public sealed class AetherHelpChatCommandPlugIn : IChatCommandPlugIn
{
    private const string Command = "/aether";

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public ValueTask HandleCommandAsync(Player player, string command)
    {
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true" || player.SelectedCharacter is null)
        {
            return ValueTask.CompletedTask;
        }

        return player.InvokeViewPlugInAsync<IShowMessagePlugIn>(view => view.ShowMessageAsync(
            "AetherMU: Rules, guides and events at https://aethermu.eu. Reset help: /resetinfo.", MessageType.BlueNormal));
    }
}
