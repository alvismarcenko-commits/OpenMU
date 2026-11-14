// <copyright file="CastleTreasuryAdapterTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>
namespace AetherMarketplaceGameServer.Tests;

using System.Threading;
using static AetherMarketplaceGameServer.CastleTreasuryIntegrationAdapter;

[TestFixture]
public sealed class CastleTreasuryAdapterTests
{
    [TestCase(1000L, 0L)]
    [TestCase(101L, 1L)]
    [TestCase(long.MaxValue, 1L)]
    public async Task ApprovedSharesConserveBalanceWithoutOverflowAsync(long balance, long remainder)
    {
        var cycle = Guid.NewGuid();
        var result = new FinalResult("Origin", cycle, "synthetic-final-event", new[] { new Ranking(1, Guid.NewGuid()), new Ranking(2, Guid.NewGuid()), new Ranking(3, Guid.NewGuid()) });
        var adapter = new CastleTreasuryIntegrationAdapter(new Authority(result));
        var proposal = await adapter.PrepareAsync("Origin", cycle, balance).ConfigureAwait(false);
        Assert.That(adapter.ExecutionEnabled, Is.False);
        Assert.That(proposal!.Rollover, Is.EqualTo(remainder));
        Assert.That(proposal.Allocations.Sum(a => a.Amount) + proposal.Rollover, Is.EqualTo(balance));
        var replay = await adapter.PrepareAsync("Origin", cycle, balance).ConfigureAwait(false);
        Assert.That(replay!.Allocations, Is.EqualTo(proposal.Allocations));
        Assert.That(replay.Rollover, Is.EqualTo(proposal.Rollover));
    }

    [Test]
    public async Task MissingSharesRollOverAndUnavailableResultsDoNotAuthorizePayoutsAsync()
    {
        var cycle = Guid.NewGuid();
        var result = new FinalResult("Legacy", cycle, "synthetic-final-event", new[] { new Ranking(1, Guid.NewGuid()) });
        var proposal = await new CastleTreasuryIntegrationAdapter(new Authority(result)).PrepareAsync("Legacy", cycle, 1000).ConfigureAwait(false);
        Assert.That(proposal!.Allocations.Single().Amount, Is.EqualTo(500));
        Assert.That(proposal.Rollover, Is.EqualTo(500));
        Assert.That(await new CastleTreasuryIntegrationAdapter(new Authority(null)).PrepareAsync("Legacy", cycle, 1000).ConfigureAwait(false), Is.Null);
    }

    [TestCase("realm")]
    [TestCase("cycle")]
    [TestCase("guild")]
    [TestCase("duplicate")]
    public void UnverifiedOrCrossRealmResultsAreRejected(string invalid)
    {
        var cycle = Guid.NewGuid();
        var guild = Guid.NewGuid();
        var rankings = invalid == "duplicate" ? new[] { new Ranking(1, guild), new Ranking(2, guild) } : new[] { new Ranking(1, guild) };
        var result = new FinalResult(invalid == "realm" ? "Evolution" : "Ascension", invalid == "cycle" ? Guid.NewGuid() : cycle, "synthetic-final-event", rankings);
        var adapter = new CastleTreasuryIntegrationAdapter(new Authority(result, invalid != "guild"));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await adapter.PrepareAsync("Ascension", cycle, 1000).ConfigureAwait(false));
    }

    private sealed class Authority(FinalResult? result, bool validGuild = true) : IResultAuthority
    {
        public ValueTask<FinalResult?> ReadAsync(string realm, Guid cycle, CancellationToken token) => ValueTask.FromResult(result);

        public ValueTask<bool> VerifyGuildAsync(string realm, Guid guild, CancellationToken token) => ValueTask.FromResult(validGuild);
    }
}
