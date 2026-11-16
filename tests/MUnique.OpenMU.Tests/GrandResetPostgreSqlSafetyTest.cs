// <copyright file="GrandResetPostgreSqlSafetyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

/// <summary>
/// Tests target rejection without opening any database connection.
/// </summary>
[TestFixture]
public class GrandResetPostgreSqlSafetyTest
{
    /// <summary>
    /// Existing and ambiguous database names must never be accepted.
    /// </summary>
    /// <param name="database">Unsafe target.</param>
    [TestCase("openmu")]
    [TestCase("OPENMU")]
    [TestCase("aether_ascension_test")]
    [TestCase("postgres")]
    [TestCase("aether_gr_test_")]
    [TestCase("")]
    public void RejectUnsafeDatabase(string database)
        => Assert.Throws<InvalidOperationException>(() => GrandResetPostgreSqlTest.ValidateTarget(database, new string('a', 32)));

    /// <summary>
    /// Confirms the fixture uses the production account entity mapping without connecting.
    /// </summary>
    [Test]
    public void AccountModelBuildsWithoutDatabase()
    {
        var script = GrandResetPostgreSqlTest.BuildTestSchema();
        Assert.That(script, Does.Contain("CREATE TABLE data."));
        Assert.That(script, Does.Contain("StatAttribute"));
        Assert.That(script, Does.Contain("Character"));
    }

    /// <summary>
    /// Profile semantics keep x1 disabled and the approved x100 rules enabled.
    /// </summary>
    [Test]
    public void ProfilesUseApprovedRules()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "deploy", "aethermu-europe", "x100.reset.json")))
        {
            directory = directory.Parent;
        }

        Assert.That(directory, Is.Not.Null, "Run from the isolated source checkout.");
        var profilePath = System.IO.Path.Combine(directory!.FullName, "deploy", "aethermu-europe");
        using var x1 = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(profilePath, "x1.reset.json")));
        using var x100 = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(profilePath, "x100.reset.json")));
        using var game = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(profilePath, "x100.game.json")));
        Assert.That(x1.RootElement.GetProperty("GrandResetEnabled").GetBoolean(), Is.False);
        Assert.That(x100.RootElement.GetProperty("GrandResetEnabled").GetBoolean(), Is.True);
        Assert.That(x100.RootElement.GetProperty("LevelAfterGrandReset").GetInt32(), Is.EqualTo(1));
        Assert.That(x100.RootElement.GetProperty("ResetStats").GetBoolean(), Is.True);
        Assert.That(x100.RootElement.GetProperty("ReplacePointsPerReset").GetBoolean(), Is.True);
        Assert.That(game.RootElement.GetProperty("ExperienceRate").GetInt32(), Is.EqualTo(100));
        Assert.That(game.RootElement.GetProperty("MasterExperienceRate").GetInt32(), Is.EqualTo(100));
    }

    /// <summary>
    /// Cross-platform empty environment values must not bypass fresh-database validation.
    /// </summary>
    [Test]
    public void EmptyRestartPhaseRequiresFreshDatabase()
    {
        Assert.That(GrandResetPostgreSqlTest.ValidateRestartPhase(null), Is.Null);
        Assert.That(GrandResetPostgreSqlTest.ValidateRestartPhase(string.Empty), Is.Null);
        Assert.That(GrandResetPostgreSqlTest.ValidateRestartPhase("write"), Is.EqualTo("write"));
        Assert.That(GrandResetPostgreSqlTest.ValidateRestartPhase("verify"), Is.EqualTo("verify"));
        Assert.Throws<InvalidOperationException>(() => GrandResetPostgreSqlTest.ValidateRestartPhase("unknown"));
    }

    /// <summary>
    /// An exact database/nonce match is required.
    /// </summary>
    [Test]
    public void RequireExactNonce()
    {
        var token = new string('a', 32);
        Assert.DoesNotThrow(() => GrandResetPostgreSqlTest.ValidateTarget("aether_gr_test_" + token, token));
        Assert.Throws<InvalidOperationException>(() => GrandResetPostgreSqlTest.ValidateTarget("aether_gr_test_" + token, new string('b', 32)));
        Assert.Throws<InvalidOperationException>(() => GrandResetPostgreSqlTest.ValidateTarget("aether_gr_test_" + token, null));
    }
}
