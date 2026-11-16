// <copyright file="GoldenInvasionPlugIn.Operator.cs" company="AetherMU">
// Licensed under the MIT License.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlugIns.InvasionEvents;

using MUnique.OpenMU.GameLogic.PlugIns.PeriodicTasks;
using Nito.AsyncEx;

public sealed partial class GoldenInvasionPlugIn
{
    private readonly AsyncLock _operatorGate = new();
    private bool _operatorPending;
    private DateTime? _announcedSlot;
    private readonly HashSet<int> _announcedMinutes = [];
    private AetherLiveSettings? _settings;
    private IList<TimeOnly>? _originalTimetable;

    /// <summary>Requests one native scheduled run without interrupting a prepared or active invasion.</summary>
    /// <param name="context">The context which owns this invasion.</param>
    /// <returns>Whether the native scheduler accepted a pending start.</returns>
    public async ValueTask<bool> RequestOperatorStartAsync(IGameContext context)
    {
        using var gate = await this._operatorGate.LockAsync().ConfigureAwait(false);
        if (this._operatorPending || !this.IsEnabledOnContext(context)
            || this.GetStateByGameContext(context).State != PeriodicTaskState.NotStarted)
        {
            return false;
        }

        this._operatorPending = true;
        return true;
    }

    /// <summary>Stops an active or prepared invasion through the staging console.</summary>
    /// <param name="context">The game context that owns the invasion.</param>
    /// <returns>The operator result code.</returns>
    public async ValueTask<string> RequestOperatorStopAsync(IGameContext context)
    {
        using var gate = await this._operatorGate.LockAsync().ConfigureAwait(false);
        var state = this.GetStateByGameContext(context);
        if (state.State == PeriodicTaskState.NotStarted)
        {
            if (!this._operatorPending)
            {
                return "inactive";
            }

            this._operatorPending = false;
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
        else
        {
            state.Reset();
        }
        return "stopped";
    }

    /// <inheritdoc />
    protected override bool IsItTimeToStart(IGameContext gameContext)
        => !AetherEventEmergency.IsDisabled() && base.IsItTimeToStart(gameContext);

    /// <inheritdoc />
    public override async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        using var gate = await this._operatorGate.LockAsync().ConfigureAwait(false);
        try
        {
            if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true")
            {
                this.Configuration ??= InvasionConfigurationDefaults.Golden;
                var settings = AetherLiveSettings.Read();
                if (!ReferenceEquals(this._settings, settings))
                {
                    this._originalTimetable ??= this.Configuration.Timetable;
                    this.Configuration.Timetable = AetherLiveSettings.ParseSchedule(settings.GoldenTimes) ?? this._originalTimetable;
                    this._settings = settings;
                }

                if (this.GetStateByGameContext(gameContext).State == PeriodicTaskState.NotStarted
                    && this.IsEnabledOnContext(gameContext) && !AetherEventEmergency.IsDisabled())
                {
                    await this.AnnounceUpcomingAsync(gameContext).ConfigureAwait(false);
                }
            }

            if (this._operatorPending && !AetherEventEmergency.IsDisabled())
            {
                this.ForceStart();
            }

            await base.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        }
        finally
        {
            this._operatorPending = false;
        }
    }

    /// <inheritdoc />
    protected override bool AnnounceWhenPrepared => Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") != "true";

    /// <inheritdoc />
    protected override async ValueTask OnStartedAsync(InvasionGameServerState state)
    {
        await base.OnStartedAsync(state).ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("AETHER_LOCAL_STAGING") == "true")
        {
            await state.Context.ForEachPlayerAsync(player => this.TrySendStartMessageAsync(player, state)).ConfigureAwait(false);
        }
    }

    private async ValueTask AnnounceUpcomingAsync(IGameContext context)
    {
        var now = DateTime.UtcNow;
        var slot = this.GetNextStartUtc(now, context.ServerTimeZone);
        if (slot != this._announcedSlot)
        {
            this._announcedSlot = slot;
            this._announcedMinutes.Clear();
        }

        if (slot is not { } startsAt)
        {
            return;
        }

        var remaining = (startsAt - now).TotalSeconds;
        foreach (var minutes in new[] { 15, 5, 1 })
        {
            if (remaining <= minutes * 60 && remaining > (minutes * 60) - 5 && this._announcedMinutes.Add(minutes))
            {
                await context.ForEachPlayerAsync(async player =>
                    await player.ShowLocalizedGoldenMessageAsync("AetherGoldenCountdown", minutes).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
    }
}
