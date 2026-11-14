// <copyright file="CastleTreasuryIntegrationAdapter.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer;

using System.Threading;

/// <summary>Validates an authoritative final result and prepares the approved guild allocation.</summary>
/// <remarks>The current native siege state persists ownership and registration scores, not an
/// immutable ranked final event. Consequently this adapter has no payout executor or wallet grants.
/// An operator must supply a verified final-result authority and approve guild custody before activation.</remarks>
public sealed class CastleTreasuryIntegrationAdapter
{
    private readonly IResultAuthority _authority;

    /// <summary>Initializes a new instance of the <see cref="CastleTreasuryIntegrationAdapter"/> class.</summary>
    /// <param name="authority">The trusted native final-event and guild authority.</param>
    public CastleTreasuryIntegrationAdapter(IResultAuthority authority)
    {
        this._authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    /// <summary>The contract for a trusted server-side final result source, never browser input.</summary>
    public interface IResultAuthority
    {
        /// <summary>Reads an immutable, authenticated final result; returns null when unavailable.</summary>
        /// <param name="realm">The explicitly routed realm.</param>
        /// <param name="cycle">The durable siege cycle.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>The verified final event, if available.</returns>
        ValueTask<FinalResult?> ReadAsync(string realm, Guid cycle, CancellationToken token);

        /// <summary>Verifies a persistent guild against that realm's native guild authority.</summary>
        /// <param name="realm">The result realm.</param>
        /// <param name="guild">The persistent guild UUID.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>Whether the guild is authentic and belongs to the realm.</returns>
        ValueTask<bool> VerifyGuildAsync(string realm, Guid guild, CancellationToken token);
    }

    /// <summary>Gets a value indicating whether payouts can execute. Final-event and guild custody integration is pending.</summary>
    public bool ExecutionEnabled => false;

    /// <summary>Prepares an allocation without modifying wallets or treasury journals.</summary>
    /// <param name="realm">The configured realm.</param>
    /// <param name="cycle">The immutable cycle key.</param>
    /// <param name="available">The committed treasury balance at the intended payout boundary.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>A validated proposal, or null if no authoritative result exists.</returns>
    public async ValueTask<Proposal?> PrepareAsync(string realm, Guid cycle, long available, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        if (cycle == Guid.Empty || available < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(available));
        }

        var result = await this._authority.ReadAsync(realm, cycle, token).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        if (result.Realm != realm || result.Cycle != cycle || string.IsNullOrWhiteSpace(result.Evidence)
            || result.Rankings.Count > 3 || result.Rankings.Any(r => r.Place is < 1 or > 3 || r.Guild == Guid.Empty)
            || result.Rankings.Select(r => r.Place).Distinct().Count() != result.Rankings.Count
            || result.Rankings.Select(r => r.Guild).Distinct().Count() != result.Rankings.Count)
        {
            throw new InvalidOperationException("Invalid or cross-realm final result.");
        }

        var allocations = new List<Allocation>();
        foreach (var ranking in result.Rankings.OrderBy(r => r.Place))
        {
            if (!await this._authority.VerifyGuildAsync(realm, ranking.Guild, token).ConfigureAwait(false))
            {
                throw new InvalidOperationException("Unverified native guild.");
            }

            var percentage = ranking.Place switch { 1 => 50, 2 => 30, _ => 20 };
            var amount = checked(((available / 100) * percentage) + (((available % 100) * percentage) / 100));
            allocations.Add(new Allocation(ranking.Place, ranking.Guild, amount));
        }

        return new Proposal(realm, cycle, result.Evidence, allocations.AsReadOnly(), checked(available - allocations.Sum(a => a.Amount)));
    }

    /// <summary>An authenticated final placement, distinct from a siege registration score.</summary>
    /// <param name="Place">The final place, from one to three.</param>
    /// <param name="Guild">The persistent native guild UUID.</param>
    public sealed record Ranking(int Place, Guid Guild);

    /// <summary>An immutable result supplied by the trusted server authority.</summary>
    /// <param name="Realm">The authoritative realm key.</param>
    /// <param name="Cycle">The durable cycle identity.</param>
    /// <param name="Evidence">The source event identity or authenticated digest.</param>
    /// <param name="Rankings">Verified final placements.</param>
    public sealed record FinalResult(string Realm, Guid Cycle, string Evidence, IReadOnlyList<Ranking> Rankings);

    /// <summary>A proposed guild allocation; it does not select an individual player's wallet.</summary>
    /// <param name="Place">The final place.</param>
    /// <param name="Guild">The validated guild UUID.</param>
    /// <param name="Amount">The guild allocation in integer AC units.</param>
    public sealed record Allocation(int Place, Guid Guild, long Amount);

    /// <summary>A proposal whose missing shares and rounding remainder stay in treasury.</summary>
    /// <param name="Realm">The isolated realm.</param>
    /// <param name="Cycle">The durable event identity required for eventual payout idempotency.</param>
    /// <param name="Evidence">The verified source event evidence.</param>
    /// <param name="Allocations">The approved 50/30/20 proposals for available placements.</param>
    /// <param name="Rollover">The amount retained for the next event.</param>
    public sealed record Proposal(string Realm, Guid Cycle, string Evidence, IReadOnlyList<Allocation> Allocations, long Rollover);
}
