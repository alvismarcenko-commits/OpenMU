// <copyright file="GrandResetAttributesPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the persistent Grand Reset counter to existing Season Six character classes without enabling gameplay.
/// </summary>
[PlugIn]
[Display(Name = nameof(PlugInResources.GrandResetAttributesPlugIn_Name), Description = nameof(PlugInResources.GrandResetAttributesPlugIn_Description), ResourceType = typeof(PlugInResources))]
[Guid("33CF2935-96EE-40AD-B11C-0B7440F60419")]
public class GrandResetAttributesPlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => "Grand Reset persistence";

    /// <inheritdoc />
    public override string Description => "Adds the Grand Reset counter; does not enable Grand Reset.";

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        this.AddStatIfNotExists(context, gameConfiguration, Stats.GrandResets);

        // Some imported configurations contain equivalent attribute entries; reuse the existing definition.
        var definition = gameConfiguration.Attributes.First(a => a == Stats.GrandResets);
        foreach (var characterClass in gameConfiguration.CharacterClasses)
        {
            if (characterClass.StatAttributes.All(a => a.Attribute != definition))
            {
                characterClass.StatAttributes.Add(context.CreateNew<StatAttributeDefinition>(definition, 0, false));
            }
        }

        return default;
    }
}
