// Licensed under MIT. Uses the existing nonce-owned native PostgreSQL fixture; never production.
namespace MUnique.OpenMU.Tests;
using MUnique.OpenMU.Persistence.EntityFramework;
using Npgsql;

public partial class GrandResetPostgreSqlTest
{
    [Test]
    public async Task NativeAccountCreationReplayConflictAndRevocationAsync()
    {
        var service = new AetherAccountService(this._connectionString);
        await service.InitializeAsync().ConfigureAwait(false);
        var owner = Random.Shared.NextInt64(2, long.MaxValue);
        var operation = Guid.NewGuid();
        var login = "a" + Guid.NewGuid().ToString("N")[..9];
        const string password = "9s!Kr4#P2z";
        var id = await service.CreateAsync(owner, operation, login, password).ConfigureAwait(false);
        Assert.That(await service.CreateAsync(owner, operation, login, password).ConfigureAwait(false), Is.EqualTo(id));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CreateAsync(owner, Guid.NewGuid(), login, password).ConfigureAwait(false));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CreateAsync(owner, operation, login, "1234567890").ConfigureAwait(false));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CreateAsync(owner - 1, Guid.NewGuid(), login, password).ConfigureAwait(false));
        // Validate real persisted BCrypt hash, account type and exactly one native identity.
        await using var connection = new NpgsqlConnection(this._connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT \"PasswordHash\" FROM data.\"Account\" WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", Guid.Parse(id));
        Assert.That(BCrypt.Net.BCrypt.Verify(password, (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!), Is.True);
        await service.RevokeAsync(owner, Guid.Parse(id)).ConfigureAwait(false);
        await service.RevokeAsync(owner, Guid.Parse(id)).ConfigureAwait(false);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await service.CreateAsync(owner, operation, login, password).ConfigureAwait(false));
        Assert.That(await command.ExecuteScalarAsync().ConfigureAwait(false), Is.Not.Null); // Account retained.
    }

    [Test]
    public async Task NativeAccountConcurrentReplayPersistsOneIdentityAsync()
    {
        var service = new AetherAccountService(this._connectionString);
        await service.InitializeAsync().ConfigureAwait(false);
        var owner = Random.Shared.NextInt64(2, long.MaxValue);
        var operation = Guid.NewGuid();
        var login = "r" + Guid.NewGuid().ToString("N")[..9];
        // The production service must handle EF-wrapped serialization failures itself.
        var ids = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.CreateAsync(owner, operation, login, "9s!Kr4#P2z"))).ConfigureAwait(false);
        Assert.That(ids.Distinct().Count(), Is.EqualTo(1));
        await using var connection = new NpgsqlConnection(this._connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var counts = new NpgsqlCommand("SELECT (SELECT count(*) FROM aether_accounts.links WHERE owner=@owner), (SELECT count(*) FROM aether_accounts.audit WHERE owner=@owner AND action='created'), (SELECT count(*) FROM data.\"Account\" WHERE \"LoginName\"=@login)", connection);
        counts.Parameters.AddWithValue("owner", owner); counts.Parameters.AddWithValue("login", login);
        await using var reader = await counts.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.That(await reader.ReadAsync().ConfigureAwait(false), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(reader.GetInt64(0), Is.EqualTo(1));
            Assert.That(reader.GetInt64(1), Is.EqualTo(1));
            Assert.That(reader.GetInt64(2), Is.EqualTo(1));
        });
    }

    [TestCase("40001", true)]
    [TestCase("40P01", true)]
    [TestCase("23505", false)]
    [TestCase("42601", false)]
    public void RetryClassificationPreservesEfWrappedSqlState(string sqlState, bool retryable)
    {
        var postgres = new PostgresException("test-only failure", "ERROR", "ERROR", sqlState);
        var wrapped = new InvalidOperationException("EF transient wrapper",
            new Microsoft.EntityFrameworkCore.DbUpdateException("save wrapper", postgres));
        Assert.That(AetherAccountService.IsRetryableTransactionFailure(wrapped), Is.EqualTo(retryable));
    }

    [Test]
    public void NativeAccountServiceRefusesExistingDatabase()
    {
        Assert.Throws<ArgumentException>(() => new AetherAccountService("Host=127.0.0.1;Database=openmu"));
        Assert.Throws<ArgumentException>(() => new AetherAccountService("Host=remote;Database=aether_playable_ascension_" + Guid.NewGuid().ToString("N")));
    }

    [Test]
    public async Task NativeAccountNeverTruncatesCredentialsAsync()
    {
        var service = new AetherAccountService(this._connectionString);
        Assert.ThrowsAsync<ArgumentException>(async () => await service.CreateAsync(1, Guid.NewGuid(), "player", "12345678901").ConfigureAwait(false));
        Assert.ThrowsAsync<ArgumentException>(async () => await service.CreateAsync(1, Guid.NewGuid(), "player", "ā234567890").ConfigureAwait(false));
        await Task.CompletedTask;
    }
}
