// Licensed under MIT. Read-only local staging identity and persistence evidence.
namespace MUnique.OpenMU.Persistence.EntityFramework;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

public static class AetherStagingState
{
    /// <summary>Gets the dedicated gameplay candidate profile flag.</summary>
    public static bool IsCompletePlayable => Environment.GetEnvironmentVariable("AETHER_STAGE_PROFILE") == "complete-playable";

    /// <summary>Gets the listener offset from the original staging ports.</summary>
    public static int PortOffset => Environment.GetEnvironmentVariable("AETHER_STAGE_PROFILE") switch
    {
        "first-playable" => 1000,
        "complete-playable" => 2000,
        _ => 0,
    };

    public static string Nonce(string dsn)
    {
        var b = new NpgsqlConnectionStringBuilder(dsn);
        const string prefix = "aether_playable_ascension_";
        if (b.Host != "127.0.0.1" || b.Database is null || !Regex.IsMatch(b.Database, "^" + prefix + "[a-f0-9]{32}$"))
        { throw new InvalidOperationException("Dedicated loopback Ascension database required."); }
        return b.Database[prefix.Length..];
    }

    public static async Task VerifyOwnerAsync(string dsn)
    {
        var nonce = Nonce(dsn);
        await using var connection = new NpgsqlConnection(dsn);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT current_database(), shobj_description(oid,'pg_database') FROM pg_database WHERE datname=current_database()", connection);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetString(0) != "aether_playable_ascension_" + nonce
            || (await reader.IsDBNullAsync(1).ConfigureAwait(false)) || reader.GetString(1) != "aether-ascension:" + nonce)
        { throw new InvalidOperationException("Staging database ownership marker mismatch."); }
    }

    public static async Task<object> ReadAsync(string dsn, bool fingerprint)
    {
        await VerifyOwnerAsync(dsn).ConfigureAwait(false);
        var nonce = Nonce(dsn);
        await using var connection = new NpgsqlConnection(dsn);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand("""
            SELECT
             (SELECT nonce FROM aether_staging.instance WHERE singleton=true),
             (SELECT count(*) FROM config."GameConfiguration" WHERE "ExperienceRate"=100 AND "MasterExperienceRate"=100),
             (SELECT count(*) FROM config."GameServerDefinition" WHERE "ExperienceRate"=1),
             (SELECT count(*) FROM config."ConnectServerDefinition" WHERE "ClientListenerPort"=@connect),
             (SELECT count(*) FROM config."GameServerEndpoint" WHERE "NetworkPort"=@game),
             (SELECT count(*) FROM aether_prestige.rules WHERE app_per_gr=100 AND revision>=2 AND activated=(@playable AND realm='ascension')
                AND resets_required=(CASE realm WHEN 'origin' THEN 25 WHEN 'legacy' THEN 50 WHEN 'evolution' THEN 75 WHEN 'ascension' THEN 100 END)),
             (SELECT count(*) FROM data."Character"),
             (SELECT count(*) FROM config."SystemConfiguration" WHERE "ReadConsoleInput" AND "AutoStart" AND NOT "AutoUpdateSchema" AND "IpResolverParameter"='127.0.0.1')
            """, connection);
        var offset = PortOffset;
        command.Parameters.AddWithValue("playable", IsCompletePlayable);
        command.Parameters.AddWithValue("connect", 44406 + offset);
        command.Parameters.AddWithValue("game", 45902 + offset);
        long characters;
        await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
        {
            if (!await reader.ReadAsync().ConfigureAwait(false) || reader.GetString(0) != nonce
                || reader.GetInt64(1) != 1 || reader.GetInt64(2) != 1 || reader.GetInt64(3) != 1
                || reader.GetInt64(4) != 1 || reader.GetInt64(5) != 4 || reader.GetInt64(7) != 1)
            { throw new InvalidOperationException("Native Ascension configuration or Prestige rules mismatch."); }
            characters = reader.GetInt64(6);
        }
        string? digest = null;
        if (fingerprint)
        {
            // Fixed pinned native tables only; no caller-supplied SQL identifiers or player data output.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (schema, table) in new[] { ("data", "Character"), ("data", "StatAttribute"), ("data", "Item"), ("data", "ItemStorage"), ("data", "Account"), ("aether_accounts", "links"), ("aether_accounts", "audit") })
            {
                await using var snapshot = new NpgsqlCommand($"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]'::jsonb)::text FROM \"{schema}\".\"{table}\" t", connection);
                var value = (string)(await snapshot.ExecuteScalarAsync().ConfigureAwait(false))!;
                hash.AppendData(Encoding.UTF8.GetBytes(schema + "." + table + ":" + value + "\n"));
            }
            digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        return new { status = "READY", instance = nonce, realm = "ascension", character_count = characters,
            progress_fingerprint = digest, connect_host = "127.0.0.1", connect_port = 44406 + offset, game_port = 45902 + offset,
            exp = 100, master_exp = 100, app_per_gr = 100, stat_bonus = 0, rates_approved = false, native_enabled = IsCompletePlayable,
            client_acceptance = "MANUAL REQUIRED" };
    }
}
