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
        this.ForceStart();
        return true;
    }

    /// <inheritdoc />
    public override async ValueTask ExecuteTaskAsync(GameContext gameContext)
    {
        using var gate = await this._operatorGate.LockAsync().ConfigureAwait(false);
        try
        {
            await base.ExecuteTaskAsync(gameContext).ConfigureAwait(false);
        }
        finally
        {
            this._operatorPending = false;
        }
    }
}
