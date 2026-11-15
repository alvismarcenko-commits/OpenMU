// Licensed under MIT. Native OpenMU EF + APP PostgreSQL acceptance, not Windows gameplay.
namespace MUnique.OpenMU.Tests;
using System.Text.Json;
using System.IO;
using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence;
using Npgsql;

public partial class GrandResetPostgreSqlTest
{
    private async Task PreparePrestigeAsync(Guid id, string realm = "ascension", string count = "0")
    {
        await using var connection = new NpgsqlConnection(this._connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var sql = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AETHER_PRESTIGE_SQL")!).ConfigureAwait(false);
        await using var setup = new NpgsqlCommand(sql, connection);
        await setup.ExecuteNonQueryAsync().ConfigureAwait(false);
        await using var deliverySetup = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("AETHER_PRESTIGE_SQL"))!, "002-delivery.sql")).ConfigureAwait(false), connection);
        await deliverySetup.ExecuteNonQueryAsync().ConfigureAwait(false);
        await using var rulesSetup = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("AETHER_PRESTIGE_SQL"))!, "003-gr-app-100.sql")).ConfigureAwait(false), connection);
        await rulesSetup.ExecuteNonQueryAsync().ConfigureAwait(false);
        // Explicit test-only activation in the nonce-owned isolated fixture.
        await using var seed = new NpgsqlCommand("UPDATE aether_prestige.rules SET activated=true; INSERT INTO aether_prestige.characters(realm,character_id,website_owner,game_account,name,gr_count,migration_state) VALUES(@realm,@id,1,'fixture','Fixture',CAST(@count AS numeric),'approved')", connection);
        seed.Parameters.AddWithValue("realm", realm);
        seed.Parameters.AddWithValue("id", id);
        seed.Parameters.AddWithValue("count", count);
        await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<string> PrestigeStateAsync(Guid id, string realm = "ascension")
    {
        await using var connection = new NpgsqlConnection(this._connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("SELECT gr_count::text||':'||balance::text FROM aether_prestige.characters WHERE realm=@realm AND character_id=@id", connection);
        cmd.Parameters.AddWithValue("realm", realm); cmd.Parameters.AddWithValue("id", id);
        return (string)(await cmd.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    [TestCase("origin", "100", 25)]
    [TestCase("legacy", "100", 50)]
    [TestCase("evolution", "100", 75)]
    [TestCase("ascension", "100", 100)]
    public async Task PrestigeAtomicNativeExchangeAsync(string realm, string award, int threshold)
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id, realm).ConfigureAwait(false);
        string inventory;
        await using (var context = this.CreateContext())
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            character.RawAttributes.Single(a => a.DefinitionId == ResetId).Value = threshold;
            await context.SaveChangesAsync().ConfigureAwait(false);
            inventory = DescribeInventory(character);
            character.RawAttributes.Single(a => a.DefinitionId == ResetId).Value = 0;
            character.RawAttributes.Single(a => a.DefinitionId == LevelId).Value = 1;
            character.Experience = 0; character.LevelUpPoints = 0;
            // Legacy float counter is retained for migration history, not authoritative Prestige.
            character.RawAttributes.Single(a => a.DefinitionId == GrandResetId).Value = 0;
            using var persistence = CreatePersistence(context);
            var receipt = await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, realm, Guid.NewGuid().ToString(), threshold).ConfigureAwait(false);
            using var data = JsonDocument.Parse(receipt);
            Assert.That(data.RootElement.GetProperty("app_granted").GetString(), Is.EqualTo(award));
            var profile = await ((IPrestigeCharacterContext)persistence).ReadPrestigeAsync(character, realm).ConfigureAwait(false);
            Assert.That(profile, Is.EqualTo(("1", award)));
            var otherRealm = realm == "origin" ? "ascension" : "origin";
            Assert.That(await ((IPrestigeCharacterContext)persistence).ReadPrestigeAsync(character, otherRealm).ConfigureAwait(false), Is.Null);
        }
        await using var verify = this.CreateContext();
        var stored = await LoadAsync(verify, id).ConfigureAwait(false);
        Assert.Multiple(() =>
        {
            Assert.That(stored.LevelUpPoints, Is.Zero);
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == LevelId).Value, Is.EqualTo(1));
            Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.Zero);
            Assert.That(DescribeInventory(stored), Is.EqualTo(inventory));
        });
        Assert.That(await this.PrestigeStateAsync(id, realm).ConfigureAwait(false), Is.EqualTo("1:" + award));
    }

    [Test]
    public async Task PrestigeFailedCreditRollsBackNativeAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        // No APP enrollment: SQL credit must fail after native SaveChanges, before commit.
        await using (var context = this.CreateContext())
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            ApplyExchange(character); character.LevelUpPoints = 0;
            using var persistence = CreatePersistence(context);
            Assert.CatchAsync(async () => await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", Guid.NewGuid().ToString(), 100).ConfigureAwait(false));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
        }
        await using var verify = this.CreateContext();
        var stored = await LoadAsync(verify, id).ConfigureAwait(false);
        Assert.That(stored.LevelUpPoints, Is.EqualTo(42));
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.EqualTo(100));
    }

    [Test]
    public async Task PrestigeCountersBeyondFloatAndLongAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id, count: "10000000000000000000000000000000000000000000000000000000000000000000000000000000000").ConfigureAwait(false);
        await using var context = this.CreateContext();
        var character = await LoadAsync(context, id).ConfigureAwait(false);
        ApplyExchange(character); character.LevelUpPoints = 0;
        using var persistence = CreatePersistence(context);
        await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", Guid.NewGuid().ToString(), 100).ConfigureAwait(false);
        Assert.That(await this.PrestigeStateAsync(id).ConfigureAwait(false), Is.EqualTo("10000000000000000000000000000000000000000000000000000000000000000000000000000000001:100"));
    }

    [Test]
    public async Task PrestigeLostAcknowledgementCannotUndoCreditAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id).ConfigureAwait(false);
        await using (var context = this.CreateContext(new LoseAcknowledgement()))
        {
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            ApplyExchange(character); character.LevelUpPoints = 0;
            using var persistence = CreatePersistence(context);
            Assert.CatchAsync(async () => await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", Guid.NewGuid().ToString(), 100).ConfigureAwait(false));
            character.LevelUpPoints = 42;
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
        }
        Assert.That(await this.PrestigeStateAsync(id).ConfigureAwait(false), Is.EqualTo("1:100"));
    }

    [Test]
    public async Task PrestigeTwoNativeContextsCreditOnceAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id).ConfigureAwait(false);
        await using var first = this.CreateContext();
        await using var second = this.CreateContext();
        var a = await LoadAsync(first, id).ConfigureAwait(false);
        var b = await LoadAsync(second, id).ConfigureAwait(false);
        ApplyExchange(a); a.LevelUpPoints = 0;
        ApplyExchange(b); b.LevelUpPoints = 0;
        using var pa = CreatePersistence(first);
        using var pb = CreatePersistence(second);
        async Task<bool> Attempt(IPrestigeCharacterContext context, MUnique.OpenMU.DataModel.Entities.Character character)
        {
            try
            {
                await context.ExecutePrestigeExchangeAsync(character, "ascension", Guid.NewGuid().ToString(), 100).ConfigureAwait(false);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        var outcomes = await Task.WhenAll(Attempt((IPrestigeCharacterContext)pa, a), Attempt((IPrestigeCharacterContext)pb, b)).ConfigureAwait(false);
        Assert.That(outcomes.Count(x => x), Is.EqualTo(1));
        Assert.That(await this.PrestigeStateAsync(id).ConfigureAwait(false), Is.EqualTo("1:100"));
    }

    private async Task<Guid> ReserveNativeFixtureAsync(Guid characterId)
    {
        var purchase = Guid.NewGuid();
        await using var db = new NpgsqlConnection(this._connectionString);
        await db.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("INSERT INTO aether_prestige.purchases VALUES(@purchase,'ascension',@character,'treasury',5,1,'fixture','pending',0); INSERT INTO aether_prestige.delivery(purchase_id,realm,character_id,item_group,item_number,quantity,status,created_at) VALUES(@purchase,'ascension',@character,12,15,1,'pending',0)", db);
        command.Parameters.AddWithValue("purchase", purchase); command.Parameters.AddWithValue("character", characterId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return purchase;
    }

    [Test]
    public async Task PrestigeNativeRewardPersistsOnceAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id).ConfigureAwait(false);
        var purchase = await this.ReserveNativeFixtureAsync(id).ConfigureAwait(false);
        var calls = 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var context = this.CreateContext();
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            using var persistence = CreatePersistence(context);
            var result = await ((IPrestigeCharacterContext)persistence).DeliverPrestigeRewardAsync(character, "ascension", purchase, (group, number, token) =>
            {
                calls++;
                var definition = new MUnique.OpenMU.Persistence.EntityFramework.Model.ItemDefinition { Id = Guid.NewGuid(), Group = (byte)group, Number = (short)number, Width = 1, Height = 1, Durability = 1 };
                var item = new MUnique.OpenMU.Persistence.EntityFramework.Model.Item { Id = Guid.NewGuid(), Definition = definition, ItemSlot = 21, Durability = 1 };
                character.RawInventory.RawItems.Add(item);
                context.Add(item);
                return ValueTask.FromResult<MUnique.OpenMU.DataModel.Entities.Item>(item);
            }).ConfigureAwait(false);
            Assert.That(result, Is.EqualTo("delivered"));
        }
        Assert.That(calls, Is.EqualTo(1));
        await using var verify = this.CreateContext();
        var stored = await LoadAsync(verify, id).ConfigureAwait(false);
        Assert.That(stored.RawInventory.RawItems.Count, Is.EqualTo(3));
        Assert.That(stored.RawInventory.RawItems.Single(i => i.ItemSlot == 21).Durability, Is.EqualTo(1));
    }

    [Test]
    public async Task PrestigeFullInventoryKeepsReservationAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id).ConfigureAwait(false);
        var purchase = await this.ReserveNativeFixtureAsync(id).ConfigureAwait(false);
        await using var context = this.CreateContext();
        var character = await LoadAsync(context, id).ConfigureAwait(false);
        using var persistence = CreatePersistence(context);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IPrestigeCharacterContext)persistence).DeliverPrestigeRewardAsync(character, "ascension", purchase,
            (group, number, token) => throw new InvalidOperationException("Inventory full")).ConfigureAwait(false));
        await using var db = new NpgsqlConnection(this._connectionString); await db.OpenAsync().ConfigureAwait(false);
        await using var check = new NpgsqlCommand("SELECT status FROM aether_prestige.delivery WHERE purchase_id=@purchase", db); check.Parameters.AddWithValue("purchase", purchase);
        Assert.That(await check.ExecuteScalarAsync().ConfigureAwait(false), Is.EqualTo("pending"));
    }

    [Test]
    public async Task PrestigeNativeReplayCannotEraseLaterProgressAsync()
    {
        var id = await this.SeedAsync(0).ConfigureAwait(false);
        await this.PreparePrestigeAsync(id).ConfigureAwait(false);
        var operation = Guid.NewGuid().ToString();
        await using (var first = this.CreateContext())
        {
            var character = await LoadAsync(first, id).ConfigureAwait(false);
            ApplyExchange(character); character.LevelUpPoints = 0;
            using var persistence = CreatePersistence(first);
            await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", operation, 100).ConfigureAwait(false);
        }
        await using (var progress = this.CreateContext())
        {
            var character = await LoadAsync(progress, id).ConfigureAwait(false);
            character.Experience = 456; character.LevelUpPoints = 17;
            character.RawAttributes.Single(a => a.DefinitionId == ResetId).Value = 50;
            await progress.SaveChangesAsync().ConfigureAwait(false);
        }
        await using (var stale = this.CreateContext())
        {
            var character = await LoadAsync(stale, id).ConfigureAwait(false);
            character.Experience = 0; character.LevelUpPoints = 0;
            character.RawAttributes.Single(a => a.DefinitionId == ResetId).Value = 0;
            using var persistence = CreatePersistence(stale);
            Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", operation, 100).ConfigureAwait(false));
            Assert.ThrowsAsync<InvalidOperationException>(async () => await persistence.SaveChangesAsync().ConfigureAwait(false));
        }
        await using var verify = this.CreateContext();
        var stored = await LoadAsync(verify, id).ConfigureAwait(false);
        Assert.That(stored.Experience, Is.EqualTo(456));
        Assert.That(stored.LevelUpPoints, Is.EqualTo(17));
        Assert.That(stored.RawAttributes.Single(a => a.DefinitionId == ResetId).Value, Is.EqualTo(50));
        Assert.That(await this.PrestigeStateAsync(id).ConfigureAwait(false), Is.EqualTo("1:100"));
    }

    [Test]
    public async Task PrestigeDurabilityAsync()
    {
        var path = Environment.GetEnvironmentVariable("AETHER_PRESTIGE_RESTART_MANIFEST")!;
        if (Environment.GetEnvironmentVariable("AETHER_GR_RESTART_PHASE") == "write")
        {
            var id = await this.SeedAsync(0).ConfigureAwait(false);
            await this.PreparePrestigeAsync(id).ConfigureAwait(false);
            await using var context = this.CreateContext();
            var character = await LoadAsync(context, id).ConfigureAwait(false);
            var inventory = DescribeInventory(character);
            ApplyExchange(character); character.LevelUpPoints = 0;
            using var persistence = CreatePersistence(context);
            await ((IPrestigeCharacterContext)persistence).ExecutePrestigeExchangeAsync(character, "ascension", Guid.NewGuid().ToString(), 100).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { id, inventory })).ConfigureAwait(false);
        }
        else
        {
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            var id = data.RootElement.GetProperty("id").GetGuid();
            Assert.That(await this.PrestigeStateAsync(id).ConfigureAwait(false), Is.EqualTo("1:100"));
            await using var context = this.CreateContext();
            var stored = await LoadAsync(context, id).ConfigureAwait(false);
            Assert.That(stored.LevelUpPoints, Is.Zero);
            Assert.That(DescribeInventory(stored), Is.EqualTo(data.RootElement.GetProperty("inventory").GetString()));
        }
    }
}
