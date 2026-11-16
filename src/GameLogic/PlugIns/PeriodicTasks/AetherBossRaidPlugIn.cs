// <copyright file="AetherBossRaidPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using System.Runtime.InteropServices;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using Nito.AsyncEx;

/// <summary>Runs one native boss encounter on the isolated Ascension server.</summary>
[PlugIn]
[Guid("13ebcf59-b9df-4ef6-8390-8b0a242c061f")]
public sealed class AetherBossRaidPlugIn : IPeriodicTaskPlugIn, IAttackableGotKilledPlugIn, IDisabledByDefault
{
    private readonly AsyncLock _gate = new();
    private Encounter? _active;

    /// <summary>Starts or cancels an encounter from the authenticated local operator console.</summary>
    /// <param name="context">The game context.</param>
    /// <param name="start">Whether to start the encounter.</param>
    /// <returns>The operator receipt status.</returns>
    public async ValueTask<string> OperateAsync(IGameContext context, bool start)
    {
        using var gate = await this._gate.LockAsync().ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("AETHER_STAGE_PROFILE") != "complete-playable")
        {
            return "unavailable";
        }

        if (!start)
        {
            if (this._active is not { } active || active.Context != context)
            {
                return "inactive";
            }

            await this.EndAsync(active).ConfigureAwait(false);
            return "stopped";
        }

        if (AetherEventEmergency.IsDisabled() || this._active is not null)
        {
            return "active_or_disabled";
        }

        var settings = AetherLiveSettings.Read();
        var location = settings.BossLocation ?? [0, 179, 129];
        var reward = settings.BossReward ?? [12, 15];
        var monsterNumber = settings.BossMonster ?? InvasionMonsters.RedDragon;
        if (location is not [>= 0 and <= ushort.MaxValue, >= 0 and <= byte.MaxValue, >= 0 and <= byte.MaxValue]
            || reward is not [12, 15] and not [14, 13] and not [14, 14]
            || await context.GetMapAsync((ushort)location[0]).ConfigureAwait(false) is not { } map
            || !map.Terrain.WalkMap[location[1], location[2]] || map.Terrain.SafezoneMap[location[1], location[2]]
            || monsterNumber is not (InvasionMonsters.GoldenBudgeDragon or InvasionMonsters.RedDragon or InvasionMonsters.GoldenDragon)
            || context.Configuration.Monsters.FirstOrDefault(m => m.Number == monsterNumber) is not { } definition
            || context.Configuration.Items.FirstOrDefault(i => i.Group == reward[0] && i.Number == reward[1]) is not { } item)
        {
            return "invalid_configuration";
        }

        var x = (byte)location[1];
        var y = (byte)location[2];
        var area = new MonsterSpawnArea
        {
            GameMap = map.Definition,
            MonsterDefinition = definition,
            SpawnTrigger = SpawnTrigger.OnceAtEventStart,
            Quantity = 1,
            X1 = x,
            X2 = x,
            Y1 = y,
            Y2 = y,
        };
        var boss = new Monster(area, definition, map, context.DropGenerator, new BasicMonsterIntelligence(),
            context.PlugInManager, context.PathFinderPool);
        boss.Initialize();
        var encounter = new Encounter(context, boss, item, Guid.NewGuid(), DateTime.UtcNow.AddMinutes(15));
        this._active = encounter;
        await map.AddAsync(boss).ConfigureAwait(false);
        boss.OnSpawn();
        await context.ForEachPlayerAsync(async player => await player.ShowLocalizedGoldenMessageAsync("AetherBossStarted",
            definition.Designation.GetTranslation(player.Culture), map.Definition.Name.GetTranslation(player.Culture), x, y).ConfigureAwait(false)).ConfigureAwait(false);
        await PublishAsync(encounter, "boss_started").ConfigureAwait(false);
        return "requested";
    }

    /// <inheritdoc />
    public async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        using var gate = await this._gate.LockAsync().ConfigureAwait(false);
        if (this._active is { } active && active.Context == gameContext
            && (DateTime.UtcNow >= active.Expires || AetherEventEmergency.IsDisabled()))
        {
            await this.EndAsync(active).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void ForceStart()
    {
        // Operator start requires an authenticated request and a validated spawn configuration.
    }

    /// <inheritdoc />
    public async ValueTask AttackableGotKilledAsync(IAttackable killed, IAttacker? killer)
    {
        using var gate = await this._gate.LockAsync().ConfigureAwait(false);
        if (this._active is not { Reward: null } active || !ReferenceEquals(killed, active.Boss)
            || killer is not Player winner || winner is not IHasIpAddress { IpAddress: not null }
            || AetherEventEmergency.IsDisabled() || DateTime.UtcNow >= active.Expires)
        {
            return;
        }

        var map = active.Boss.CurrentMap;
        var drop = new DroppedItem(new TemporaryItem { Definition = active.Item, Durability = 1 },
            active.Boss.Position, map, null, [winner], false, System.Threading.Timeout.InfiniteTimeSpan)
        {
            TreasureOccurrence = active.Id,
            IsRaidReward = true,
        };
        this._active = active with { Reward = drop, Expires = DateTime.UtcNow.AddMinutes(2) };
        await map.AddAsync(drop).ConfigureAwait(false);
        await active.Context.ForEachPlayerAsync(async player => await player.ShowLocalizedGoldenMessageAsync("AetherBossDefeated",
            active.Boss.Definition.Designation.GetTranslation(player.Culture), winner.Name).ConfigureAwait(false)).ConfigureAwait(false);
        await PublishAsync(active, "boss_defeated").ConfigureAwait(false);
    }

    private static async ValueTask PublishAsync(Encounter active, string kind)
    {
        if (active.Context is IGameServerContext server)
        {
            await server.EventPublisher.GameEventAsync(new AetherObservationEvent(server.Id, DateTime.UtcNow,
                kind, active.Id.ToString("N"))).ConfigureAwait(false);
        }
    }

    private async ValueTask EndAsync(Encounter active)
    {
        this._active = null;
        if (active.Reward is { } reward)
        {
            await reward.ExpireTreasureAsync().ConfigureAwait(false);
        }
        else if (active.Boss.IsAlive)
        {
            await active.Boss.CurrentMap.RemoveAsync(active.Boss).ConfigureAwait(false);
        }

        await active.Context.ForEachPlayerAsync(async player =>
            await player.ShowLocalizedGoldenMessageAsync("AetherBossEnded").ConfigureAwait(false)).ConfigureAwait(false);
        await PublishAsync(active, "boss_ended").ConfigureAwait(false);
    }

    private sealed record Encounter(IGameContext Context, Monster Boss, ItemDefinition Item, Guid Id, DateTime Expires, DroppedItem? Reward = null);
}
