// <copyright file="AetherTreasureHuntPlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Pathfinding;
using MUnique.OpenMU.PlugIns;
using Nito.AsyncEx;

/// <summary>Runs a single shared, native ground drop in the isolated Ascension world.</summary>
[PlugIn]
[Guid("95101688-2750-4dee-a438-444c480ad577")]
public sealed class AetherTreasureHuntPlugIn : PeriodicTaskBasePlugIn<PeriodicTaskConfiguration, PeriodicTaskGameServerState>, ISupportDefaultCustomConfiguration
{
    private readonly AsyncLock _gate = new();
    private DroppedItem? _treasure;
    private bool _pending;
    private bool _manual;
    private AetherLiveSettings? _settings;
    private IList<TimeOnly>? _originalTimetable;

    /// <inheritdoc />
    public object CreateDefaultConfig()
    {
        return new PeriodicTaskConfiguration
        {
            TaskDuration = TimeSpan.FromMinutes(10),
            Timetable = [new TimeOnly(18, 30)],
        };
    }

    /// <summary>Requests a start or stops a run through the staging console.</summary>
    /// <param name="context">The game context.</param>
    /// <param name="start">Whether to start the event.</param>
    /// <returns>The operator result code.</returns>
    public async ValueTask<string> OperateAsync(IGameContext context, bool start)
    {
        using var gate = await this._gate.LockAsync().ConfigureAwait(false);
        var state = this.GetStateByGameContext(context);
        if (start)
        {
            if (this._pending || state.State != PeriodicTaskState.NotStarted || !this.IsEnabledOnContext(context))
            {
                return "active_or_disabled";
            }

            this._pending = true;
            return "requested";
        }

        if (state.State == PeriodicTaskState.NotStarted)
        {
            if (!this._pending)
            {
                return "inactive";
            }

            this._pending = false;
            return "stopped";
        }

        var started = state.State == PeriodicTaskState.Started;
        state.State = PeriodicTaskState.NotStarted;
        state.LastRunUtc = DateTime.MinValue;
        state.NextRunUtc = DateTime.UtcNow;
        if (started)
        {
            await this.OnFinishedAsync(state).ConfigureAwait(false);
        }

        return "stopped";
    }

