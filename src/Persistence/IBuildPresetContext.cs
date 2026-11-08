// <copyright file="IBuildPresetContext.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence;

using MUnique.OpenMU.DataModel.Entities;

/// <summary>Persists stat targets for the selected character in a Realm.</summary>
public interface IBuildPresetContext : IAtomicCharacterProgressContext
{
    /// <summary>Loads the saved class and stat targets for a slot.</summary>
    /// <param name="character">The selected native character.</param>
    /// <param name="slot">The preset slot.</param>
    /// <returns>The saved preset, if present.</returns>
    ValueTask<(int ClassNumber, int[] Targets)?> ReadBuildPresetAsync(Character character, int slot);

    /// <summary>Creates or replaces a preset without changing character progress.</summary>
    /// <param name="character">The selected native character.</param>
    /// <param name="slot">The preset slot.</param>
    /// <param name="classNumber">The character class identifier.</param>
    /// <param name="targets">The five base stat targets.</param>
    /// <returns>The database operation.</returns>
    ValueTask SaveBuildPresetAsync(Character character, int slot, int classNumber, int[] targets);
}
