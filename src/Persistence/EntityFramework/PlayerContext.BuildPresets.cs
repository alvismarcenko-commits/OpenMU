// <copyright file="PlayerContext.BuildPresets.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework;

using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

internal partial class PlayerContext : IBuildPresetContext
{
    /// <inheritdoc />
    public async ValueTask<(int ClassNumber, int[] Targets)?> ReadBuildPresetAsync(DataModel.Entities.Character character, int slot)
    {
        this.EnsurePrestigeStagingDatabase();
        using var contextLock = await this.LockAsync(default).ConfigureAwait(false);
        this.EnsureExchangeContextValid();
        await this.Context.Database.OpenConnectionAsync().ConfigureAwait(false);
        try
        {
            await using var command = this.Context.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                SELECT class_number, targets FROM aether_build.presets
                WHERE character_id=@character AND realm='ascension' AND slot=@slot
                """;
            Add(command, "character", ((Character)character).Id);
            Add(command, "slot", slot);
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            return await reader.ReadAsync().ConfigureAwait(false) ? (reader.GetInt32(0), await reader.GetFieldValueAsync<int[]>(1).ConfigureAwait(false)) : null;
        }
        finally
        {
            await this.Context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask SaveBuildPresetAsync(DataModel.Entities.Character character, int slot, int classNumber, int[] targets)
    {
        this.EnsurePrestigeStagingDatabase();
        using var contextLock = await this.LockAsync(default).ConfigureAwait(false);
        this.EnsureExchangeContextValid();
        await this.Context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO aether_build.presets (character_id, realm, slot, class_number, targets)
            VALUES ({((Character)character).Id}, 'ascension', {slot}, {classNumber}, {targets})
            ON CONFLICT (character_id, realm, slot) DO UPDATE
            SET class_number=EXCLUDED.class_number, targets=EXCLUDED.targets
            """).ConfigureAwait(false);
    }
}
