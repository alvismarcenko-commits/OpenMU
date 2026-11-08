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
              login text NOT NULL UNIQUE, operation uuid NOT NULL UNIQUE, revoked boolean NOT NULL DEFAULT false,
              shared_revision bigint NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS aether_accounts.audit (
              id uuid PRIMARY KEY, owner bigint NOT NULL, action text NOT NULL,
              account_id uuid NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            """).ConfigureAwait(false);
    }

    /// <summary>Adds revision tracking for the verified staging account bridge.</summary>
    public async Task InitializeSharedCredentialsAsync()
    {
        await using var db = new BridgeContext(this.connectionString);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE aether_accounts.links ADD COLUMN IF NOT EXISTS shared_revision bigint NOT NULL DEFAULT 0").ConfigureAwait(false);
    }

    /// <summary>Replays only the same owner, operation and full credentials; never claims an existing account.</summary>
    public Task<string> CreateAsync(long owner, Guid operation, string login, string password)
    {
        if (owner <= 0 || operation == Guid.Empty || !Regex.IsMatch(login, "^[a-z0-9_]{3,10}$")
            || password.Length != 10 || password.Any(c => c < '!' || c > '~'))
        { throw new ArgumentException("Separate game login: 3-10 lowercase ASCII; password: exactly 10 printable non-space ASCII characters. Never truncate."); }
        return this.CreateAsync(owner, operation, login, BCrypt.Net.BCrypt.HashPassword(password), password, 0);
    }

    /// <summary>Creates an account from a verifier derived before Website email verification.</summary>
    public Task<string> CreateSharedAsync(long owner, Guid operation, string login, string passwordHash)
    {
        if (owner <= 0 || operation == Guid.Empty || !Regex.IsMatch(login, "\\A[A-Za-z0-9_]{3,10}\\z") || !IsSharedHash(passwordHash))
        { throw new ArgumentException("Invalid shared account credentials."); }
        return this.CreateAsync(owner, operation, login, passwordHash, null, 1);
    }

    private static bool IsSharedHash(string hash) => Regex.IsMatch(hash, "\\A\\$2b\\$12\\$[./A-Za-z0-9]{53}\\z");

    private async Task<string> CreateAsync(long owner, Guid operation, string login, string passwordHash, string? password, long revision)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await this.CreateOnceAsync(owner, operation, login, passwordHash, password, revision).ConfigureAwait(false);
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
            if (current is PostgresException postgres && postgres.SqlState is "40001" or "40P01" or "23505")
            {
                return true;
            }
        }

        return false;
    }

    private async Task<string> CreateOnceAsync(long owner, Guid operation, string login, string passwordHash, string? password, long revision)
    {
        await using var db = new BridgeContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        // Same owner lock serializes create/revoke, including when no row exists yet.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({owner})").ConfigureAwait(false);
        await using var read = db.Database.GetDbConnection().CreateCommand();
        read.Transaction = tx.GetDbTransaction();
        read.CommandText = "SELECT account_id,login,operation,revoked,shared_revision FROM aether_accounts.links WHERE owner=@owner";
        var op = read.CreateParameter(); op.ParameterName = "owner"; op.Value = owner; read.Parameters.Add(op);
        Guid? existing = null; string existingLogin = ""; Guid existingOperation = Guid.Empty; bool revoked = false; long existingRevision = 0;
        await using (var reader = await read.ExecuteReaderAsync().ConfigureAwait(false))
        {
            if (await reader.ReadAsync().ConfigureAwait(false))
            { existing = reader.GetGuid(0); existingLogin = reader.GetString(1); existingOperation = reader.GetGuid(2); revoked = reader.GetBoolean(3); existingRevision = reader.GetInt64(4); }
        }
        if (existing is { } id)
        {
            var account = await db.Set<Account>().SingleAsync(a => a.Id == id).ConfigureAwait(false);
            var sameVerifier = password is null ? passwordHash == account.PasswordHash : BCrypt.Net.BCrypt.Verify(password, account.PasswordHash);
            if (revoked || existingLogin != login || existingOperation != operation || existingRevision != revision || !sameVerifier)
            { throw new InvalidOperationException("Account identity conflict or revoked link."); }
            await tx.CommitAsync().ConfigureAwait(false);
            return id.ToString();
        }
        var folded = login.ToLowerInvariant();
        if (await db.Set<Account>().AnyAsync(a => a.LoginName.ToLower() == folded).ConfigureAwait(false))
        { throw new InvalidOperationException("Login is already registered; existing accounts cannot be claimed."); }
        var created = new Account { Id = Guid.NewGuid(), LoginName = login,
            PasswordHash = passwordHash, State = DataModel.Entities.AccountState.Normal,
            RegistrationDate = DateTime.UtcNow, SecurityCode = string.Empty, EMail = string.Empty };
        db.Add(created);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO aether_accounts.links(owner,realm,account_id,login,operation,shared_revision) VALUES({owner},'ascension',{created.Id},{login},{operation},{revision})").ConfigureAwait(false);
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
        var changed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE aether_accounts.links SET revoked=true WHERE owner={owner} AND account_id={accountId} AND NOT revoked AND shared_revision=0").ConfigureAwait(false);
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

    /// <summary>Returns a bounded Ascension ranking of active, Website-linked staging characters.</summary>
    public async Task<object> RankingsAsync(Guid level, Guid resets, Guid grandResets)
    {
        await using var db = new ProfileContext(this.connectionString);
        var characters = await db.Set<Character>().FromSqlInterpolated($"""
                SELECT c.* FROM data."Character" c
                JOIN data."Account" a ON a."Id" = c."AccountId"
                JOIN aether_accounts.links l ON l.account_id = a."Id"
                WHERE NOT l.revoked AND a."State" = {(int)DataModel.Entities.AccountState.Normal}
                ORDER BY c."Experience" DESC, c."Name", c."Id"
                LIMIT 25
                """)
            .AsNoTracking().Include(c => c.RawCharacterClass).Include(c => c.RawAttributes)
            .AsSplitQuery().ToArrayAsync().ConfigureAwait(false);
        var items = characters.OrderByDescending(c => c.Experience).ThenBy(c => c.Name, StringComparer.Ordinal)
            .ThenBy(c => c.Id).Select(c => new
            {
                name = c.Name,
                character_class = c.RawCharacterClass?.Name.ToString(),
                level = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == level)?.Value,
                resets = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == resets)?.Value ?? 0,
                grand_resets = (int?)c.RawAttributes.SingleOrDefault(a => a.DefinitionId == grandResets)?.Value ?? 0,
                experience = c.Experience,
            }).ToArray();
        return new { realm = "ascension", items };
    }

    /// <summary>Replaces credentials of an active linked account and records the change atomically.</summary>
    public Task<bool> ChangePasswordAsync(long owner, Guid accountId, string password)
    {
        if (password.Length != 10 || password.Any(c => c < '!' || c > '~'))
        {
            throw new ArgumentException("Game password must be exactly ten printable non-space ASCII characters.");
        }

        return this.ChangePasswordHashAsync(owner, accountId, BCrypt.Net.BCrypt.HashPassword(password), 0);
    }

    public Task<bool> ChangeSharedPasswordAsync(long owner, Guid accountId, string passwordHash, long revision)
    {
        if (revision < 2 || !IsSharedHash(passwordHash)) { throw new ArgumentException("Invalid credential revision."); }
        return this.ChangePasswordHashAsync(owner, accountId, passwordHash, revision);
    }

    private async Task<bool> ChangePasswordHashAsync(long owner, Guid accountId, string passwordHash, long revision)
    {
        await using var db = new BridgeContext(this.connectionString);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({owner})").ConfigureAwait(false);
        var revisions = await db.Database.SqlQuery<long>($"SELECT shared_revision AS \"Value\" FROM aether_accounts.links WHERE owner={owner} AND account_id={accountId} AND NOT revoked").ToListAsync().ConfigureAwait(false);
        if (revisions.Count != 1)
        {
            return false;
        }

        var account = await db.Set<Account>().SingleAsync(a => a.Id == accountId).ConfigureAwait(false);
        if (account.State != DataModel.Entities.AccountState.Normal)
        {
            return false;
        }

        var current = revisions[0];
        if (revision > 0 && current == revision) { return account.PasswordHash == passwordHash; }
        if ((revision == 0 && current != 0) || (revision > 0 && (current == 0 || revision - current != 1))) { return false; }
        account.PasswordHash = passwordHash;
        await db.SaveChangesAsync().ConfigureAwait(false);
        if (revision > 0)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE aether_accounts.links SET shared_revision={revision} WHERE owner={owner} AND account_id={accountId}").ConfigureAwait(false);
        }
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
