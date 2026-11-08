// Licensed under the MIT License. Isolated staging only; existing BCrypt authentication is unchanged.
namespace MUnique.OpenMU.Persistence.EntityFramework;

using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MUnique.OpenMU.Persistence.EntityFramework.Model;
using Npgsql;

/// <summary>Atomic native account creation and immutable Website ownership; no APP grants.</summary>
public sealed class AetherAccountService
{
    private readonly string connectionString;
    public AetherAccountService(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.Host != "127.0.0.1" || !Regex.IsMatch(b.Database ?? "", "^aether_(playable_ascension|gr_test)_[a-f0-9]{32}$"))
        { throw new ArgumentException("Only nonce-owned loopback staging databases are allowed."); }
        this.connectionString = connectionString;
    }

    /// <summary>Creates the bridge schema only. Caller must own a freshly initialized staging DB.</summary>
    public async Task InitializeAsync()
    {
        await using var db = new BridgeContext(this.connectionString);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS aether_accounts;
            CREATE UNIQUE INDEX IF NOT EXISTS aether_native_login_folded ON data."Account"(lower("LoginName"));
            CREATE TABLE IF NOT EXISTS aether_accounts.links (
              owner bigint PRIMARY KEY CHECK(owner>0), realm text NOT NULL CHECK(realm='ascension'),
              account_id uuid NOT NULL UNIQUE REFERENCES data."Account"("Id"),
              login text NOT NULL UNIQUE, operation uuid NOT NULL UNIQUE, revoked boolean NOT NULL DEFAULT false);
            CREATE TABLE IF NOT EXISTS aether_accounts.audit (
              id uuid PRIMARY KEY, owner bigint NOT NULL, action text NOT NULL,
              account_id uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            """).ConfigureAwait(false);
    }

    /// <summary>Replays only the same owner, operation and full credentials; never claims an existing account.</summary>
    public async Task<string> CreateAsync(long owner, Guid operation, string login, string password)
    {
        if (owner <= 0 || operation == Guid.Empty || !Regex.IsMatch(login, "^[a-z0-9_]{3,10}$")
            || password.Length != 10 || password.Any(c => c < '!' || c > '~'))
        { throw new ArgumentException("Separate game login: 3-10 lowercase ASCII; password: exactly 10 printable non-space ASCII characters. Never truncate."); }
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await this.CreateOnceAsync(owner, operation, login, password).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < 7 && IsRetryableTransactionFailure(exception))
            {
                // EF can wrap PostgresException in DbUpdateException and InvalidOperationException.
                // Retry the WHOLE operation in a new context/transaction, never the failed SaveChanges.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1))).ConfigureAwait(false);
            }
        }
    }

    internal static bool IsRetryableTransactionFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState is "40001" or "40P01")
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> CreateOnceAsync(long owner, Guid operation, string login, string password)
    {
        await using var db = new BridgeContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        // Same owner lock serializes create/revoke, including when no row exists yet.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({owner})").ConfigureAwait(false);
        await using var read = db.Database.GetDbConnection().CreateCommand();
        read.Transaction = tx.GetDbTransaction();
        read.CommandText = "SELECT account_id,login,operation,revoked FROM aether_accounts.links WHERE owner=@owner";
        var op = read.CreateParameter(); op.ParameterName = "owner"; op.Value = owner; read.Parameters.Add(op);
        Guid? existing = null; string existingLogin = ""; Guid existingOperation = Guid.Empty; bool revoked = false;
        await using (var reader = await read.ExecuteReaderAsync().ConfigureAwait(false))
        {
            if (await reader.ReadAsync().ConfigureAwait(false))
            { existing = reader.GetGuid(0); existingLogin = reader.GetString(1); existingOperation = reader.GetGuid(2); revoked = reader.GetBoolean(3); }
        }
        if (existing is { } id)
        {
            var account = await db.Set<Account>().SingleAsync(a => a.Id == id).ConfigureAwait(false);
            if (revoked || existingLogin != login || existingOperation != operation || !BCrypt.Net.BCrypt.Verify(password, account.PasswordHash))
            { throw new InvalidOperationException("Account identity conflict or revoked link."); }
            await tx.CommitAsync().ConfigureAwait(false);
            return id.ToString();
        }
        if (await db.Set<Account>().AnyAsync(a => a.LoginName.ToLower() == login).ConfigureAwait(false))
        { throw new InvalidOperationException("Login is already registered; existing accounts cannot be claimed."); }
        var created = new Account { Id = Guid.NewGuid(), LoginName = login,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password), State = DataModel.Entities.AccountState.Normal,
            RegistrationDate = DateTime.UtcNow, SecurityCode = string.Empty, EMail = string.Empty };
        db.Add(created);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO aether_accounts.links(owner,realm,account_id,login,operation) VALUES({owner},'ascension',{created.Id},{login},{operation})").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO aether_accounts.audit(id,owner,action,account_id) VALUES({Guid.NewGuid()},{owner},'created',{created.Id})").ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return created.Id.ToString();
    }

    /// <summary>Revokes Website ownership capabilities permanently, preserving account/gameplay and all player data.</summary>
    public async Task RevokeAsync(long owner, Guid accountId)
    {
        if (owner <= 0 || accountId == Guid.Empty) { throw new ArgumentException("Invalid identity."); }
        await using var db = new BridgeContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({owner})").ConfigureAwait(false);
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE aether_accounts.links SET revoked=true WHERE owner={owner} AND account_id={accountId} AND NOT revoked").ConfigureAwait(false);
        if (changed == 1)
        { await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO aether_accounts.audit(id,owner,action,account_id) VALUES({Guid.NewGuid()},{owner},'revoked',{accountId})").ConfigureAwait(false); }
        await tx.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>Reads the persisted player data only for the linked Website owner.</summary>
    public async Task<object?> ProfileAsync(long owner, Guid accountId, Guid level, Guid resets, Guid grandResets)
    {
        await using var db = new ProfileContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead).ConfigureAwait(false);
        if (!await HasLinkAsync(db, owner, accountId).ConfigureAwait(false))
        {
            return null;
        }

        var account = await db.Set<Account>().AsNoTracking()
            .Include(a => a.RawCharacters).ThenInclude(c => c.RawCharacterClass)
            .Include(a => a.RawCharacters).ThenInclude(c => c.RawAttributes)
            .AsSplitQuery().SingleAsync(a => a.Id == accountId).ConfigureAwait(false);
        return new
        {
            login = account.LoginName,
            status = account.State == DataModel.Entities.AccountState.Normal ? "active" : "restricted",
            characters = account.RawCharacters.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new
            {
                name = c.Name,
                character_class = c.RawCharacterClass?.Name.ToString(),
                level = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == level)?.Value,
                resets = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == resets)?.Value ?? 0,
                grand_resets = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == grandResets)?.Value ?? 0,
                experience = c.Experience,
            }).ToArray(),
        };
    }

    /// <summary>Replaces credentials of an active linked account and records the change atomically.</summary>
    public async Task<bool> ChangePasswordAsync(long owner, Guid accountId, string password)
    {
        if (password.Length != 10 || password.Any(c => c < '!' || c > '~'))
        {
            throw new ArgumentException("Game password must be exactly ten printable non-space ASCII characters.");
        }

        await using var db = new BridgeContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({owner})").ConfigureAwait(false);
        if (!await HasLinkAsync(db, owner, accountId).ConfigureAwait(false))
        {
            return false;
        }

        var account = await db.Set<Account>().SingleAsync(a => a.Id == accountId).ConfigureAwait(false);
        if (account.State != DataModel.Entities.AccountState.Normal)
        {
            return false;
        }

        account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO aether_accounts.audit(id,owner,action,account_id) VALUES({Guid.NewGuid()},{owner},'password_changed',{accountId})").ConfigureAwait(false);
        await tx.CommitAsync().ConfigureAwait(false);
        return true;
    }

    private static Task<bool> HasLinkAsync(DbContext db, long owner, Guid accountId) => db.Database
        .SqlQuery<Guid>($"SELECT account_id AS \"Value\" FROM aether_accounts.links WHERE owner={owner} AND account_id={accountId} AND NOT revoked")
        .AnyAsync();

    private sealed class BridgeContext(string connectionString) : AccountContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder.UseNpgsql(connectionString);
    }

    private sealed class ProfileContext(string connectionString) : EntityDataContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder builder) => builder.UseNpgsql(connectionString);
    }
}