    /// <summary>Shows the current hunt to players who missed its announcement.</summary>
    /// <param name="player">The requesting player.</param>
    /// <returns>The message task.</returns>
    public async ValueTask ShowStatusAsync(Player player)
    {
        string? mapName = null;
        bool claimed;
        using (await this._gate.LockAsync().ConfigureAwait(false))
        {
            var treasure = this._treasure;
            claimed = treasure is not null && treasure.CurrentMap.GetDrop(treasure.Id) != treasure;
            if (treasure is not null && !claimed)
            {
                mapName = treasure.CurrentMap.Definition.Name.GetTranslation(player.Culture);
            }
        }

        if (mapName is not null)
        {
            await player.ShowLocalizedBlueMessageAsync("AetherTreasureHint", mapName).ConfigureAwait(false);
        }
        else
        {
            await player.ShowLocalizedBlueMessageAsync(claimed ? "AetherTreasureClaimed" : "AetherTreasureInactive").ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true")
        {
            return;
        }

        using var gate = await this._gate.LockAsync().ConfigureAwait(false);
        try
        {
            this.Configuration ??= (PeriodicTaskConfiguration)this.CreateDefaultConfig();
            var settings = AetherLiveSettings.Read();
            if (!ReferenceEquals(this._settings, settings))
            {
                this._originalTimetable ??= this.Configuration.Timetable;
                this.Configuration.Timetable = AetherLiveSettings.ParseSchedule(settings.TreasureTimes) ?? this._originalTimetable;
                this._settings = settings;
            }

            if (this._pending && !AetherEventEmergency.IsDisabled())
            {
                this.ForceStart();
            }

            await base.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        }
        finally
        {
            this._pending = false;
        }
    }

    /// <inheritdoc />
    protected override PeriodicTaskGameServerState CreateState(IGameContext gameContext) => new(gameContext);

    /// <inheritdoc />
    protected override bool IsItTimeToStart(IGameContext gameContext)
        => !AetherEventEmergency.IsDisabled() && base.IsItTimeToStart(gameContext);

    /// <inheritdoc />
    protected override ValueTask OnPrepareEventAsync(PeriodicTaskGameServerState state)
    {
        this._manual = this._pending;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnPreparedAsync(PeriodicTaskGameServerState state) => ValueTask.CompletedTask;

    /// <inheritdoc />
    protected override async ValueTask OnStartedAsync(PeriodicTaskGameServerState state)
    {
        if (AetherEventEmergency.IsDisabled())
        {
            state.NextRunUtc = DateTime.UtcNow;
            return;
        }

        var location = AetherLiveSettings.Read().TreasureLocation ?? [0, 149, 123];
        if (location is not [>= 0 and <= ushort.MaxValue, >= 0 and <= byte.MaxValue, >= 0 and <= byte.MaxValue]
            || await state.Context.GetMapAsync((ushort)location[0]).ConfigureAwait(false) is not { } map
            || !map.Terrain.WalkMap[location[1], location[2]]
            || state.Context.Configuration.Items.FirstOrDefault(item => item.Group == 12 && item.Number == 15) is not { } jewel)
        {
            state.Context.LoggerFactory.CreateLogger(this.GetType()).LogWarning("Treasure Hunt refused an invalid map, coordinate or reward definition.");
            state.NextRunUtc = DateTime.UtcNow;
            return;
        }

        if (!this._manual && !this.TryReserveScheduledRun(state))
        {
            state.NextRunUtc = DateTime.UtcNow;
            return;
        }

        var occurrence = Guid.NewGuid();
        var point = new Point((byte)location[1], (byte)location[2]);
        var treasure = new DroppedItem(new TemporaryItem { Definition = jewel, Durability = 1 }, point,
            map, null, null, false, System.Threading.Timeout.InfiniteTimeSpan) { TreasureOccurrence = occurrence };
        this._treasure = treasure;
        await map.AddAsync(treasure).ConfigureAwait(false);
        await state.Context.ForEachPlayerAsync(async player =>
            await player.ShowLocalizedGoldenMessageAsync("AetherTreasureStarted", map.Definition.Name.GetTranslation(player.Culture)).ConfigureAwait(false)).ConfigureAwait(false);
        if (state.Context is IGameServerContext server)
        {
            await server.EventPublisher.GameEventAsync(new AetherObservationEvent(server.Id, DateTime.UtcNow,
                "treasure_started", occurrence.ToString("N"))).ConfigureAwait(false);
        }
    }

    private bool TryReserveScheduledRun(PeriodicTaskGameServerState state)
    {
        if (Environment.GetEnvironmentVariable("AETHER_GUARDIAN_HOME") is not { } home)
        {
            return false;
        }

        var preparedAt = state.LastRunUtc - (this.Configuration?.PreStartMessageDelay ?? TimeSpan.Zero);
        var slot = preparedAt.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        try
        {
            var directory = Path.Combine(home, "treasure-slots");
            Directory.CreateDirectory(directory);
            using var marker = new FileStream(Path.Combine(directory, slot), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            state.Context.LoggerFactory.CreateLogger(this.GetType()).LogWarning("Treasure Hunt slot was already reserved or could not be written.");
            return false;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnFinishedAsync(PeriodicTaskGameServerState state)
    {
        if (this._treasure is not { TreasureOccurrence: { } occurrence } treasure)
        {
            return;
        }

        this._treasure = null;
        await treasure.ExpireTreasureAsync().ConfigureAwait(false);
        await state.Context.ForEachPlayerAsync(async player =>
            await player.ShowLocalizedGoldenMessageAsync("AetherTreasureEnded").ConfigureAwait(false)).ConfigureAwait(false);
        if (state.Context is IGameServerContext server)
        {
            await server.EventPublisher.GameEventAsync(new AetherObservationEvent(server.Id, DateTime.UtcNow,
                "treasure_ended", occurrence.ToString("N"))).ConfigureAwait(false);
        }
    }
}
