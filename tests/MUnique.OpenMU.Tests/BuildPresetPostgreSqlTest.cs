// <copyright file="BuildPresetPostgreSqlTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.IO;
using MUnique.OpenMU.Persistence;
using Npgsql;

public partial class GrandResetPostgreSqlTest
{
    /// <summary>Persists an Ascension build across contexts and isolates the slot and character.</summary>
    [Test]
    public async Task BuildPresetSurvivesReconnectAsync()
    {
        var migration = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("AETHER_PRESTIGE_SQL"))!, "005-build-presets.sql");
        await using (var connection = new NpgsqlConnection(this._connectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var sql = new NpgsqlCommand(await File.ReadAllTextAsync(migration).ConfigureAwait(false), connection);
            await sql.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var id = await this.SeedAsync(0).ConfigureAwait(false);
        var otherId = await this.SeedAsync(0).ConfigureAwait(false);
        int[] targets = [38, 20, 27, 10, 0];
        await using (var context = this.CreateContext())
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            using var persistence = CreatePersistence(context);
            await ((IBuildPresetContext)persistence).SaveBuildPresetAsync(character, 2, 16, targets).ConfigureAwait(false);
        }

        await using var reopened = this.CreateContext();
        var original = await LoadAsync(reopened, id).ConfigureAwait(false);
        var other = await LoadAsync(reopened, otherId).ConfigureAwait(false);
        using var reader = CreatePersistence(reopened);
        var presets = (IBuildPresetContext)reader;
        var build = await presets.ReadBuildPresetAsync(original, 2).ConfigureAwait(false);
        Assert.That(build, Is.Not.Null);
        Assert.That(build!.Value.ClassNumber, Is.EqualTo(16));
        Assert.That(build.Value.Targets, Is.EqualTo(targets));
        Assert.That(await presets.ReadBuildPresetAsync(original, 1).ConfigureAwait(false), Is.Null);
        Assert.That(await presets.ReadBuildPresetAsync(other, 2).ConfigureAwait(false), Is.Null);
        Assert.That(original.LevelUpPoints, Is.EqualTo(42));
    }
}
