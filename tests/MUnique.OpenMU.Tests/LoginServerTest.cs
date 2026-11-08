// <copyright file="LoginServerTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading.Tasks;

/// <summary>
/// Tests for the <see cref="LoginServer.LoginServer"/>.
/// </summary>
[TestFixture]
public class LoginServerTest
{
    /// <summary>
    /// Tests that a log off of another server doesn't remove the login of the account.
    /// Previously, any log off removed it, so the account could be logged in a second time.
    /// </summary>
    [Test]
    public async ValueTask LogOffOfAnotherServerKeepsTheLoginAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);

        await loginServer.LogOffAsync("account", 2).ConfigureAwait(false);

        Assert.That(await loginServer.TryLoginAsync("account", 2).ConfigureAwait(false), Is.False);
    }

    /// <summary>
    /// Tests that a log off of the server of the login removes it.
    /// </summary>
    [Test]
    public async ValueTask LogOffOfTheServerRemovesTheLoginAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        Assert.That(await loginServer.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);

        await loginServer.LogOffAsync("account", 1).ConfigureAwait(false);

        Assert.That(await loginServer.TryLoginAsync("account", 2).ConfigureAwait(false), Is.True);
    }
    /// <summary>A crashed caller cannot keep an account reserved indefinitely or release a replacement lease.</summary>
    [Test]
    public async Task ExpiredReservationAllowsLoginAndPreservesNewOwnerAsync()
    {
        var clock = new ReservationClock();
        var server = new LoginServer.LoginServer(clock);
        Assert.That(await server.TryReserveAccountAsync("account", 1000).ConfigureAwait(false), Is.True);
        Assert.That(await server.TryLoginAsync("account", 1).ConfigureAwait(false), Is.False);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.That(await server.TryReserveAccountAsync("account", 1001).ConfigureAwait(false), Is.True);
        await server.ReleaseAccountReservationAsync("account", 1000).ConfigureAwait(false);
        Assert.That(await server.TryLoginAsync("account", 1).ConfigureAwait(false), Is.False);
        clock.Now = clock.Now.AddMinutes(6);
        Assert.That(await server.TryLoginAsync("account", 1).ConfigureAwait(false), Is.True);
        clock.Now = clock.Now.AddMinutes(10);
        Assert.That(await server.TryLoginAsync("account", 2).ConfigureAwait(false), Is.False);
    }

    /// <summary>Parallel registrations share two slots, and the rightful logoff frees exactly one.</summary>
    [Test]
    [NonParallelizable]
    public async Task AscensionAllowsTwoConcurrentAccountsPerAddressAsync()
    {
        const string variable = "AETHER_LOCAL_STAGING";
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "true");
            var server = new LoginServer.LoginServer();
            var results = await Task.WhenAll(Enumerable.Range(0, 10)
                .Select(async i => (Account: $"account{i}", Accepted: await server.TryLoginFromAddressAsync($"account{i}", 1, "127.0.0.1").ConfigureAwait(false)))).ConfigureAwait(false);
            Assert.That(results.Count(result => result.Accepted), Is.EqualTo(2));
            Assert.That(server.ActiveAccountCount, Is.EqualTo(2));

            var active = results.First(result => result.Accepted).Account;
            await server.LogOffAsync(active, 2).ConfigureAwait(false);
            Assert.That(await server.TryLoginFromAddressAsync("retry", 1, "127.0.0.1").ConfigureAwait(false), Is.False);
            await server.LogOffAsync(active, 1).ConfigureAwait(false);
            Assert.That(await server.TryLoginFromAddressAsync("retry", 1, "127.0.0.1").ConfigureAwait(false), Is.True);
            Assert.That(await server.TryLoginFromAddressAsync("other", 1, "127.0.0.2").ConfigureAwait(false), Is.True);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private sealed class ReservationClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => this.Now;
    }
}
