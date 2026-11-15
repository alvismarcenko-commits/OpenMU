// <copyright file="ClassicWorldRestorationTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Tests;

using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.Persistence.Initialization.Updates;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Regression tests for fresh worlds, existing-world repair and operator customizations.
/// </summary>
[TestFixture]
public class ClassicWorldRestorationTests
{
    private IContext _context = null!;
    private GameConfiguration _configuration = null!;

    /// <summary>
    /// Initializes a complete Season 6 world in memory, without a database server.
    /// </summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        var provider = new InMemoryPersistenceContextProvider();
        var initialization = new VersionSeasonSix.DataInitialization(provider, NullLoggerFactory.Instance);
        await initialization.CreateInitialDataAsync(1, true).ConfigureAwait(false);
        this._context = provider.CreateNewContext();
        this._configuration = (await this._context.GetAsync<GameConfiguration>().ConfigureAwait(false)).First();
    }

    /// <summary>
    /// Disposes the isolated context.
    /// </summary>
    [TearDown]
    public void TearDown() => this._context.Dispose();

    /// <summary>
    /// Checks every initialized merchant rectangle, not just the two conflicting slots.
    /// </summary>
    [Test]
    public void FreshMerchantsFitWithoutOverlaps()
    {
        foreach (var monster in this._configuration.Monsters.Where(monster => monster.MerchantStore is not null))
        {
            var cells = new HashSet<(int X, int Y)>();
            foreach (var item in monster.MerchantStore!.Items)
            {
                var x = item.ItemSlot % 8;
                var y = item.ItemSlot / 8;
                Assert.That(x + item.Definition!.Width, Is.LessThanOrEqualTo(8), $"NPC {monster.Number}");
                Assert.That(y + item.Definition.Height, Is.LessThanOrEqualTo(15), $"NPC {monster.Number}");
                for (var dx = 0; dx < item.Definition.Width; dx++)
                {
                    for (var dy = 0; dy < item.Definition.Height; dy++)
                    {
                        Assert.That(cells.Add((x + dx, y + dy)), Is.True, $"NPC {monster.Number}, slot {item.ItemSlot}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Both corrected spawn points are walkable and adjacent to the existing entrance.
    /// </summary>
    [Test]
    public void FreshArenaSpawnGatesAreReachableFromEntrance()
    {
        var arena = this._configuration.Maps.Single(map => map.Number == 6);
        var terrain = new GameMapTerrain(arena);
        Assert.That(arena.ExitGates.Count(gate => gate.IsSpawnGate), Is.EqualTo(3));
        foreach (var gate in arena.ExitGates.Where(gate => gate.IsSpawnGate))
        {
            Assert.That(terrain.GetWalkableCoordinate(gate), Is.Not.Null);
        }

        Assert.That(terrain.WalkMap[101, 115], Is.True);
        Assert.That(terrain.WalkMap[102, 115], Is.True);
        Assert.That(arena.ExitGates.Count(gate => gate.X1 == gate.X2 && gate.Y1 == 115 && gate.Y2 == 115 && gate.X1 is 101 or 102), Is.EqualTo(2));
    }

    /// <summary>
    /// Karutan cell hashes were independently obtained by decoding the installed client.
    /// </summary>
    /// <param name="number">The map number.</param>
    /// <param name="cellHash">The independently observed cell hash.</param>
    [TestCase(80, "8CCEB73897A5E321695EAAA4576CCDFA45BFEBB5AB8C078223FD96F2EBEDE709")]
    [TestCase(81, "FCECA98E49AE386DF7D7EF6D01B56A865550AC3FCBA1E4E2D66636DB2A3B483D")]
    public void KarutanHasCanonicalHeaderAndUnchangedClientCells(int number, string cellHash)
    {
        var data = this._configuration.Maps.Single(map => map.Number == number).TerrainData!;
        Assert.That(data.Length, Is.EqualTo(65539));
        Assert.That(data.Take(3), Is.EqualTo(new byte[] { 0, 255, 255 }));
        Assert.That(Convert.ToHexString(SHA256.HashData(data.AsSpan(3))), Is.EqualTo(cellHash));
        var terrain = new GameMapTerrain(data);
        Assert.That(terrain.AttributeMap[0, 0], Is.EqualTo(data[3]));
        Assert.That(terrain.AttributeMap[255, 255], Is.EqualTo(data[^1]));
    }

    /// <summary>
    /// Restores the exact old defects, repairs them, and verifies repeat application and item identity.
    /// </summary>
    [Test]
    public async Task ExistingWorldRepairIsIdempotentAndPreservesEconomyAsync()
    {
        var arena = this._configuration.Maps.Single(map => map.Number == 6);
        var gates = arena.ExitGates.Where(gate => gate.X1 == gate.X2).ToList();
        for (var i = 0; i < gates.Count; i++)
        {
            gates[i].X1 = gates[i].X2 = 107;
            gates[i].Y1 = gates[i].Y2 = (byte)(114 + i);
        }

        var hanzo = this._configuration.Monsters.Single(monster => monster.Number == 251).MerchantStore!;
        var falchion = hanzo.Items.Single(item => item.Definition is { Group: 0, Number: 7 });
        var price = new ItemPriceCalculator().CalculateFinalBuyingPrice(falchion);
        var originalCount = hanzo.Items.Count;
        falchion.ItemSlot = 73;
        foreach (var map in this._configuration.Maps.Where(map => map.Number is 80 or 81))
        {
            map.TerrainData = new byte[] { 0, 255 }.Concat(map.TerrainData!.Skip(3)).Concat(new byte[2]).ToArray();
        }

        var update = new FixClassicWorldConfigurationUpdatePlugIn();
        await update.ApplyUpdateAsync(this._context, this._configuration).ConfigureAwait(false);
        var repairedTerrain = this._configuration.Maps.Single(map => map.Number == 80).TerrainData;
        await update.ApplyUpdateAsync(this._context, this._configuration).ConfigureAwait(false);
        Assert.That(falchion.ItemSlot, Is.EqualTo(76));
        Assert.That(hanzo.Items.Count, Is.EqualTo(originalCount));
        Assert.That(hanzo.Items.Contains(falchion), Is.True);
        Assert.That(new ItemPriceCalculator().CalculateFinalBuyingPrice(falchion), Is.EqualTo(price));
        Assert.That(this._configuration.Maps.Single(map => map.Number == 80).TerrainData, Is.SameAs(repairedTerrain));
        this.FreshArenaSpawnGatesAreReachableFromEntrance();
        this.FreshMerchantsFitWithoutOverlaps();
        this.KarutanHasCanonicalHeaderAndUnchangedClientCells(80, "8CCEB73897A5E321695EAAA4576CCDFA45BFEBB5AB8C078223FD96F2EBEDE709");
    }

    /// <summary>
    /// A modified terrain and an already customized Falchion location are not overwritten.
    /// </summary>
    [Test]
    public async Task UpdatePreservesCustomizedDataAsync()
    {
        var map = this._configuration.Maps.Single(map => map.Number == 80);
        map.TerrainData![1234] ^= 1;
        var data = map.TerrainData!.ToArray();
        var falchion = this._configuration.Monsters.Single(monster => monster.Number == 251).MerchantStore!.Items.Single(item => item.Definition is { Group: 0, Number: 7 });
        falchion.ItemSlot = 77;
        await new FixClassicWorldConfigurationUpdatePlugIn().ApplyUpdateAsync(this._context, this._configuration).ConfigureAwait(false);
        Assert.That(map.TerrainData, Is.EqualTo(data));
        Assert.That(falchion.ItemSlot, Is.EqualTo(77));
    }

    /// <summary>
    /// Exports the actual in-memory initialization for the read-only validator when requested.
    /// No persistence connection or deployment is used.
    /// </summary>
    [Test]
    public async Task FreshWorldIntegrityAndOptionalSnapshotAsync()
    {
        var configuration = this._configuration;
        Assert.That(configuration.Maps.Count, Is.EqualTo(73));
        Assert.That(configuration.Maps.Select(map => (map.Number, map.Discriminator)).Distinct().Count(), Is.EqualTo(73));
        var stores = configuration.Monsters.Where(monster => monster.MerchantStore is not null).Select(monster => monster.MerchantStore!).Distinct().ToList();
        Assert.That(stores.Sum(store => store.Items.Count), Is.EqualTo(585));
        var path = Environment.GetEnvironmentVariable("WORLD_VALIDATION_EXPORT");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var snapshot = new
        {
            GameMapDefinition = configuration.Maps.Select(map => new { ((MUnique.OpenMU.Persistence.IIdentifiable)map).Id, map.Number, map.Discriminator, Name = map.Name.ToString(), terrain_hex = Convert.ToHexString(map.TerrainData!) }),
            MonsterDefinition = configuration.Monsters.Select(monster => new { ((MUnique.OpenMU.Persistence.IIdentifiable)monster).Id, monster.Number }),
            MonsterSpawnArea = configuration.Maps.SelectMany(map => map.MonsterSpawns.Select(spawn => new { ((MUnique.OpenMU.Persistence.IIdentifiable)spawn).Id, GameMapId = ((MUnique.OpenMU.Persistence.IIdentifiable)map).Id, MonsterDefinitionId = ((MUnique.OpenMU.Persistence.IIdentifiable)spawn.MonsterDefinition!).Id, spawn.X1, spawn.X2, spawn.Y1, spawn.Y2, spawn.Quantity })),
            ExitGate = configuration.Maps.SelectMany(map => map.ExitGates.Select(gate => new { ((MUnique.OpenMU.Persistence.IIdentifiable)gate).Id, MapId = ((MUnique.OpenMU.Persistence.IIdentifiable)map).Id, gate.IsSpawnGate, gate.X1, gate.X2, gate.Y1, gate.Y2 })),
            ItemDefinition = configuration.Items.Select(item => new { ((MUnique.OpenMU.Persistence.IIdentifiable)item).Id, item.Number, item.Group, item.Width, item.Height }),
            ShopItems = stores.SelectMany(store => store.Items.Select(item => new { ((MUnique.OpenMU.Persistence.IIdentifiable)item).Id, ItemStorageId = ((MUnique.OpenMU.Persistence.IIdentifiable)store).Id, DefinitionId = ((MUnique.OpenMU.Persistence.IIdentifiable)item.Definition!).Id, item.ItemSlot })),
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot)).ConfigureAwait(false);
    }
}
