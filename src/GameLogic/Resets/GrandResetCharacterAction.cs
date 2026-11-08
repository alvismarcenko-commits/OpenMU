// <copyright file="GrandResetCharacterAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.Resets;

using System.Text.Json;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.Views.Character;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Exchanges the configured realm reset threshold for 100 APP with no extra stat points.
/// </summary>
public class GrandResetCharacterAction
{
    private readonly Player _player;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrandResetCharacterAction"/> class.
    /// </summary>
    /// <param name="player">The player.</param>
    public GrandResetCharacterAction(Player player) => this._player = player;

    /// <summary>
    /// Checks eligibility. The check is repeated inside the persistence lock before mutation.
    /// </summary>
    /// <returns>Whether the feature is enabled and the character is eligible.</returns>
    public bool CanGrandReset()
    {
        var configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()?.Configuration;
        return configuration is { PrestigeEnabled: true, GrandResetEnabled: true, ResetStats: true, ReplacePointsPerReset: true, LevelAfterGrandReset: 1 }
            && configuration.PrestigeResetsRequired > 0
            && new[] { "origin", "legacy", "evolution", "ascension" }.Contains(configuration.PrestigeRealm)
            && this._player.PlayerState.CurrentState == PlayerState.EnteredWorld
            && this._player.SelectedCharacter?.CharacterClass is not null
            && this._player.OpenedNpc is null && this._player.TradingPartner is null && this._player.TemporaryStorage is not { ItemStorage.Items.Count: > 0 }
            && this._player.Attributes is { } attributes
            && float.IsFinite(attributes[Stats.Resets])
            && attributes[Stats.Resets] == MathF.Truncate(attributes[Stats.Resets])
            && attributes[Stats.Resets] >= configuration.PrestigeResetsRequired
            && attributes[Stats.Resets] < int.MaxValue
            // Legacy stat-bonus characters require explicit migration review, never silent conversion.
            && attributes[Stats.GrandResets] == 0
            && this._player.PersistenceContext is IPrestigeCharacterContext;
    }

    /// <summary>
    /// Performs and saves the exchange before acknowledging it to the client.
    /// </summary>
    /// <returns>A task which completes when the operation finishes.</returns>
    public ValueTask GrandResetCharacterAsync()
        => this._player.RunPersistenceExclusiveAsync(this.GrandResetExclusiveAsync);

    private async ValueTask GrandResetExclusiveAsync()
    {
        if (!this.CanGrandReset())
        {
            await this._player.ShowLocalizedBlueMessageAsync("GrandResetUnavailable").ConfigureAwait(false);
            return;
        }

        var configuration = this._player.GameContext.FeaturePlugIns.GetPlugIn<ResetFeaturePlugIn>()!.Configuration!;
        // All four realms use the same native + APP transaction. No legacy stat-bonus fallback.
        await this.PrestigeExchangeAsync(configuration).ConfigureAwait(false);
    }

    private async ValueTask PrestigeExchangeAsync(ResetConfiguration configuration)
    {
        var character = this._player.SelectedCharacter!;
        var attributes = this._player.Attributes!;
        var previous = character.CharacterClass!.StatAttributes.Where(s => s.IncreasableByPlayer && s.Attribute is not null)
            .Select(s => s.Attribute!).Concat(new[] { Stats.Level, Stats.Resets }).Distinct().ToDictionary(a => a, a => attributes[a]);
        var oldExperience = character.Experience;
        var oldPoints = character.LevelUpPoints;
        var resetsBefore = checked((int)attributes[Stats.Resets]);
        string receipt;
        try
        {
            foreach (var stat in character.CharacterClass.StatAttributes.Where(s => s.IncreasableByPlayer && s.Attribute is not null))
            {
                attributes[stat.Attribute!] = stat.BaseValue;
            }

            attributes[Stats.Level] = 1;
            attributes[Stats.Resets] = 0;
            // Prestige counters live in NUMERIC, never in the legacy float attribute.
            character.Experience = 0;
            character.LevelUpPoints = 0;
            receipt = await ((IPrestigeCharacterContext)this._player.PersistenceContext).ExecutePrestigeExchangeAsync(
                character, configuration.PrestigeRealm, Guid.NewGuid().ToString(), resetsBefore).ConfigureAwait(false);
        }
        catch
        {
            foreach (var (definition, value) in previous) { attributes[definition] = value; }
            character.Experience = oldExperience;
            character.LevelUpPoints = oldPoints;
            // The invalidated persistence context refuses every later save, including disconnect.
            await this._player.DisconnectAsync().ConfigureAwait(false);
            throw;
        }

        await this._player.ResetComboProgressAsync().ConfigureAwait(false);
        await this._player.InvokeViewPlugInAsync<IUpdateCharacterBaseStatsPlugIn>(p => p.UpdateCharacterBaseStatsAsync()).ConfigureAwait(false);
        await this._player.InvokeViewPlugInAsync<IUpdateLevelPlugIn>(p => p.UpdateLevelAsync()).ConfigureAwait(false);
        // Read only after commit, outside the rollback path. Keep numeric counters as strings.
        using var result = JsonDocument.Parse(receipt);
        var balance = result.RootElement.GetProperty("balance_app").GetString();
        await this._player.ShowLocalizedBlueMessageAsync("PrestigeGrandResetCompleted").ConfigureAwait(false);
        await this._player.ShowLocalizedBlueMessageAsync("PrestigeGrandResetBalance", balance).ConfigureAwait(false);
    }
}

