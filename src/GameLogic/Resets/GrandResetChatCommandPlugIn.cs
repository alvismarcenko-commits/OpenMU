// <copyright file="GrandResetChatCommandPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Runtime.InteropServices;
using MUnique.OpenMU.GameLogic.PlugIns.ChatCommands;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// A chat command plugin which handles reset command.
/// </summary>
/// <seealso cref="MUnique.OpenMU.GameLogic.PlugIns.ChatCommands.IChatCommandPlugIn" />
[Guid("F94D795C-F67B-48AD-8112-EA45DA39D10A")]
[PlugIn]
[Display(Name = nameof(PlugInResources.GrandResetChatCommandPlugIn_Name), Description = nameof(PlugInResources.GrandResetChatCommandPlugIn_Description), ResourceType = typeof(PlugInResources))]
[ChatCommandHelp(Command)]
public class GrandResetChatCommandPlugIn : IChatCommandPlugIn, IDisabledByDefault
{
    private const string Command = "/grandreset";

    /// <inheritdoc />
    public string Key => Command;

    /// <inheritdoc />
    public CharacterStatus MinCharacterStatusRequirement => CharacterStatus.Normal;

    /// <inheritdoc />
    public async ValueTask HandleCommandAsync(Player player, string command)
    {
        var resetAction = new GrandResetCharacterAction(player);
        await resetAction.GrandResetCharacterAsync().ConfigureAwait(false);
    }
}