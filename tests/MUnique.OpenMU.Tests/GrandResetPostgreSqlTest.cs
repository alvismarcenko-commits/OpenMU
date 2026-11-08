// <copyright file="GrandResetPostgreSqlTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.GameLogic.Resets;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.EntityFramework;
using MUnique.OpenMU.Persistence.EntityFramework.Model;
using Npgsql;

/// <summary>
/// Real PostgreSQL persistence tests. Only a fresh nonce-named database is accepted.
/// No default OpenMU connection configuration or migrations are used.
/// </summary>
[TestFixture]
[NonParallelizable]
public partial class GrandResetPostgreSqlTest
{
    private string _connectionString = string.Empty;

    /// <summary>
    /// Validates both requested and server-reported identities before the first DDL write.
    /// </summary>
    [OneTimeSetUp]
    public async Task SetUpAsync()
    {
        this._connectionString = Environment.GetEnvironmentVariable("AETHER_GR_TEST_CONNECTION") ?? string.Empty;
        if (string.IsNullOrEmpty(this._connectionString))
        {
            Assert.Ignore("AETHER_GR_TEST_CONNECTION missing; PostgreSQL integration NOT EXECUTED.");
        }

        var token = Environment.GetEnvironmentVariable("AETHER_GR_TEST_TOKEN");
        var builder = new NpgsqlConnectionStringBuilder(this._connectionString);
        ValidateTarget(builder.Database, token);

        await using var connection = new NpgsqlConnection(this._connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var identity = new NpgsqlCommand("SELECT current_database(), shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = current_database()", connection);
        await using (var reader = await identity.ExecuteReaderAsync().ConfigureAwait(false))
        {
            Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.True);
            Assert.That(reader.GetString(0), Is.EqualTo(builder.Database));
            Assert.That(reader.IsDBNull(1) ? null : reader.GetString(1), Is.EqualTo("aether-gr-disposable:" + token));
        }

        await using var empty = new NpgsqlCommand("SELECT count(*) FROM pg_tables WHERE schemaname NOT IN ('pg_catalog', 'information_schema')", connection);
        var tableCount = Convert.ToInt64(await empty.ExecuteScalarAsync().ConfigureAwait(false));
        var restartPhase = ValidateRestartPhase(Environment.GetEnvironmentVariable("AETHER_GR_RESTART_PHASE"));
        if (restartPhase is not null)
        {
            Assert.That(restartPhase, Is.AnyOf("write", "verify"), "Unknown restart phase.");
            Assert.That(tableCount, Is.GreaterThan(0), "The runner must provision this isolated database before restart phases.");
            return;
        }

        Assert.That(tableCount, Is.Zero, "Disposable database must initially be empty.");
        await using var context = this.CreateContext();
        await context.Database.ExecuteSqlRawAsync(context.Database.GenerateCreateScript()).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists the actual OpenMU character, stats, inventory and equipment in one commit.
    /// </summary>
    [TestCase(0, 1, 100)]
    [TestCase(19, 20, 2000)]
    [TestCase(20, 21, 2000)]
    [TestCase(99, 100, 2000)]
    public async Task ExchangeSurvivesIndependentContextAsync(int before, int after, int points)
    {
        var id = await this.SeedAsync(before).ConfigureAwait(false);
        Guid[] itemIds;
        await using (var context = this.CreateContext())
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            itemIds = character.RawInventory.RawItems.Select(i => i.Id).Order().ToArray();
            ApplyExchange(character);
            using var persistence = CreatePersistence(context);
            await persistence.SaveCharacterExchangeAsync(character).ConfigureAwait(false);
        }

        await using var reopened = this.CreateContext();
        var stored = await LoadAsync(reopened, id).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.Zero);
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value, Is.EqualTo(after));
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == LevelId).Value, Is.EqualTo(1));
            Assert.That(stored.Experience, Is.Zero);
            Assert.That(stored.LevelUpPoints, Is.EqualTo(points));
            Assert.That(stored.RawInventory.RawItems.Select(i => i.Id).Order(), Is.EqualTo(itemIds));
            Assert.That(stored.RawInventory.RawItems.Select(i => (i.ItemSlot, i.Level, i.Durability)).OrderBy(i => i.ItemSlot),
                Is.EqualTo(new[] { ((byte)0, (byte)9, (byte)70), ((byte)20, (byte)7, (byte)50) }));
        });
    }

    /// <summary>
    /// Independent stale contexts race for the same persisted character; only one may commit.
    /// </summary>
    [Test]
    public async Task ConcurrentContextsCommitOnceAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        var contexts = Enumerable.Range(0, 8).Select(_ => this.CreateContext()).ToArray();
        try
        {
            var characters = await Task.WhenAll(contexts.Select(c => LoadAsync(c, id))).ConfigureAwait(false);
            foreach (var character in characters)
            {
                ApplyExchange(character);
            }

            var results = await Task.WhenAll(contexts.Select(async (context, index) =>
            {
                using var persistence = CreatePersistence(context);
                try
                {
                    await persistence.SaveCharacterExchangeAsync(characters[index]).ConfigureAwait(false);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
                    return false;
                }
            })).ConfigureAwait(false);
            Assert.That(results.Count(r => r), Is.EqualTo(1));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
        }

        await using var verify = this.CreateContext();
        var stored = await LoadAsync(verify, id).ConfigureAwait(false);
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value, Is.EqualTo(1));
        Assert.That(stored.LevelUpPoints, Is.EqualTo(100));
    }

    /// <summary>
    /// A server rejection rolls back every write; a failed context cannot later save a restored snapshot.
    /// </summary>
    [Test]
    public async Task RejectedSaveRollsBackAllProgressAsync()
    {
        var id = await this.SeedAsync(7).ConfigureAwait(false);
        await using (var context = this.CreateContext(new RejectSave()))
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            ApplyExchange(character);
            using var persistence = CreatePersistence(context);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveCharacterExchangeAsync(character).ConfigureAwait(false));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
        }

        await using var reopened = this.CreateContext();
        var stored = await LoadAsync(reopened, id).ConfigureAwait(false);
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.EqualTo(100));
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value, Is.EqualTo(7));
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == LevelId).Value, Is.EqualTo(400));
        Assert.That(stored.LevelUpPoints, Is.EqualTo(42));
        Assert.That(stored.Experience, Is.EqualTo(123));
        Assert.That(stored.RawInventory.RawItems.Count, Is.EqualTo(2));
    }

    /// <summary>
    /// A commit acknowledgement failure must not allow a restored snapshot to overwrite the commit.
    /// </summary>
    [Test]
    public async Task LostCommitAcknowledgementRequiresReloadAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await using (var context = this.CreateContext(new LoseAcknowledgement()))
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            ApplyExchange(character);
            using var persistence = CreatePersistence(context);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveCharacterExchangeAsync(character).ConfigureAwait(false));
            character.LevelUpPoints = 42;
            character.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value = 0;
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
        }

        await using var reopened = this.CreateContext();
        var stored = await LoadAsync(reopened, id).ConfigureAwait(false);
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value, Is.EqualTo(1));
        Assert.That(stored.LevelUpPoints, Is.EqualTo(100));
    }

    /// <summary>
    /// Runs in two independent test processes around an actual PostgreSQL container restart.
    /// The runner owns the database and supplies the phase and external baseline manifest.
    /// </summary>
    [Test]
    [Explicit("Run only through the isolated restart orchestrator.")]
    public async Task RestartDurabilityAsync()
    {
        var phase = Environment.GetEnvironmentVariable("AETHER_GR_RESTART_PHASE");
        var manifestPath = Environment.GetEnvironmentVariable("AETHER_GR_RESTART_MANIFEST");
        Assert.That(manifestPath, Is.Not.Null.And.Not.Empty);
        if (phase == "write")
        {
            var id = await this.SeedAsync(20).ConfigureAwait(false);
            await using var context = this.CreateContext();
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            var inventory = DescribeInventory(character);
            ApplyExchange(character);
            using var persistence = CreatePersistence(context);
            await persistence.SaveCharacterExchangeAsync(character).ConfigureAwait(false);
            var manifest = System.Text.Json.JsonSerializer.Serialize(new { CharacterId = id, Inventory = inventory });
            await System.IO.File.WriteAllTextAsync(manifestPath!, manifest).ConfigureAwait(false);
            return;
        }

        Assert.That(phase, Is.EqualTo("verify"));
        using var baseline = System.Text.Json.JsonDocument.Parse(await System.IO.File.ReadAllTextAsync(manifestPath!).ConfigureAwait(false));
        await using var reopened = this.CreateContext();
        var stored = await LoadAsync(reopened, baseline.RootElement.GetProperty("CharacterId").GetGuid()).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.Zero);
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value, Is.EqualTo(21));
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == LevelId).Value, Is.EqualTo(1));
            Assert.That(stored.Experience, Is.Zero);
            Assert.That(stored.LevelUpPoints, Is.EqualTo(2000));
            Assert.That(DescribeInventory(stored), Is.EqualTo(baseline.RootElement.GetProperty("Inventory").GetString()));
        });
    }

    private static string DescribeInventory(Character character)
        => System.Text.Json.JsonSerializer.Serialize(character.RawInventory.RawItems.OrderBy(i => i.Id).Select(i => new { i.Id, i.ItemSlot, i.Level, i.Durability }));

    /// <summary>
    /// Builds the real account schema without opening a connection.
    /// </summary>
    /// <returns>The schema DDL.</returns>
    internal static string BuildTestSchema()
    {
        using var context = new TestAccountContext("Host=127.0.0.1;Database=never_connect;Username=test", null);
        return context.Database.GenerateCreateScript();
    }

    /// <summary>
    /// Treats unset and empty runner environment values identically, rejecting unknown phases.
    /// </summary>
    /// <param name="phase">The supplied runner phase.</param>
    /// <returns>The explicit continuation phase, or null for a fresh database.</returns>
    internal static string? ValidateRestartPhase(string? phase)
    {
        if (string.IsNullOrEmpty(phase))
        {
            return null;
        }

        if (phase is not "write" and not "verify")
        {
            throw new InvalidOperationException("Unknown restart phase.");
        }

        return phase;
    }

    /// <summary>
    /// Rejects ambiguous targets before opening a database connection.
    /// </summary>
    /// <param name="database">Requested database.</param>
    /// <param name="token">Fresh runner nonce.</param>
    internal static void ValidateTarget(string? database, string? token)
    {
        if (token is null || !Regex.IsMatch(token, "\\A[a-f0-9]{32}\\z") || database != "aether_gr_test_" + token)
        {
            throw new InvalidOperationException("Refusing non-isolated PostgreSQL target.");
        }
    }

    private static Guid ResetId => GameLogic.Attributes.Stats.Resets.Id;

    private static Guid GrandResetId => GameLogic.Attributes.Stats.GrandResets.Id;

    private static Guid LevelId => GameLogic.Attributes.Stats.Level.Id;

    private static PlayerContext CreatePersistence(DbContext context)
        => new(context, new RepositoryProvider(NullLoggerFactory.Instance, null, new ContextStack()), NullLogger<PlayerContext>.Instance);

    private static Task<Character> LoadAsync(DbContext context, Guid id)
        => context.Set<Character>().Include(c => c.RawAttributes).Include(c => c.RawInventory).ThenInclude(i => i.RawItems).SingleAsync(c => c.Id == id);

    private static void ApplyExchange(Character character)
    {
        var resets = character.RawAttributes.Single(a => a.DefinitionId == ResetId);
        Assert.That(GrandResetCalculator.CanGrandReset((int)resets.Value), Is.True);
        var gr = character.RawAttributes.Single(a => a.DefinitionId == GrandResetId);
        gr.Value++;
        resets.Value = 0;
        character.RawAttributes.Single(a => a.DefinitionId == LevelId).Value = 1;
        character.Experience = 0;
        character.LevelUpPoints = GrandResetCalculator.CalculateBonusPoints((int)gr.Value);
    }

    private async Task<Guid> SeedAsync(int grandResets)
    {
        await using var context = this.CreateContext();
        var character = new Character { Id = Guid.NewGuid(), CharacterClassId = Guid.NewGuid(), CreateDate = DateTime.UtcNow, Name = Guid.NewGuid().ToString("N")[..10], Experience = 123, LevelUpPoints = 42 };
        character.RawAttributes.Add(new StatAttribute { Id = Guid.NewGuid(), DefinitionId = ResetId, Value = 100 });
        character.RawAttributes.Add(new StatAttribute { Id = Guid.NewGuid(), DefinitionId = GrandResetId, Value = grandResets });
        character.RawAttributes.Add(new StatAttribute { Id = Guid.NewGuid(), DefinitionId = LevelId, Value = 400 });
        character.Inventory = new ItemStorage { Id = Guid.NewGuid() };
        character.RawInventory.RawItems.Add(new Item { Id = Guid.NewGuid(), ItemSlot = 0, Level = 9, Durability = 70 });
        character.RawInventory.RawItems.Add(new Item { Id = Guid.NewGuid(), ItemSlot = 20, Level = 7, Durability = 50 });
        context.Add(character);
        await context.SaveChangesAsync().ConfigureAwait(false);
        return character.Id;
    }

    private TestAccountContext CreateContext(IInterceptor? interceptor = null)
        => new(this._connectionString, interceptor);

    private sealed class TestAccountContext(string connectionString, IInterceptor? interceptor) : AccountContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseNpgsql(connectionString);
            if (interceptor is not null)
            {
                optionsBuilder.AddInterceptors(interceptor);
            }
        }
    }

    private sealed class LoseAcknowledgement : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected lost acknowledgement after successful commit.");
    }

    private sealed class RejectSave : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Injected failure after SQL writes, before transaction commit.");
    }
}
