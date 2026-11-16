// <copyright file="FixClassicWorldConfigurationUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Repairs the known Season 6 configuration defects without replacing customized data.
/// </summary>
[PlugIn]
[Guid("2BBBD772-6DA8-4E01-B4BC-5F13ED91C814")]
public class FixClassicWorldConfigurationUpdatePlugIn : UpdatePlugInBase
{
    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => Properties.PlugInResources.ResourceManager.GetString("FixClassicWorldConfiguration_Name")!;

    /// <inheritdoc />
    public override string Description => Properties.PlugInResources.ResourceManager.GetString("FixClassicWorldConfiguration_Description")!;

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        RepairArena(gameConfiguration);
        RepairHanzo(gameConfiguration);
        RepairTerrain(gameConfiguration, 80, "74649A990315A44C7544E8E35D6DDE22AC3AEB02FFAFB4F7C5175DA773F549F7");
        RepairTerrain(gameConfiguration, 81, "2F2E4469BE2ED4FB73D4A7B90CFEA3E75FCB5D26213A634F18C999A815B682F2");
        return default;
    }

    private static void RepairArena(GameConfiguration configuration)
    {
        var arena = configuration.Maps.FirstOrDefault(map => map.Number == 6 && map.Discriminator == 0);
        if (arena?.TerrainData is null)
        {
            return;
        }

        var terrain = new GameMapTerrain(arena);
        foreach (var gate in arena.ExitGates.Where(gate => gate.IsSpawnGate && gate.X1 == 107 && gate.X2 == 107 && gate.Y1 == gate.Y2 && gate.Y1 is 114 or 115))
        {
            // Both destinations are inside the existing Arena entrance rectangle (gate 50).
            // Preserve an operator's terrain which already makes the old gate walkable.
            var x = (byte)(gate.Y1 == 115 ? 101 : 102);
            if (terrain.GetWalkableCoordinate(gate) is not null || !terrain.WalkMap[x, 115])
            {
                continue;
            }

            gate.X1 = gate.X2 = x;
            gate.Y1 = gate.Y2 = 115;
        }
    }

    private static void RepairHanzo(GameConfiguration configuration)
    {
        var store = configuration.Monsters.FirstOrDefault(monster => monster.Number == 251)?.MerchantStore;
        if (store is null || !store.Items.Any(item => item.ItemSlot == 73 && item.Definition is { Group: 0, Number: 6 }))
        {
            return;
        }

        var conflicts = store.Items.Where(item => item.ItemSlot == 73 && item.Definition is { Group: 0, Number: 7 }).ToList();
        if (conflicts.Count == 1 && CanPlace(store.Items, conflicts[0], 76))
        {
            // Slot 76 has room for the full 1x3 Falchion; no price/options/items change.
            conflicts[0].ItemSlot = 76;
        }
    }

    private static bool CanPlace(IEnumerable<Item> items, Item movedItem, byte slot)
    {
        const int columns = 8;
        const int rows = 15;
        if (movedItem.Definition is not { } definition || (slot % columns) + definition.Width > columns || (slot / columns) + definition.Height > rows)
        {
            return false;
        }

        return !items.Where(item => item != movedItem && item.Definition is not null).Any(item =>
            slot % columns < (item.ItemSlot % columns) + item.Definition!.Width
            && (slot % columns) + definition.Width > item.ItemSlot % columns
            && slot / columns < (item.ItemSlot / columns) + item.Definition.Height
            && (slot / columns) + definition.Height > item.ItemSlot / columns);
    }

    private static void RepairTerrain(GameConfiguration configuration, byte mapNumber, string legacyHash)
    {
        var map = configuration.Maps.FirstOrDefault(map => map.Number == mapNumber && map.Discriminator == 0);
        if (map?.TerrainData is not { } data || Convert.ToHexString(SHA256.HashData(data)) != legacyHash)
        {
            return;
        }

        // Only the two identified legacy byte streams are replaced. All 65536 cell
        // flags are unchanged and match the installed client's decoded low-byte flags.
        map.TerrainData = TerrainUpdateHelper.ReadTerrainResource($"Terrain{mapNumber + 1}.att");
    }
}
